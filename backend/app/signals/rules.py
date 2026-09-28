"""Deterministic signal rules. Each rule reads stored facts and emits evidence-backed signals.

To add a rule: write a class with `name` and `evaluate(facts, today)`, append it to RULES.
"""
from datetime import date, timedelta

from app.collectors.fmcsa import parse_date, parse_int
from app.signals.base import Fact, SignalDraft

WINDOW = timedelta(days=365)


def _recent(facts: list[Fact], record_type: str, today: date) -> list[Fact]:
    return [f for f in facts if f.record_type == record_type and f.observed_at and today - f.observed_at <= WINDOW]


def _num(f: Fact, key: str) -> int:
    return parse_int(f.payload.get(key)) or 0


def _pick(f: Fact, *keys: str) -> dict:
    return {k: f.payload.get(k) for k in keys if k in f.payload}


INSPECTION_FIELDS = ("inspection_id", "insp_date", "report_state", "insp_level_id", "viol_total", "oos_total",
                     "driver_oos_total", "vehicle_oos_total", "hazmat_oos_total")


def _draft(rule: str, type_: str, f: Fact, description: str, severity: str, evidence: dict) -> SignalDraft:
    return SignalDraft(type=type_, description=description, severity=severity, source=f.source,
                       source_url=f.source_url, observed_at=f.observed_at, detected_by=rule,
                       dedupe_key=f"{type_}:{f.record_type}:{f.external_id}", evidence=evidence,
                       source_record_id=f.id)


class OutOfServiceRule:
    name = "OutOfServiceRule"

    def evaluate(self, facts, today):
        out = []
        for f in _recent(facts, "inspection", today):
            if (oos := _num(f, "oos_total")) > 0:
                desc = (f"Out-of-service order at {f.observed_at} inspection in {f.payload.get('report_state')}: "
                        f"{_num(f, 'driver_oos_total')} driver / {_num(f, 'vehicle_oos_total')} vehicle OOS, "
                        f"{_num(f, 'viol_total')} violation(s)")
                out.append(_draft(self.name, "OUT_OF_SERVICE", f, desc, "high", _pick(f, *INSPECTION_FIELDS)))
        return out


class InspectionViolationRule:
    name = "InspectionViolationRule"

    def evaluate(self, facts, today):
        out = []
        for f in _recent(facts, "inspection", today):
            if _num(f, "viol_total") > 0 and _num(f, "oos_total") == 0:
                desc = (f"{_num(f, 'viol_total')} violation(s) at {f.observed_at} inspection "
                        f"in {f.payload.get('report_state')}")
                out.append(_draft(self.name, "INSPECTION_VIOLATION", f, desc, "medium", _pick(f, *INSPECTION_FIELDS)))
        return out


class RepeatedViolationRule:
    name = "RepeatedViolationRule"
    min_count = 3

    def evaluate(self, facts, today):
        bad = sorted((f for f in _recent(facts, "inspection", today) if _num(f, "viol_total") > 0),
                     key=lambda f: f.observed_at, reverse=True)
        total = len(_recent(facts, "inspection", today))
        if len(bad) < self.min_count:
            return []
        latest = bad[0]
        desc = f"{len(bad)} of {total} inspections in the last 12 months had violations"
        evidence = {"violating_inspections": len(bad), "total_inspections": total,
                    "inspection_ids": [f.external_id for f in bad[:20]], "latest": str(latest.observed_at)}
        return [SignalDraft(type="REPEATED_VIOLATIONS", description=desc, severity="high", source=latest.source,
                            source_url=latest.source_url, observed_at=latest.observed_at, detected_by=self.name,
                            dedupe_key="REPEATED_VIOLATIONS", evidence=evidence, source_record_id=latest.id)]


class HighOutOfServiceRateRule:
    """Share of inspections ending in an OOS order. Normalizes for fleet size / inspection volume."""

    name = "HighOutOfServiceRateRule"
    min_inspections = 5
    min_rate = 0.30

    def evaluate(self, facts, today):
        insp = _recent(facts, "inspection", today)
        oos = sorted((f for f in insp if _num(f, "oos_total") > 0), key=lambda f: f.observed_at, reverse=True)
        if len(insp) < self.min_inspections or not oos or len(oos) / len(insp) < self.min_rate:
            return []
        rate = len(oos) / len(insp)
        desc = (f"{len(oos)} of {len(insp)} inspections ({rate:.0%}) in the last 12 months ended in an "
                f"out-of-service order (threshold {self.min_rate:.0%})")
        evidence = {"oos_inspections": len(oos), "total_inspections": len(insp), "rate": round(rate, 3),
                    "threshold": self.min_rate, "inspection_ids": [f.external_id for f in oos[:20]]}
        return [SignalDraft(type="HIGH_OOS_RATE", description=desc, severity="high", source=oos[0].source,
                            source_url=oos[0].source_url, observed_at=oos[0].observed_at, detected_by=self.name,
                            dedupe_key="HIGH_OOS_RATE", evidence=evidence, source_record_id=oos[0].id)]


