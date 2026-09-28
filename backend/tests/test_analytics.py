from app import scoring
from app.analytics import analytics
from app.models import Company, Lead, LeadEvent, Signal


def make_lead(db, i: int, score: int, status: str, signal_types: list[str]) -> Lead:
    c = Company(dot_number=str(i), name=f"CO {i}")
    db.add(c)
    db.flush()
    for t in signal_types:
        db.add(Signal(company_id=c.id, type=t, description=t, severity="high", source="fmcsa", detected_by="test",
                      dedupe_key=t))
    lead = Lead(company_id=c.id, score=score, status=status)
    db.add(lead)
    db.flush()
    return lead


def test_analytics_win_rates_and_suggestions(db):
    scoring.seed_rules(db)
    # with CRASH: 5 won, 1 lost; without: 1 won, 5 lost; plus 2 untouched leads
    for i, status in enumerate(["QUALIFIED", "CONVERTED", "QUALIFIED", "QUALIFIED", "CONVERTED", "DECLINED"]):
        make_lead(db, i, 95, status, ["CRASH", "HOS_VIOLATIONS"])
    for i, status in enumerate(["QUALIFIED", "DECLINED", "DISQUALIFIED", "DECLINED", "DECLINED", "DISQUALIFIED"], 10):
        make_lead(db, i, 45, status, ["HOS_VIOLATIONS"])
    first = make_lead(db, 20, 30, "NEW", ["CRASH"])
    make_lead(db, 21, 30, "NO_ANSWER", [])
    db.add(LeadEvent(lead_id=first.id, event_type="STATUS_CHANGED", meta={"from": "NEW", "to": "NO_ANSWER", "channel": "phone"}))
    db.add(LeadEvent(lead_id=first.id, event_type="STATUS_CHANGED", meta={"from": "NO_ANSWER", "to": "CONTACTED", "channel": "email"}))
    db.add(LeadEvent(lead_id=first.id, event_type="STATUS_CHANGED", meta={"from": "NEW", "to": "REVIEWED"}))  # no channel
    db.commit()

    a = analytics(db)
    assert a["totals"] == {"leads": 14, "worked": 13, "won": 6, "lost": 6, "win_rate": 0.5}
    crash = next(r for r in a["by_signal"] if r["type"] == "CRASH")
    assert (crash["leads"], crash["won"], crash["lost"], crash["win_rate"]) == (7, 5, 1, 5 / 6)
    assert crash["lift"] == 1.5  # smoothed: (5+1)/(6+2) vs (6+1)/(12+2)

    bands = {b["band"]: b for b in a["by_score_band"]}
    assert (bands["90-100"]["won"], bands["40-59"]["lost"]) == (5, 5)

    assert {c["channel"]: (c["attempts"], c["no_answer"]) for c in a["by_channel"]} == {"phone": (1, 1), "email": (1, 0)}

    sugg = {s["key"]: s for s in a["suggestions"]}
    assert sugg["sig_crash"]["current"] == 10 and sugg["sig_crash"]["suggested"] == 15
    assert "5/6 won" in sugg["sig_crash"]["reason"]
    assert "sig_inactive" not in sugg  # negative weights are never auto-suggested


def test_no_suggestions_without_enough_outcomes(db):
    scoring.seed_rules(db)
    make_lead(db, 1, 90, "QUALIFIED", ["CRASH"])
    db.commit()
    a = analytics(db)
    assert a["suggestions"] == [] and a["totals"]["win_rate"] == 1.0
