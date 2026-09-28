from datetime import date, timedelta

import httpx
from sqlalchemy import func, select

from app import scoring
from app.collectors.base import NormalizedBatch
from app.collectors.fmcsa import FmcsaCollector, RawFmcsa, parse_date
from app.models import Company, Contact, Lead, LeadEvent, ScoringRule, Signal, SourceRecord
from app.pipeline import ingest, process_companies
from app.services_catalog import lines_for, pitch
from app.signals.base import Fact
from app.signals.rules import _next_anniversary, detect

TODAY = date(2026, 9, 27)
COLLECTOR = FmcsaCollector(client=httpx.Client())  # no network used by normalize()


def ago(days: int, fmt: str = "%Y%m%d") -> str:
    return (TODAY - timedelta(days=days)).strftime(fmt).upper()


CENSUS = {"dot_number": "123", "legal_name": "ACME FREIGHT LLC", "phy_state": "TX", "phy_city": "DALLAS",
          "power_units": "12", "total_drivers": "14", "status_code": "A", "add_date": ago(60),
          "mcs150_date": ago(900), "phone": "(214) 555-0100", "email_address": "Ops@AcmeFreight.com", "hm_ind": "N",
          "company_officer_1": "JOHN Q DOE", "cell_phone": "(214) 555-0111", "fax": "214-555-0122",
          "carrier_mailing_street": "PO BOX 9", "carrier_mailing_city": "DALLAS", "carrier_mailing_state": "TX",
          "carrier_mailing_zip": "75201"}


def insp(i: int, days_ago: int, viol: int, oos: int) -> dict:
    return {"inspection_id": str(i), "dot_number": "123", "insp_date": ago(days_ago), "report_state": "TX",
            "viol_total": str(viol), "oos_total": str(oos), "driver_oos_total": "0", "vehicle_oos_total": str(oos)}


def viol(uid: str, days_ago: int, basic: str, code: str, oos: bool = False) -> dict:
    return {"unique_id": uid, "dot_number": "123", "insp_date": ago(days_ago, "%d-%b-%y"), "basic_desc": basic,
            "viol_code": code, "section_desc": f"desc {code}", "oos_indicator": str(oos).lower(), "viol_unit": "D"}


RAW = RawFmcsa(
    census=[CENSUS, {"dot_number": "abc", "legal_name": "BAD ROW"}],
    inspections=[insp(1, 5, 2, 1), insp(2, 20, 1, 1), insp(3, 40, 3, 1), insp(4, 50, 1, 0), insp(5, 70, 0, 0),
                 insp(6, 400, 5, 2),  # outside 12-month window
                 {**insp(7, 3, 1, 0), "dot_number": "999"}],  # unknown company -> dropped
    crashes=[{"crash_id": "c1", "dot_number": "123", "report_date": ago(10), "report_state": "OK",
              "fatalities": "1", "injuries": "0", "tow_away": "Y"}],
    violations=[viol("u1", 5, "Hours-of-Service Compliance", "3958A"), viol("u2", 20, "Hours-of-Service Compliance", "3958A"),
                viol("u1", 5, "Vehicle Maintenance", "39347"),  # 1 < min 3 -> no maintenance signal
                viol("u3", 40, "Controlled Substances/&#8203;Alcohol", "3924", oos=True)],
    authority=[{"docket_number": "MC999", "dot_number": "00000123", "sub_number": "0",
                "mod_col_1": "MOTOR PROPERTY COMMON CARRIER", "original_action_desc": "GRANTED",
                "orig_served_date": ago(40, "%m/%d/%Y")}],
    insurance=[{"docket_number": "MC999", "dot_number": "00000123", "mod_col_1": "BIPD/Primary", "name_company": "ACME INS",
                "policy_no": "P1", "ins_form_code": "91X", "effective_date": ago(345, "%m/%d/%Y")},
               {"docket_number": "MC999", "dot_number": "00000123", "mod_col_1": "BIPD/Primary", "name_company": "OLD INS",
                "policy_no": "P2", "ins_form_code": "91X", "effective_date": ago(300, "%m/%d/%Y"),
                "cancl_effective_date": ago(-10, "%m/%d/%Y")}],
)


def batch() -> NormalizedBatch:
    return COLLECTOR.normalize(RAW)


