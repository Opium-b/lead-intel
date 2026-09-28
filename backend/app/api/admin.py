import logging
from datetime import datetime, timedelta, timezone

from fastapi import APIRouter, BackgroundTasks, Depends, HTTPException
from sqlalchemy import select
from sqlalchemy.orm import Session

from app import scoring
from app.collectors import COLLECTORS
from app.db import SessionLocal, get_db
from app.models import CollectorRun, ScoringRule
from app.pipeline import process_companies, run_collection
from app.schemas import CollectIn, RunOut, ScoringRuleOut, ScoringRuleUpdate

router = APIRouter(tags=["admin"])
log = logging.getLogger(__name__)


@router.get("/scoring-rules", response_model=list[ScoringRuleOut])
def list_rules(db: Session = Depends(get_db)):
    scoring.seed_rules(db)
    return db.scalars(select(ScoringRule).order_by(ScoringRule.weight.desc())).all()


@router.put("/scoring-rules/{key}", response_model=ScoringRuleOut)
def update_rule(key: str, body: ScoringRuleUpdate, db: Session = Depends(get_db)):
    rule = db.scalar(select(ScoringRule).where(ScoringRule.key == key))
    if not rule:
        raise HTTPException(404, "Rule not found")
    for k, v in body.model_dump(exclude_none=True).items():
        setattr(rule, k, v)
    db.commit()
    log.info("scoring rule %s updated: %s (run POST /api/admin/reprocess to apply)", key, body.model_dump(exclude_none=True))
    return rule


def _in_background(fn, *args) -> None:
    with SessionLocal() as db:
        try:
            fn(db, *args)
        except Exception:
            log.exception("background job failed")


def _ensure_idle(db: Session) -> None:
    cutoff = datetime.now(timezone.utc) - timedelta(hours=2)
    if db.scalar(select(CollectorRun.id).where(CollectorRun.status == "running", CollectorRun.started_at > cutoff)):
        raise HTTPException(409, "A collection run is already in progress")


@router.post("/admin/collect", status_code=202)
def collect(body: CollectIn, tasks: BackgroundTasks, db: Session = Depends(get_db)):
    if body.source not in COLLECTORS:
        raise HTTPException(422, f"Unknown source; available: {sorted(COLLECTORS)}")
    _ensure_idle(db)
    tasks.add_task(_in_background, run_collection, body.source, body.since, body.limit)
    return {"accepted": True}


@router.post("/admin/reprocess", status_code=202)
def reprocess(tasks: BackgroundTasks, db: Session = Depends(get_db)):
    _ensure_idle(db)
    tasks.add_task(_in_background, process_companies)
    return {"accepted": True}


@router.get("/admin/runs", response_model=list[RunOut])
def runs(db: Session = Depends(get_db)):
    return db.scalars(select(CollectorRun).order_by(CollectorRun.id.desc()).limit(50)).all()