class CrashRule:
    name = "CrashRule"

    def evaluate(self, facts, today):
        out = []
        for f in _recent(facts, "crash", today):
            fatal, injured = _num(f, "fatalities"), _num(f, "injuries")
            severity = "critical" if fatal else "high" if injured else "medium"
            parts = [f"{fatal} fatality(ies)"] * bool(fatal) + [f"{injured} injury(ies)"] * bool(injured)
            if f.payload.get("tow_away") == "Y":
                parts.append("tow-away")
            desc = f"Reportable crash on {f.observed_at} in {f.payload.get('report_state')}" + (
                f": {', '.join(parts)}" if parts else "")
            out.append(_draft(self.name, "CRASH", f, desc, severity, _pick(
                f, "crash_id", "report_date", "report_state", "city", "fatalities", "injuries", "tow_away",
                "hazmat_released", "vehicles_in_accident")))
        return out


def _census(facts: list[Fact]) -> Fact | None:
    return next((f for f in facts if f.record_type == "census"), None)


class NewCarrierRule:
    name = "NewCarrierRule"
    days = 180

    def evaluate(self, facts, today):
        c = _census(facts)
        added = c and parse_date(c.payload.get("add_date"))
        if not added or (today - added).days > self.days:
            return []
        s = _draft(self.name, "NEW_CARRIER", c, f"New carrier: USDOT registered on {added}", "low",
                   _pick(c, "dot_number", "add_date", "status_code", "power_units"))
        s.observed_at, s.dedupe_key = added, "NEW_CARRIER"
        return [s]


class StaleRegistrationRule:
    """MCS-150 must be updated every 24 months; overdue updates can lead to deactivation."""

    name = "StaleRegistrationRule"
    days = 730

    def evaluate(self, facts, today):
        c = _census(facts)
        updated = c and parse_date(c.payload.get("mcs150_date"))
        if not updated or c.payload.get("status_code") != "A" or (today - updated).days <= self.days:
            return []
        s = _draft(self.name, "STALE_MCS150", c,
                   f"MCS-150 registration last updated {updated} ({(today - updated).days // 30} months ago; "
                   "biennial update required)", "medium", _pick(c, "dot_number", "mcs150_date", "status_code"))
        s.observed_at, s.dedupe_key = updated, f"STALE_MCS150:{updated}"  # date of the fact, not of our fetch
        return [s]


class InactiveStatusRule:
    name = "InactiveStatusRule"

    def evaluate(self, facts, today):
        c = _census(facts)
        if not c or c.payload.get("status_code") in (None, "A"):
            return []
        status = c.payload["status_code"]
        s = _draft(self.name, "INACTIVE_STATUS", c, f"USDOT status is '{status}' (not active)", "medium",
                   _pick(c, "dot_number", "status_code"))
        s.dedupe_key = f"INACTIVE_STATUS:{status}"
        return [s]


# Official FMCSA BASIC categories -> signal. min = violations needed in 12 months to fire.
BASIC_SIGNALS = {
    "Hours-of-Service": ("HOS_VIOLATIONS", "hours-of-service", "medium", 1),
    "Vehicle Maintenance": ("MAINTENANCE_VIOLATIONS", "vehicle maintenance", "medium", 3),
    "Unsafe Driving": ("UNSAFE_DRIVING", "unsafe driving", "medium", 1),
    "Driver Fitness": ("DRIVER_FITNESS", "driver fitness", "medium", 1),
    "Controlled Substances": ("DRUG_ALCOHOL", "controlled substances/alcohol", "high", 1),
    "Hazardous Materials": ("HAZMAT_VIOLATIONS", "hazardous materials", "medium", 1),
}