def facts(b: NormalizedBatch) -> list[Fact]:
    return [Fact(None, e.source, e.record_type, e.external_id, e.observed_at, e.source_url, e.payload) for e in b.events]


def test_parse_date_formats():
    assert parse_date("20260927 2140") == parse_date("09/27/2026") == parse_date("27-SEP-26") == date(2026, 9, 27)
    assert parse_date("") is None and parse_date("garbage") is None


def test_normalize():
    b = batch()
    assert [c.dot_number for c in b.companies] == ["123"]
    c = b.companies[0]
    assert (c.state, c.fleet_size, c.operating_status, c.mc_number) == ("TX", 12, "A", "MC999")
    assert {(x.type, x.value, x.label) for x in c.contacts} == {
        ("phone", "2145550100", "Registered phone"), ("email", "ops@acmefreight.com", "Registered email"),
        ("person", "John Q Doe", "Company officer"), ("phone", "2145550111", "Registered cell phone"),
        ("fax", "2145550122", "Registered fax"), ("address", "PO BOX 9, DALLAS, TX 75201", "Mailing address")}
    counts = {t: sum(e.record_type == t for e in b.events) for t in ("inspection", "violation", "authority", "insurance")}
    assert counts == {"inspection": 6, "violation": 4, "authority": 1, "insurance": 2}
    assert all(e.source_url.startswith("https://data.transportation.gov/") for e in b.events)


def test_signal_rules():
    sigs = detect(facts(batch()), TODAY)
    types = sorted(s.type for s in sigs)
    assert types == sorted(["OUT_OF_SERVICE"] * 3 + [
        "INSPECTION_VIOLATION", "REPEATED_VIOLATIONS", "HIGH_OOS_RATE", "CRASH", "NEW_CARRIER", "STALE_MCS150",
        "HOS_VIOLATIONS", "DRUG_ALCOHOL", "NEW_AUTHORITY", "INSURANCE_CANCELLATION", "INSURANCE_RENEWAL"])
    by = {s.type: s for s in sigs}
    assert by["CRASH"].severity == "critical" and "crash_id=c1" in by["CRASH"].source_url
    assert by["HIGH_OOS_RATE"].evidence["oos_inspections"] == 3 and by["HIGH_OOS_RATE"].evidence["total_inspections"] == 5
    assert by["HOS_VIOLATIONS"].evidence["violations"] == 2
    assert by["DRUG_ALCOHOL"].severity == "high"  # had an OOS violation
    assert by["INSURANCE_CANCELLATION"].severity == "critical" and "OLD INS" in by["INSURANCE_CANCELLATION"].description
    assert by["INSURANCE_RENEWAL"].evidence["estimated_renewal"] == str(TODAY + timedelta(days=20))
    assert by["STALE_MCS150"].observed_at == TODAY - timedelta(days=900)  # dated by the fact, not the fetch
    assert len({s.dedupe_key for s in sigs}) == len(sigs)
    assert all(s.source_url and s.evidence for s in sigs)


def test_next_anniversary_handles_leap_day():
    assert _next_anniversary(date(2024, 2, 29), date(2026, 1, 10)) == date(2026, 2, 28)
    assert _next_anniversary(date(2025, 3, 1), date(2026, 9, 27)) == date(2027, 3, 1)


def test_pitch_maps_signals_to_service_lines():
    sigs = detect(facts(batch()), TODAY)
    ranked = pitch(sigs)
    keys = [p["key"] for p in ranked]
    assert {"insurance", "compliance", "eld_hos", "driver_files", "new_carrier"} <= set(keys)
    ins = next(p for p in ranked if p["key"] == "insurance")
    assert {r["type"] for r in ins["reasons"]} >= {"INSURANCE_CANCELLATION", "CRASH"}
    driver_oos = next(s for s in sigs if s.type == "OUT_OF_SERVICE")
    driver_oos.evidence = {"driver_oos_total": "1", "vehicle_oos_total": "0"}
    assert lines_for(driver_oos) == ["compliance", "driver_files", "eld_hos"]


def rules(**overrides) -> list[ScoringRule]:
    return [ScoringRule(key=r[0], label=r[1], kind=r[2], weight=overrides.get(r[0], r[3]), params=r[4],
                        enabled=r[5] if len(r) > 5 else True) for r in scoring.DEFAULT_RULES]


