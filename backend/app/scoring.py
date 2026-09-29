"""Explainable scoring. Weights live in the scoring_rules table; this file only knows *kinds* of factors.

score = clamp(sum of weights of matched rules, 0, 100); every matched rule adds a breakdown line.
To add a new factor kind: add a function to KINDS. To add/tune a rule: insert/update a scoring_rules row.
"""
from dataclasses import dataclass, field
from datetime import date

from sqlalchemy import delete, select
from sqlalchemy.dialects.postgresql import insert
from sqlalchemy.orm import Session

from app.models import ScoringRule

# Tuned for a full-service trucking provider (compliance, drivers, ELD, safety, maintenance, insurance,
# permits/taxes, new-carrier setup, back office). Rows are seeded once; the DB is the source of truth after that.
URGENT = ["OUT_OF_SERVICE", "CRASH", "INSURANCE_CANCELLATION", "AUTHORITY_REVOKED", "INSURANCE_SUSPENDED",
          "SUSPENSION_NOTICE", "INSURANCE_NEEDED"]
DEFAULT_RULES = [
    # key, label, kind, weight, params[, enabled]
    ("sig_new_carrier", "New carrier", "signal_type", 25, {"type": "NEW_CARRIER"}),
    ("sig_insurance_cancel", "Insurance cancellation", "signal_type", 25, {"type": "INSURANCE_CANCELLATION"}),
    ("sig_insurance_suspended", "Suspended for no insurance", "signal_type", 30, {"type": "INSURANCE_SUSPENDED"}),
    ("sig_suspension_notice", "Suspension notice served", "signal_type", 30, {"type": "SUSPENSION_NOTICE"}),
    ("sig_insurance_needed", "Pending authority, no insurance filed", "signal_type", 25, {"type": "INSURANCE_NEEDED"}),
    ("sig_high_oos_rate", "High out-of-service rate", "signal_type", 20, {"type": "HIGH_OOS_RATE"}),
    ("sig_authority_revoked", "Authority revoked", "signal_type", 20, {"type": "AUTHORITY_REVOKED"}),
    ("sig_new_authority", "New operating authority", "signal_type", 15, {"type": "NEW_AUTHORITY"}),
    ("sig_stale_mcs150", "Overdue MCS-150 update", "signal_type", 15, {"type": "STALE_MCS150"}),
    ("sig_drug_alcohol", "Drug & alcohol violations", "signal_type", 15, {"type": "DRUG_ALCOHOL"}),
    ("sig_crash", "Crash on record", "signal_type", 10, {"type": "CRASH"}),
    ("sig_crash_injury", "Injury or fatal crash", "signal_type", 10, {"type": "CRASH", "severity": ["high", "critical"]}),
    ("sig_crash_fatal", "Fatal crash", "signal_type", 10, {"type": "CRASH", "severity": ["critical"]}),
    ("sig_oos_pattern", "3+ out-of-service orders", "signal_type", 10, {"type": "OUT_OF_SERVICE", "min_count": 3}),
    ("sig_repeated", "Repeated violations", "signal_type", 10, {"type": "REPEATED_VIOLATIONS"}),
    ("sig_hos", "Hours-of-service violations", "signal_type", 10, {"type": "HOS_VIOLATIONS"}),
    ("sig_maintenance", "Maintenance violations", "signal_type", 10, {"type": "MAINTENANCE_VIOLATIONS"}),
    ("sig_unsafe_driving", "Unsafe driving violations", "signal_type", 10, {"type": "UNSAFE_DRIVING"}),
    ("sig_driver_fitness", "Driver fitness violations", "signal_type", 10, {"type": "DRIVER_FITNESS"}),
    ("sig_insurance_renewal", "Insurance renewal window", "signal_type", 10, {"type": "INSURANCE_RENEWAL"}),
    ("sig_reactivated", "Reactivated carrier", "signal_type", 15, {"type": "REACTIVATED"}),
    ("sig_reinstated", "Authority reinstated", "signal_type", 20, {"type": "REINSTATED_AUTHORITY"}),
    ("sig_register_published", "Published in FMCSA Register", "signal_type", 20, {"type": "REGISTER_PUBLISHED"}),
    ("sig_name_change", "Company name change", "signal_type", 10, {"type": "NAME_CHANGE"}),
    ("sig_voluntary_suspension", "Voluntary suspension", "signal_type", 5, {"type": "VOLUNTARY_SUSPENSION"}),
    ("sig_fleet_growth", "Growing fleet", "signal_type", 10, {"type": "FLEET_GROWTH"}),
    ("sig_driver_growth", "Adding drivers", "signal_type", 10, {"type": "DRIVER_GROWTH"}),
    ("sig_hiring", "Hiring drivers", "signal_type", 10, {"type": "HIRING_DRIVERS"}),
    ("sig_fleet_shrink", "Shrinking fleet", "signal_type", 5, {"type": "FLEET_SHRINK"}),
    ("hot_now", "Hot right now", "recent_signal", 10, {"days": 14, "types": URGENT}),
    ("multi_service", "Needs 3+ service lines", "service_lines", 10, {"min": 3}),
    ("sig_out_of_service", "Out-of-service order", "signal_type", 5, {"type": "OUT_OF_SERVICE"}),
    ("sig_hazmat_violations", "Hazmat violations", "signal_type", 5, {"type": "HAZMAT_VIOLATIONS"}),
    ("sig_hazmat_carrier", "Hazmat carrier", "signal_type", 5, {"type": "HAZMAT_CARRIER"}),
    ("sig_inactive", "Inactive carrier", "signal_type", -40, {"type": "INACTIVE_STATUS"}),
    # Off by default: every collected company already matches these (they're collection filters).
    ("sig_violation", "Inspection violation", "signal_type", 5, {"type": "INSPECTION_VIOLATION"}, False),
    ("recent_activity", "Recent activity", "recent_signal", 10, {"days": 30}, False),
    ("active_company", "Active carrier", "active_company", 5, {}, False),
    ("target_fleet", "Target fleet size", "fleet_size", 5, {"min": 5, "max": 500}, False),
    ("has_contact", "Contact information", "has_contact", 5, {"types": ["phone", "email"]}, False),
]