class ViolationCategoryRule:
    """One signal per BASIC category, aggregating the company's violations in the last 12 months."""

    name = "ViolationCategoryRule"

    def evaluate(self, facts, today):
        by_type: dict[str, list[Fact]] = {}
        for f in _recent(facts, "violation", today):
            desc = f.payload.get("basic_desc", "")
            for prefix, (type_, *_rest) in BASIC_SIGNALS.items():
                if desc.startswith(prefix):
                    by_type.setdefault(type_, []).append(f)
        out = []
        for prefix, (type_, label, severity, min_count) in BASIC_SIGNALS.items():
            vs = sorted(by_type.get(type_, []), key=lambda f: f.observed_at, reverse=True)
            if len(vs) < min_count:
                continue
            oos = sum(f.payload.get("oos_indicator") == "true" for f in vs)
            common: dict[str, int] = {}
            for f in vs:
                key = f"{f.payload.get('viol_code')} {f.payload.get('section_desc', '')}".strip()
                common[key] = common.get(key, 0) + 1
            top = sorted(common.items(), key=lambda kv: -kv[1])[:5]
            desc = (f"{len(vs)} {label} violation(s) in the last 12 months"
                    + (f", {oos} out-of-service" if oos else "") + f"; most common: {top[0][0]}")
            evidence = {"basic": vs[0].payload.get("basic_desc"), "violations": len(vs), "oos_violations": oos,
                        "latest": str(vs[0].observed_at), "top_violations": [f"{n}× {k}" for k, n in top],
                        "inspection_ids": list(dict.fromkeys(f.payload.get("unique_id") for f in vs))[:20]}
            out.append(SignalDraft(type=type_, description=desc, severity="high" if oos else severity,
                                   source=vs[0].source, source_url=vs[0].source_url, observed_at=vs[0].observed_at,
                                   detected_by=self.name, dedupe_key=type_, evidence=evidence,
                                   source_record_id=vs[0].id))
        return out


AUTHORITY_FIELDS = ("docket_number", "sub_number", "mod_col_1", "original_action_desc", "orig_served_date",
                    "disp_action_desc", "disp_decided_date", "disp_served_date")


class AuthorityRevokedRule:
    name = "AuthorityRevokedRule"

    def evaluate(self, facts, today):
        out = []
        for f in facts:
            if f.record_type != "authority" or "REVOK" not in (f.payload.get("disp_action_desc") or ""):
                continue
            served = parse_date(f.payload.get("disp_served_date")) or parse_date(f.payload.get("disp_decided_date"))
            if served and (today - served).days <= 365:
                s = _draft(self.name, "AUTHORITY_REVOKED", f,
                           f"Operating authority {f.payload.get('docket_number')} ({f.payload.get('mod_col_1', '').lower()}) "
                           f"{f.payload['disp_action_desc'].lower()} on {served}", "high", _pick(f, *AUTHORITY_FIELDS))
                s.observed_at = served
                out.append(s)
        return out


class NewAuthorityRule:
    name = "NewAuthorityRule"
    days = 180

    def evaluate(self, facts, today):
        out = []
        for f in facts:
            granted = parse_date(f.payload.get("orig_served_date")) if f.record_type == "authority" else None
            if (granted and f.payload.get("original_action_desc") == "GRANTED"
                    and not f.payload.get("disp_action_desc") and (today - granted).days <= self.days):
                s = _draft(self.name, "NEW_AUTHORITY", f,
                           f"New operating authority {f.payload.get('docket_number')} "
                           f"({f.payload.get('mod_col_1', '').lower()}) granted {granted}", "low",
                           _pick(f, *AUTHORITY_FIELDS))
                s.observed_at = granted
                out.append(s)
        return out


INSURANCE_FIELDS = ("docket_number", "mod_col_1", "name_company", "policy_no", "ins_form_code", "effective_date",
                    "cancl_effective_date", "max_cov_amount")


class InsuranceCancellationRule:
    name = "InsuranceCancellationRule"
    past_days, future_days = 30, 90

    def evaluate(self, facts, today):
        out = []
        for f in facts:
            cancel = parse_date(f.payload.get("cancl_effective_date")) if f.record_type == "insurance" else None
            if cancel and -self.past_days <= (cancel - today).days <= self.future_days:
                when = f"in {(cancel - today).days} days" if cancel >= today else f"{(today - cancel).days} days ago"
                s = _draft(self.name, "INSURANCE_CANCELLATION", f,
                           f"{f.payload.get('mod_col_1', 'Insurance')} policy with {f.payload.get('name_company')} "
                           f"has a cancellation effective {cancel} ({when})", "critical" if cancel >= today else "high",
                           _pick(f, *INSURANCE_FIELDS))
                s.observed_at = cancel
                out.append(s)
        return out