def test_scoring_breakdown_and_clamp():
    fatal = scoring.SignalView("CRASH", TODAY - timedelta(days=3), "critical")
    view = scoring.CompanyView("A", 12, [fatal], {"phone"}, ["insurance", "safety_tech", "compliance"])
    points, bd = scoring.score(view, rules(), TODAY)
    assert {b["key"] for b in bd} == {"sig_crash", "sig_crash_injury", "sig_crash_fatal", "hot_now", "multi_service"}
    assert points == sum(b["points"] for b in bd) == 50  # disabled rules (active, fleet, contact) don't count
    assert scoring.score(view, rules(sig_crash=500), TODAY)[0] == 100
    minor = scoring.CompanyView("A", 12, [scoring.SignalView("CRASH", TODAY - timedelta(days=90), "medium")], set())
    assert {b["key"] for b in scoring.score(minor, rules(), TODAY)[1]} == {"sig_crash"}
    oos = [scoring.SignalView("OUT_OF_SERVICE", TODAY - timedelta(days=60), "high")] * 3
    assert "sig_oos_pattern" in {b["key"] for b in scoring.score(scoring.CompanyView("A", 12, oos, set()), rules(), TODAY)[1]}
    inactive = scoring.CompanyView("I", 12, [scoring.SignalView("INACTIVE_STATUS", None)], set())
    assert scoring.score(inactive, rules(), TODAY)[0] == 0


def test_pipeline_is_idempotent(db):
    for _ in range(2):
        process_companies(db, ingest(db, batch()), TODAY)
    count = lambda m: db.scalar(select(func.count()).select_from(m))  # noqa: E731
    assert (count(SourceRecord), count(Contact), count(Lead)) == (15, 6, 1)
    assert db.scalar(select(Company.mc_number)) == "MC999"
    n_signals = count(Signal)
    assert [e.event_type for e in db.scalars(select(LeadEvent))] == ["CREATED"]
    lead = db.scalar(select(Lead))
    assert lead.service_lines[0] in ("insurance", "compliance") and "eld_hos" in lead.service_lines

    # new fact arrives -> one new signal, one SIGNALS_ADDED event, nothing duplicated
    b = batch()
    b.events += COLLECTOR.normalize(RawFmcsa(census=[CENSUS], inspections=[insp(8, 1, 4, 0)])).events
    process_companies(db, ingest(db, b), TODAY)
    assert count(Signal) == n_signals + 1
    assert "SIGNALS_ADDED" in {e.event_type for e in db.scalars(select(LeadEvent))}


def test_census_changes_become_signals(db):
    process_companies(db, ingest(db, batch()), TODAY)
    grown = RawFmcsa(census=[CENSUS | {"power_units": "20", "total_drivers": "22"}])
    for _ in range(2):  # same snapshot twice -> one change record per field
        process_companies(db, ingest(db, COLLECTOR.normalize(grown)), TODAY)
    changes = db.scalars(select(SourceRecord).where(SourceRecord.record_type == "census_change")).all()
    assert sorted((c.payload["field"], c.payload["from"], c.payload["to"]) for c in changes) == [
        ("power_units", "12", "20"), ("total_drivers", "14", "22")]
    types = set(db.scalars(select(Signal.type)))
    assert {"FLEET_GROWTH", "DRIVER_GROWTH"} <= types


def change(field: str, old: str, new: str, days_ago: int = 5) -> Fact:
    return Fact(None, "fmcsa", "census_change", f"123:{field}:{old}->{new}", TODAY - timedelta(days=days_ago),
                "https://x", {"field": field, "from": old, "to": new})


def test_change_rules():
    sigs = {s.type: s for s in detect([change("power_units", "10", "12"), change("total_drivers", "10", "11"),
                                       change("status_code", "I", "A")], TODAY)}
    assert set(sigs) == {"FLEET_GROWTH", "REACTIVATED"}  # +1 driver is noise
    assert sigs["FLEET_GROWTH"].evidence == {"field": "power_units", "from": "10", "to": "12"}
    assert {s.type for s in detect([change("power_units", "40", "30")], TODAY)} == {"FLEET_SHRINK"}
    assert detect([change("power_units", "10", "20", days_ago=400)], TODAY) == []  # older than 12 months
    hiring = Fact(None, "website", "hiring", "https://a.com/", TODAY - timedelta(days=10), "https://a.com/",
                  {"phrase": "Now hiring CDL drivers", "page": "https://a.com/"})
    assert [s.type for s in detect([hiring], TODAY)] == ["HIRING_DRIVERS"]