@dataclass
class SignalView:
    type: str
    observed_at: date | None
    severity: str = "medium"


@dataclass
class CompanyView:
    operating_status: str | None
    fleet_size: int | None
    signals: list[SignalView]
    contact_types: set[str]
    service_lines: list[str] = field(default_factory=list)


def _signal_type(c: CompanyView, p: dict, today: date) -> str | None:
    hits = [s for s in c.signals if s.type == p["type"] and (not p.get("severity") or s.severity in p["severity"])]
    if len(hits) < p.get("min_count", 1):
        return None
    latest = max((s.observed_at for s in hits if s.observed_at), default=None)
    return f"{len(hits)} signal(s)" + (f", latest {latest}" if latest else "")


def _recent_signal(c, p, today):
    pool = [s for s in c.signals if s.observed_at and (not p.get("types") or s.type in p["types"])]
    latest = max(pool, key=lambda s: s.observed_at, default=None)
    if latest and 0 <= (today - latest.observed_at).days <= p["days"]:
        return f"{latest.type.lower().replace('_', ' ')} on {latest.observed_at} ({(today - latest.observed_at).days}d ago)"


def _service_lines(c, p, today):
    if len(c.service_lines) >= p["min"]:
        return f"{len(c.service_lines)} service lines: {', '.join(c.service_lines)}"


def _active_company(c, p, today):
    return "USDOT status active" if c.operating_status == "A" else None


def _fleet_size(c, p, today):
    if c.fleet_size is not None and p.get("min", 0) <= c.fleet_size <= p.get("max", 10**9):
        return f"{c.fleet_size} power units"


def _has_contact(c, p, today):
    found = sorted(c.contact_types & set(p["types"]))
    return ", ".join(found) + " on file" if found else None


KINDS = {"signal_type": _signal_type, "recent_signal": _recent_signal, "service_lines": _service_lines,
         "active_company": _active_company,
         "fleet_size": _fleet_size, "has_contact": _has_contact}


def score(company: CompanyView, rules: list[ScoringRule], today: date) -> tuple[int, list[dict]]:
    breakdown = []
    for r in rules:
        if r.enabled and (fn := KINDS.get(r.kind)) and (reason := fn(company, r.params, today)):
            breakdown.append({"key": r.key, "label": r.label, "points": r.weight, "reason": reason})
    breakdown.sort(key=lambda b: -b["points"])
    return max(0, min(100, sum(b["points"] for b in breakdown))), breakdown


def seed_rules(db: Session) -> None:
    """Insert default rules that don't exist yet. Never overwrites tuned weights."""
    rows = [dict(key=r[0], label=r[1], kind=r[2], weight=r[3], params=r[4], enabled=r[5] if len(r) > 5 else True)
            for r in DEFAULT_RULES]
    db.execute(insert(ScoringRule).values(rows).on_conflict_do_nothing(index_elements=["key"]))
    db.commit()


def load_rules(db: Session) -> list[ScoringRule]:
    return list(db.scalars(select(ScoringRule).where(ScoringRule.enabled)))


def reset_rules(db: Session) -> None:
    """Replace all rules with DEFAULT_RULES (discards tuned weights)."""
    db.execute(delete(ScoringRule))
    seed_rules(db)
