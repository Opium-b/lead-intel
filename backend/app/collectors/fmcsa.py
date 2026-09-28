"""FMCSA open data (data.transportation.gov, Socrata). Public, no auth required.

Discovery is problem-first: recent inspections with violations + newly registered
carriers -> census profile -> 12 months of inspections, crashes, violations by category,
operating-authority history and active/pending insurance.
"""
import logging
import time
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta
from typing import Any

import httpx

from app.collectors.base import CompanyRecord, ContactRecord, EventRecord, NormalizedBatch
from app.config import get_settings

log = logging.getLogger(__name__)

BASE = "https://data.transportation.gov/resource"
CENSUS = "az4n-8mr2"
INSPECTIONS = "fx4q-ay7w"
CRASHES = "aayw-vxb3"
VIOLATIONS = "8mt8-2mdr"  # SMS input: violations with their official BASIC category (monthly, 24 months)
AUTHORITY = "9mw4-x3tu"  # AuthHist: MC dockets, grants, revocations (DOT zero-padded to 8)
INSURANCE = "qh9u-swkp"  # ActPendInsur: active/pending insurance filings incl. cancellations (DOT padded)
# FMCSA's new registration system (Motus). New carriers only appear here. DOT not padded, dates YYYYMMDD.
MOTUS_AUTHORITY = "yu5v-wbh6"  # authority status changes (pending, granted, suspended, revoked)
MOTUS_ORDERS = "wb4f-neki"  # revocation / suspension notices with effective dates
MOTUS_INSURANCE = "c5y8-a4uz"  # current insurance filings
MOTUS_INSURANCE_HISTORY = "3uet-3z4i"  # past filings incl. cancellations
HISTORY_DAYS = 365
NEW_CARRIER_DAYS = 180
CHUNK = 100  # DOT numbers per IN (...) query
PAGE = 10_000

SOURCE = "fmcsa"
DATE_FORMATS = ("%Y%m%d", "%m/%d/%Y", "%d-%b-%y")


def parse_date(v: str | None) -> date | None:
    """FMCSA uses 'YYYYMMDD[ HHMM]', 'MM/DD/YYYY' and 'DD-MON-YY' depending on the dataset."""
    v = (v or "").strip()
    for fmt, n in zip(DATE_FORMATS, (8, 10, 9)):
        try:
            return datetime.strptime(v[:n], fmt).date()
        except ValueError:
            continue
    return None


def parse_int(v: str | None) -> int | None:
    try:
        return int(v) if v not in (None, "") else None
    except ValueError:
        return None


def row_url(dataset: str, key: str, value: str) -> str:
    return f"{BASE}/{dataset}.json?{key}={value}"


def _in(values: list[str]) -> str:
    return ",".join(f"'{v}'" for v in values)  # values are validated digit strings


def _pad(dot: str) -> str:
    return dot.zfill(8)


def _unpad(dot: str | None) -> str:
    return str(int(dot)) if dot and dot.isdigit() else ""


@dataclass
class RawFmcsa:
    census: list[dict] = field(default_factory=list)
    inspections: list[dict] = field(default_factory=list)
    crashes: list[dict] = field(default_factory=list)
    violations: list[dict] = field(default_factory=list)
    authority: list[dict] = field(default_factory=list)
    insurance: list[dict] = field(default_factory=list)
    motus_authority: list[dict] = field(default_factory=list)
    motus_orders: list[dict] = field(default_factory=list)
    motus_insurance: list[dict] = field(default_factory=list)
    motus_insurance_history: list[dict] = field(default_factory=list)