def test_motus_registration_and_insurance_signals():
    def f(record_type: str, ext: str, **payload) -> Fact:
        return Fact(None, "fmcsa", record_type, ext, None, None, {"docket_number": "MC70887654", **payload})

    prop = "Motor Carrier of Property (Except Household Goods)"
    got = lambda fs: {s.type: s for s in detect(fs, TODAY)}  # noqa: E731

    # granted and still active -> NEW_AUTHORITY
    granted = f("authority_status", "g", op_auth_type=prop, op_auth_status="Active", reason="Granted",
                status_change_date=ago(2))
    assert "NEW_AUTHORITY" in got([granted])

    # pending, $750k BIPD required, nothing filed -> INSURANCE_NEEDED; once filed -> nothing
    pending = f("authority_status", "p", op_auth_type=prop, op_auth_status="Pending", reason="Published to FMCSA Register",
                status_change_date=ago(5))
    filings = f("authority_filings", "c", op_auth_type=prop, op_auth_status="Pending", min_cov_amount="750000.00",
                bipd_file="0.000000000000000000")
    assert got([pending, filings])["INSURANCE_NEEDED"].severity == "high"
    assert "INSURANCE_NEEDED" not in got([pending, Fact(
        None, "fmcsa", "authority_filings", "c", None, None, {**filings.payload, "bipd_file": "750000.00"})])

    # suspended for lapsed insurance -> INSURANCE_SUSPENDED, unless reinstated afterwards
    susp = f("authority_status", "s", op_auth_type=prop, op_auth_status="Inactive", status_change_date=ago(1),
             reason="Involuntary Suspension - insurance cancellation effective; no active insurance meeting minimum "
                    "coverage on file")
    assert got([granted, susp])["INSURANCE_SUSPENDED"].severity == "critical"
    back = f("authority_status", "r", op_auth_type=prop, op_auth_status="Active", status_change_date=ago(0),
             reason="Reinstated - insurance coverage restored")
    assert "INSURANCE_SUSPENDED" not in got([granted, susp, back])
    main_mc = Fact(None, "fmcsa", "authority_filings", "m", None, None, {  # only an old second docket lapsed
        "docket_number": "MC138732", "op_auth_type": prop, "op_auth_status": "Active", "bipd_file": "1000000.00"})
    assert "INSURANCE_SUSPENDED" not in got([granted, susp, main_mc])

    # suspension notice with a future effective date -> SUSPENSION_NOTICE, resolved by a newer insurance filing
    notice = f("authority_order", "n", docket_number="MC-70887654", op_auth_type=prop, order1_serve_date=ago(10),
               order1_type_desc="Operating Authority Involuntary Suspension Notice", order1_effective_date=ago(-20))
    assert "SUSPENSION_NOTICE" in got([granted, notice])
    new_policy = f("insurance", "i2", ins_type_code="1", policy_no="NEW", effective_date=ago(4),
                   insurance_company_name="NEW INS")
    assert "SUSPENSION_NOTICE" not in got([granted, notice, new_policy])

    # Motus cancellations: CANCEL fires, TERM/REPL (replaced) doesn't, and a newer same-coverage policy clears it
    cancel = f("insurance_history", "h1", filing_status_reason="CANCEL", ins_type_desc="BIPD", ins_type_code="1",
               policy_no="OLD", effective_date=ago(300), cancl_effective_date=ago(-15), insurance_company_name="OLD INS")
    assert "OLD INS" in got([cancel])["INSURANCE_CANCELLATION"].description
    assert "INSURANCE_CANCELLATION" not in got([Fact(None, "fmcsa", "insurance_history", "h2", None, None,
                                                     {**cancel.payload, "filing_status_reason": "TERM/REPL"})])
    assert "INSURANCE_CANCELLATION" not in got([cancel, new_policy])
