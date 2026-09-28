"""What we sell, and which detected signals indicate a need for it.

Deterministic mapping: signal type (+ its evidence) -> service lines. Edit here to change what gets pitched.
"""
from collections.abc import Iterable
from typing import Any, Protocol

SERVICE_LINES: dict[str, tuple[str, list[str]]] = {
    "compliance": ("Compliance & audits", [
        "DOT / FMCSA compliance", "DOT audit preparation", "New Entrant Safety Audit support", "CSA / SMS monitoring",
        "CSA score improvement", "DataQs dispute assistance", "Safety program development", "Safety manuals and policies",
        "Compliance document management", "Risk assessment"]),
    "driver_files": ("Driver files & drug testing", [
        "Driver Qualification Files (DQF)", "MVR / driver record checks", "CDL verification",
        "Medical certificate tracking", "Clearinghouse compliance", "Drug & alcohol testing programs",
        "Random drug testing", "SAP coordination"]),
    "eld_hos": ("ELD & hours of service", [
        "ELD setup / installation", "HOS compliance", "Log auditing", "ELD troubleshooting"]),
    "safety_tech": ("Safety tech & training", [
        "Driver safety training", "AI dashcams / video telematics", "Driver behavior monitoring", "GPS / fleet tracking",
        "Telematics", "Fleet analytics", "Accident / incident management"]),
    "maintenance": ("Maintenance & repair", [
        "Preventive maintenance", "Truck / trailer repair coordination", "Tire management", "Vehicle diagnostics",
        "Roadside / breakdown assistance"]),
    "insurance": ("Insurance & claims", [
        "Trucking insurance", "Commercial auto liability", "Motor truck cargo insurance", "Physical damage insurance",
        "General liability", "Bobtail / non-trucking liability", "Trailer interchange", "Workers' compensation",
        "Occupational accident", "Excess / umbrella liability", "Reefer / specialized cargo coverage",
        "Certificate of Insurance (COI) management", "Insurance filings", "Claims assistance",
        "Accident / cargo claims support", "Risk management"]),
    "registration": ("Registration, permits & taxes", [
        "IRP registration & renewal", "IFTA registration & filing", "UCR registration", "2290 / Heavy Vehicle Use Tax",
        "State highway-use taxes", "Oversize / overweight permits", "Trip permits", "State-specific permits"]),
    "new_carrier": ("New carrier setup", [
        "New trucking company / authority setup", "USDOT registration", "MC authority", "BOC-3",
        "Business formation / LLC setup", "Compliance setup for new carriers", "New Entrant Safety Audit support"]),
    "back_office": ("Back office & finance", [
        "Bookkeeping", "Trucking accounting", "Tax preparation", "Payroll", "Driver settlements",
        "Invoice preparation", "Accounts receivable (AR)", "Broker payment follow-up", "Collections",
        "Fuel cards", "Fuel discounts", "TMS setup / implementation", "Workflow automation",
        "Cost-per-mile analysis", "Fleet profitability analysis", "Fleet growth consulting"]),
}

SIGNAL_LINES: dict[str, list[str]] = {
    "OUT_OF_SERVICE": ["compliance"],  # + driver/vehicle specific lines, see lines_for()
    "HIGH_OOS_RATE": ["compliance", "maintenance", "safety_tech"],
    "REPEATED_VIOLATIONS": ["compliance", "safety_tech"],
    "INSPECTION_VIOLATION": ["compliance"],
    "CRASH": ["insurance", "safety_tech", "compliance"],
    "NEW_CARRIER": ["new_carrier", "insurance", "eld_hos", "registration", "back_office"],
    "NEW_AUTHORITY": ["new_carrier", "insurance", "registration", "back_office"],
    "STALE_MCS150": ["registration", "compliance"],
    "INACTIVE_STATUS": ["compliance", "registration"],
    "HOS_VIOLATIONS": ["eld_hos", "compliance"],
    "MAINTENANCE_VIOLATIONS": ["maintenance"],
    "UNSAFE_DRIVING": ["safety_tech"],
    "DRIVER_FITNESS": ["driver_files"],
    "DRUG_ALCOHOL": ["driver_files", "compliance"],
    "HAZMAT_VIOLATIONS": ["compliance", "safety_tech"],
    "HAZMAT_CARRIER": ["insurance", "compliance"],
    "AUTHORITY_REVOKED": ["insurance", "new_carrier", "compliance"],
    "INSURANCE_CANCELLATION": ["insurance"],
    "INSURANCE_SUSPENDED": ["insurance", "compliance"],
    "SUSPENSION_NOTICE": ["insurance", "compliance"],
    "INSURANCE_NEEDED": ["insurance", "new_carrier"],
    "INSURANCE_RENEWAL": ["insurance"],
    "FLEET_GROWTH": ["insurance", "driver_files", "safety_tech", "back_office"],
    "FLEET_SHRINK": ["back_office", "insurance"],
    "DRIVER_GROWTH": ["driver_files", "eld_hos", "safety_tech"],
    "HIRING_DRIVERS": ["driver_files", "safety_tech", "insurance"],
    "REACTIVATED": ["registration", "insurance", "compliance", "new_carrier"],
}
SEVERITY_WEIGHT = {"critical": 4, "high": 3, "medium": 2, "low": 1}


class SignalLike(Protocol):
    type: str
    severity: str
    evidence: dict[str, Any]


def lines_for(s: SignalLike) -> list[str]:
    lines = list(SIGNAL_LINES.get(s.type, []))
    if s.type == "OUT_OF_SERVICE":
        if int(s.evidence.get("driver_oos_total") or 0):
            lines += ["driver_files", "eld_hos"]
        if int(s.evidence.get("vehicle_oos_total") or 0):
            lines += ["maintenance"]
    return lines


def pitch(signals: Iterable[SignalLike]) -> list[dict]:
    """Service lines ranked by how strongly the evidence points at them, with the reasons."""
    acc: dict[str, dict] = {}
    for s in signals:
        for line in lines_for(s):
            p = acc.setdefault(line, {"strength": 0, "reasons": {}})
            p["strength"] += SEVERITY_WEIGHT.get(s.severity, 1)
            p["reasons"][s.type] = p["reasons"].get(s.type, 0) + 1
    ranked = sorted(acc.items(), key=lambda kv: -kv[1]["strength"])
    return [{"key": k, "label": SERVICE_LINES[k][0], "services": SERVICE_LINES[k][1], "strength": v["strength"],
             "reasons": [{"type": t, "count": n} for t, n in sorted(v["reasons"].items(), key=lambda kv: -kv[1])]}
            for k, v in ranked]