class FmcsaCollector:
    name = SOURCE

    def __init__(self, client: httpx.Client | None = None):
        s = get_settings()
        headers = {"User-Agent": "leadintel/0.1 (lead research; public FMCSA data)"}
        if s.socrata_app_token:
            headers["X-App-Token"] = s.socrata_app_token
        self.client = client or httpx.Client(timeout=120, headers=headers)
        self.states = [st.upper() for st in s.collect_states if st.isalpha() and len(st) == 2]
        self.min_fleet, self.max_fleet = s.target_min_fleet, s.target_max_fleet
        self.new_min_fleet = s.target_new_carrier_min_fleet
        self.active_only = s.target_active_only

    def _in_target(self, r: dict) -> bool:
        if self.active_only and r.get("status_code") != "A":
            return False
        units = parse_int(r.get("power_units")) or 0
        added = parse_date(r.get("add_date"))
        is_new = added is not None and (date.today() - added).days <= NEW_CARRIER_DAYS
        return (self.new_min_fleet if is_new else self.min_fleet) <= units <= self.max_fleet

    def _get(self, dataset: str, where: str, limit: int | None = None, order: str = ":id",
             select: str | None = None) -> list[dict]:
        """Fetch all matching rows (paged), or at most `limit`."""
        rows: list[dict] = []
        while True:
            size = PAGE if limit is None else min(PAGE, limit - len(rows))
            params = {"$where": where, "$limit": str(size), "$offset": str(len(rows)), "$order": order}
            if select:
                params["$select"] = select
            for attempt in range(4):
                r = self.client.get(f"{BASE}/{dataset}.json", params=params)
                if r.status_code in (429, 500, 502, 503, 504) and attempt < 3:
                    time.sleep(2**attempt * 2)
                    continue
                r.raise_for_status()
                break
            page = r.json()
            rows += page
            if len(page) < size or (limit is not None and len(rows) >= limit):
                return rows

    def _state_filter(self, column: str) -> str:
        return f" AND {column} in ({_in(self.states)})" if self.states else ""

    def fetch(self, since: date, limit: int) -> RawFmcsa:
        since_s = since.strftime("%Y%m%d")
        # ponytail: newest-first with a hard cap; nationwide volume exceeds one run's cap, so
        # older rows in a busy window are skipped. Raise --limit or add COLLECT_STATES for coverage.
        flagged = self._get(
            INSPECTIONS,
            f"insp_date >= '{since_s}' AND viol_total != '0' AND dot_number != '0'"
            + self._state_filter("insp_carrier_state"),
            limit, order="insp_date DESC", select="dot_number",
        )
        new_carriers = self._get(
            CENSUS,
            f"add_date >= '{since_s}' AND status_code = 'A'" + self._state_filter("phy_state"),
            max(limit // 4, 1), order="add_date DESC", select="dot_number",
        )
        dots = list(dict.fromkeys(r["dot_number"] for r in flagged + new_carriers if r.get("dot_number", "").isdigit()))
        log.info("fmcsa discovery: %d flagged inspections, %d new carriers, %d unique DOTs",
                 len(flagged), len(new_carriers), len(dots))
        return self.fetch_companies(dots)

    def fetch_companies(self, dots: list[str], apply_target: bool = True) -> RawFmcsa:
        """Profile + full recent history for specific DOT numbers.

        Discovery applies the target profile; refresh doesn't, so tracked companies that change
        (e.g. go inactive) are still updated.
        """
        raw = RawFmcsa()
        for i in range(0, len(dots), CHUNK):
            raw.census += [r for r in self._get(CENSUS, f"dot_number in ({_in(dots[i : i + CHUNK])})")
                           if not apply_target or self._in_target(r)]
        targets = [r["dot_number"] for r in raw.census]
        log.info("fmcsa profiles: kept %d of %d DOTs", len(targets), len(dots))

        since = (date.today() - timedelta(days=HISTORY_DAYS)).strftime("%Y%m%d")
        for i in range(0, len(targets), CHUNK):
            chunk = targets[i : i + CHUNK]
            plain, padded = _in(chunk), _in([_pad(d) for d in chunk])
            raw.inspections += self._get(INSPECTIONS, f"dot_number in ({plain}) AND insp_date >= '{since}'")
            raw.crashes += self._get(CRASHES, f"dot_number in ({plain}) AND report_date >= '{since}'")
            raw.violations += self._get(VIOLATIONS, f"dot_number in ({plain})")
            raw.authority += self._get(AUTHORITY, f"dot_number in ({padded})")
            raw.insurance += self._get(INSURANCE, f"dot_number in ({padded})")
            raw.motus_authority += self._get(MOTUS_AUTHORITY, f"usdot_number in ({plain})")
            raw.motus_orders += self._get(MOTUS_ORDERS, f"usdot_number in ({plain})")
            raw.motus_insurance += self._get(MOTUS_INSURANCE, f"usdot_number in ({plain})")
            raw.motus_insurance_history += self._get(
                MOTUS_INSURANCE_HISTORY, f"usdot_number in ({plain}) AND cancl_effective_date >= '{since}'")
        return raw

    def normalize(self, raw: RawFmcsa) -> NormalizedBatch:
        companies, events = [], []
        known: set[str] = set()
        mc: dict[str, str] = {}
        for r in raw.authority:  # prefer an MC docket without a disposition (still active)
            dot, docket = _unpad(r.get("dot_number")), r.get("docket_number", "")
            if docket.startswith("MC") and (dot not in mc or not r.get("disp_action_desc")):
                mc[dot] = docket

        for r in sorted(raw.motus_authority, key=lambda r: r.get("status_change_date", "")):
            dot, docket = r.get("usdot_number", ""), (r.get("docket_number") or "").replace("-", "")
            if docket.startswith("MC") and r.get("op_auth_status") in ("Active", "Pending") and dot not in mc:
                mc[dot] = docket

        for r in raw.census:
            dot = r.get("dot_number", "")
            if not dot.isdigit() or not r.get("legal_name"):
                continue
            known.add(dot)
            companies.append(self._company(r, mc.get(dot)))
            events.append(EventRecord(SOURCE, "census", dot, dot, date.today(), row_url(CENSUS, "dot_number", dot), r))

        newest: date | None = None
        for r in raw.inspections:
            d = parse_date(r.get("insp_date"))
            if r.get("dot_number") not in known or not r.get("inspection_id") or not d:
                continue
            newest = max(newest or d, d)
            events.append(EventRecord(SOURCE, "inspection", r["inspection_id"], r["dot_number"], d,
                                      row_url(INSPECTIONS, "inspection_id", r["inspection_id"]), r))
        for r in raw.crashes:
            d = parse_date(r.get("report_date"))
            if r.get("dot_number") not in known or not r.get("crash_id") or not d:
                continue
            events.append(EventRecord(SOURCE, "crash", r["crash_id"], r["dot_number"], d,
                                      row_url(CRASHES, "crash_id", r["crash_id"]), r))
        for r in raw.violations:
            d = parse_date(r.get("insp_date"))
            if r.get("dot_number") not in known or not r.get("unique_id") or not d:
                continue
            ext = f"{r['unique_id']}:{r.get('viol_code')}:{r.get('viol_unit')}"
            events.append(EventRecord(SOURCE, "violation", ext, r["dot_number"], d,
                                      row_url(VIOLATIONS, "unique_id", r["unique_id"]), r))
        for r in raw.authority:
            dot = _unpad(r.get("dot_number"))
            if dot not in known or not r.get("docket_number"):
                continue
            d = parse_date(r.get("disp_served_date")) or parse_date(r.get("orig_served_date"))
            events.append(EventRecord(SOURCE, "authority", f"{r['docket_number']}:{r.get('sub_number', '0')}", dot, d,
                                      row_url(AUTHORITY, "docket_number", r["docket_number"]), r))
        for r in raw.insurance:
            dot = _unpad(r.get("dot_number"))
            if dot not in known or not r.get("policy_no"):
                continue
            ext = f"{r.get('docket_number')}:{r['policy_no']}:{r.get('ins_form_code')}:{r.get('effective_date')}"
            events.append(EventRecord(SOURCE, "insurance", ext, dot, parse_date(r.get("effective_date")),
                                      row_url(INSURANCE, "dot_number", _pad(dot)), r))

        motus = (("authority_status", raw.motus_authority, MOTUS_AUTHORITY, "status_change_date",
                  ("docket_number", "op_auth_type", "op_auth_status", "status_change_date")),
                 ("authority_order", raw.motus_orders, MOTUS_ORDERS, "order1_serve_date",
                  ("docket_number", "order1_type_desc", "order1_serve_date")),
                 ("insurance", raw.motus_insurance, MOTUS_INSURANCE, "effective_date",
                  ("docket_number", "policy_no", "ins_form_code", "effective_date")),
                 ("insurance_history", raw.motus_insurance_history, MOTUS_INSURANCE_HISTORY, "cancl_effective_date",
                  ("docket_number", "policy_no", "ins_form_code", "effective_date", "cancl_effective_date")))
        for record_type, rows, dataset, date_key, id_keys in motus:
            for r in rows:
                dot = r.get("usdot_number", "")
                if dot not in known:
                    continue
                ext = "motus:" + ":".join(str(r.get(k, "")) for k in id_keys)
                events.append(EventRecord(SOURCE, record_type, ext, dot, parse_date(r.get(date_key)),
                                          row_url(dataset, "usdot_number", dot), r))

        stats = {"companies": len(companies), "events": len(events)}
        return NormalizedBatch(companies, events, newest, stats)

    @staticmethod
    def _company(r: dict[str, Any], mc_number: str | None = None) -> CompanyRecord:
        dot = r["dot_number"]
        url = row_url(CENSUS, "dot_number", dot)
        contacts = []
        digits = lambda k: "".join(c for c in r.get(k) or "" if c.isdigit())  # noqa: E731
        for key, kind, label in (("phone", "phone", "Registered phone"), ("cell_phone", "phone", "Registered cell phone"),
                                 ("fax", "fax", "Registered fax")):
            if len(number := digits(key)) >= 10:
                contacts.append(ContactRecord(kind, number, "fmcsa_census", url, label))
        if (email := (r.get("email_address") or "").strip().lower()) and "@" in email:
            contacts.append(ContactRecord("email", email, "fmcsa_census", url, "Registered email"))
        for key in ("company_officer_1", "company_officer_2"):
            if name := " ".join((r.get(key) or "").split()).title():
                contacts.append(ContactRecord("person", name, "fmcsa_census", url, "Company officer"))
        street, city, st, zip_ = (r.get(k) for k in ("phy_street", "phy_city", "phy_state", "phy_zip"))
        m_street, m_city, m_st, m_zip = (r.get(f"carrier_mailing_{k}") for k in ("street", "city", "state", "zip"))
        if m_street and m_street.strip() != (street or "").strip():  # same as the yard address adds nothing
            mailing = ", ".join(p for p in (m_street.strip(), m_city, f"{m_st or ''} {m_zip or ''}".strip()) if p)
            contacts.append(ContactRecord("address", mailing, "fmcsa_census", url, "Mailing address"))
        return CompanyRecord(
            dot_number=dot,
            name=r["legal_name"].strip(),
            dba_name=(r.get("dba_name") or "").strip() or None,
            mc_number=mc_number,
            state=(st or "")[:2] or None,
            city=city,
            location=", ".join(p for p in (street, city, f"{st or ''} {zip_ or ''}".strip()) if p) or None,
            fleet_size=parse_int(r.get("power_units")),
            drivers=parse_int(r.get("total_drivers")),
            operating_status=r.get("status_code"),
            added_at=parse_date(r.get("add_date")),
            attributes={k: r[k] for k in ("mcs150_date", "carrier_operation", "classdef", "hm_ind",
                                          "business_org_desc", "fleetsize", "mcs150_mileage") if k in r},
            contacts=contacts,
        )
