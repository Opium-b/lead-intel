from datetime import date, datetime, timedelta, timezone

from fastapi import APIRouter, Depends
from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.config import get_settings
from app.db import get_db
from app.models import Company, CollectorRun, Lead, LeadEvent, LeadStatus, Signal

router = APIRouter(prefix="/stats", tags=["stats"])


@router.get("/overview")
def overview(db: Session = Depends(get_db)):
    count = lambda stmt: db.scalar(select(func.count()).select_from(stmt.subquery()))  # noqa: E731
    week_ago = datetime.now(timezone.utc) - timedelta(days=7)
    today = datetime.combine(date.today(), datetime.min.time()).astimezone()
    high = get_settings().notify_min_score
    events = db.execute(
        select(LeadEvent, Lead.id, Company.name, Lead.score)
        .join(Lead, Lead.id == LeadEvent.lead_id).join(Company, Company.id == Lead.company_id)
        .order_by(LeadEvent.created_at.desc(), LeadEvent.id.desc()).limit(20)).all()
    last_run = db.scalar(select(CollectorRun).order_by(CollectorRun.id.desc()).limit(1))
    return {
        "companies": count(select(Company.id)),
        "new_signals_7d": count(select(Signal.id).where(Signal.created_at >= week_ago)),
        "leads": count(select(Lead.id)),
        "qualified_leads": count(select(Lead.id).where(Lead.status == LeadStatus.QUALIFIED)),
        "high_score_leads": count(select(Lead.id).where(Lead.score >= high)),
        "high_score_threshold": high,
        "leads_today": count(select(Lead.id).where(Lead.created_at >= today)),
        "signals_by_type": dict(db.execute(select(Signal.type, func.count()).group_by(Signal.type)).all()),
        "recent_activity": [{"id": e.id, "lead_id": lid, "company": name, "score": score, "event_type": e.event_type,
                             "meta": e.meta, "created_at": e.created_at} for e, lid, name, score in events],
        "last_run": last_run and {"id": last_run.id, "status": last_run.status, "started_at": last_run.started_at,
                                  "finished_at": last_run.finished_at, "stats": last_run.stats},
    }