def _next_anniversary(start: date, today: date) -> date:
    for year in (today.year, today.year + 1):
        try:
            d = start.replace(year=year)
        except ValueError:  # Feb 29 in a non-leap year
            d = date(year, 2, 28)
        if d >= today:
            return d
    raise AssertionError("unreachable")


class InsuranceRenewalRule:
    """Estimate: BIPD liability policies are typically annual, so the next anniversary is a renewal window."""

    name = "InsuranceRenewalRule"
    window_days = 60

    def evaluate(self, facts, today):
        out = []
        for f in facts:
            if f.record_type != "insurance" or "BIPD" not in (f.payload.get("mod_col_1") or ""):
                continue
            start = parse_date(f.payload.get("effective_date"))
            if not start or start > today or f.payload.get("cancl_effective_date"):
                continue
            nxt = _next_anniversary(start, today)
            if (nxt - today).days <= self.window_days:
                s = _draft(self.name, "INSURANCE_RENEWAL", f,
                           f"Liability policy with {f.payload.get('name_company')} effective {start}; estimated annual "
                           f"renewal {nxt} (in {(nxt - today).days} days)", "low",
                           _pick(f, *INSURANCE_FIELDS) | {"estimated_renewal": str(nxt),
                                                          "assumption": "annual policy term"})
                s.observed_at, s.dedupe_key = start, f"INSURANCE_RENEWAL:{f.external_id}:{nxt}"
                out.append(s)
        return out


class HazmatCarrierRule:
    name = "HazmatCarrierRule"

    def evaluate(self, facts, today):
        c = _census(facts)
        if not c or c.payload.get("hm_ind") != "Y":
            return []
        s = _draft(self.name, "HAZMAT_CARRIER", c, "Registered as a hazardous-materials carrier", "low",
                   _pick(c, "dot_number", "hm_ind"))
        s.dedupe_key = "HAZMAT_CARRIER"
        return [s]


class CensusChangeRule:
    """Changes between two census snapshots (see pipeline.census_changes). Small count moves are noise."""

    name = "CensusChangeRule"
    min_delta, min_ratio = 2, 0.10

    def evaluate(self, facts, today):
        out = []
        for f in _recent(facts, "census_change", today):
            field, old, new = f.payload.get("field"), f.payload.get("from"), f.payload.get("to")
            evidence = _pick(f, "field", "from", "to")
            if field == "status_code":
                if new == "A" and old != "A":
                    out.append(_draft(self.name, "REACTIVATED", f, f"USDOT status changed '{old}' → active", "medium", evidence))
                continue
            before, after = parse_int(old), parse_int(new)
            if before is None or after is None or abs(after - before) < max(self.min_delta, before * self.min_ratio):
                continue
            what = "power units" if field == "power_units" else "drivers"
            desc = f"{what.capitalize()} {'grew' if after > before else 'dropped'} {before} → {after} (FMCSA registration)"
            if field == "power_units":
                out.append(_draft(self.name, "FLEET_GROWTH" if after > before else "FLEET_SHRINK", f, desc,
                                  "medium" if after > before else "low", evidence))
            elif field == "total_drivers" and after > before:
                out.append(_draft(self.name, "DRIVER_GROWTH", f, desc, "medium", evidence))
        return out


class HiringRule:
    """Hiring language on the company's own website (recorded during enrichment)."""

    name = "HiringRule"
    days = 90

    def evaluate(self, facts, today):
        return [_draft(self.name, "HIRING_DRIVERS", f, f"Website says “{f.payload.get('phrase')}”", "medium",
                       _pick(f, "phrase", "page"))
                for f in facts if f.record_type == "hiring" and f.observed_at and (today - f.observed_at).days <= self.days]


RULES = [OutOfServiceRule(), InspectionViolationRule(), RepeatedViolationRule(), HighOutOfServiceRateRule(),
         CrashRule(), NewCarrierRule(), StaleRegistrationRule(), InactiveStatusRule(), ViolationCategoryRule(),
         AuthorityRevokedRule(), NewAuthorityRule(), InsuranceCancellationRule(), InsuranceRenewalRule(),
         HazmatCarrierRule(), CensusChangeRule(), HiringRule()]


def detect(facts: list[Fact], today: date) -> list[SignalDraft]:
    return [s for rule in RULES for s in rule.evaluate(facts, today)]
