"""Phase 6: which signals, scores and contact channels actually lead to deals, from the contact log.

Won = QUALIFIED/CONVERTED, lost = DECLINED/DISQUALIFIED ("decided" = either). Weight suggestions are advice shown in
the dashboard; nothing here changes a weight by itself.
"""
from collections import Counter, defaultdict

from sqlalchemy import select
from sqlalchemy.orm import Session

from app.models import Lead, LeadEvent, ScoringRule, Signal

WON = {"QUALIFIED", "CONVERTED"}
LOST = {"DECLINED", "DISQUALIFIED"}
UNWORKED = {"NEW", "REVIEWED"}
MIN_DECIDED_WITH = 5  # decided leads carrying a signal before its weight is judged
MIN_DECIDED_TOTAL = 10
BANDS = [(90, 100), (70, 89), (60, 69), (40, 59), (0, 39)]


def _rates(statuses: list[str]) -> dict:
    won, lost = sum(s in WON for s in statuses), sum(s in LOST for s in statuses)
    return {"leads": len(statuses), "worked": sum(s not in UNWORKED for s in statuses), "won": won, "lost": lost,
            "win_rate": won / (won + lost) if won + lost else None}


def _smoothed(r: dict) -> float:
    """Win rate pulled toward 50% for small samples (Laplace), so 1/1 doesn't read as a sure thing."""
    return (r["won"] + 1) / (r["won"] + r["lost"] + 2)


def analytics(db: Session) -> dict:
    leads = db.execute(select(Lead.company_id, Lead.score, Lead.status)).all()
    types_by_company: dict[int, set[str]] = defaultdict(set)
    for company_id, type_ in db.execute(select(Signal.company_id, Signal.type).distinct()):
        types_by_company[company_id].add(type_)

    totals = _rates([s for _, _, s in leads])
    by_type: dict[str, list[str]] = defaultdict(list)
    for company_id, _, status in leads:
        for t in types_by_company[company_id]:
            by_type[t].append(status)
    by_signal = []
    for t, statuses in by_type.items():
        r = _rates(statuses)
        by_signal.append({"type": t, **r, "lift": round(_smoothed(r) / _smoothed(totals), 2)})
    by_signal.sort(key=lambda r: (-r["leads"], r["type"]))

    by_band = [{"band": f"{lo}-{hi}", **_rates([s for _, score, s in leads if lo <= score <= hi])} for lo, hi in BANDS]

    channels: dict[str, Counter] = defaultdict(Counter)
    for meta in db.scalars(select(LeadEvent.meta).where(LeadEvent.event_type == "STATUS_CHANGED")):
        if ch := meta.get("channel"):
            to = meta.get("to")
            channels[ch].update(attempts=1, no_answer=to == "NO_ANSWER", won=to in WON, lost=to in LOST)
    by_channel = sorted(({"channel": ch, **{k: c[k] for k in ("attempts", "no_answer", "won", "lost")}}
                         for ch, c in channels.items()), key=lambda r: -r["attempts"])

    suggestions = []
    decided_total = totals["won"] + totals["lost"]
    stats = {r["type"]: r for r in by_signal}
    for rule in db.scalars(select(ScoringRule).where(ScoringRule.kind == "signal_type", ScoringRule.enabled)):
        r = stats.get(rule.params.get("type"))
        if (not r or rule.weight <= 0 or decided_total < MIN_DECIDED_TOTAL
                or r["won"] + r["lost"] < MIN_DECIDED_WITH):
            continue
        suggested = max(-100, min(100, round(rule.weight * r["lift"] / 5) * 5))
        if suggested != rule.weight:
            suggestions.append({"key": rule.key, "label": rule.label, "type": r["type"], "current": rule.weight,
                                "suggested": suggested, "reason": f"{r['won']}/{r['won'] + r['lost']} won with this "
                                f"signal vs {totals['won']}/{decided_total} overall (lift {r['lift']})"})
    suggestions.sort(key=lambda s: -abs(s["suggested"] - s["current"]))

    return {"funnel": dict(Counter(s for _, _, s in leads)), "totals": totals, "by_signal": by_signal,
            "by_score_band": by_band, "by_channel": by_channel, "suggestions": suggestions,
            "thresholds": {"min_decided_with_signal": MIN_DECIDED_WITH, "min_decided_total": MIN_DECIDED_TOTAL}}
