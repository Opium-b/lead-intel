"""Collector output -> companies/facts -> signals -> score -> lead. Every step is an idempotent upsert."""
import hashlib
import json
import logging
from datetime import date, datetime, timedelta, timezone

from sqlalchemy import delete, func, select
from sqlalchemy.dialects.postgresql import insert
from sqlalchemy.orm import Session

from app import scoring
from app.collectors import COLLECTORS
from app.collectors.base import NormalizedBatch
from app.config import get_settings
from app.enrichment import enrich_leads
from app.models import Company, CollectorRun, Contact, Lead, LeadEvent, Signal, SourceRecord
from app.signals.base import Fact
from app.services_catalog import pitch
from app.signals.rules import detect

log = logging.getLogger(__name__)
CHUNK = 1000


def _chunks(rows: list[dict]):
    for i in range(0, len(rows), CHUNK):
        yield rows[i : i + CHUNK]


def _hash(payload: dict) -> str:
    return hashlib.sha256(json.dumps(payload, sort_keys=True, default=str).encode()).hexdigest()


def ingest(db: Session, batch: NormalizedBatch) -> list[int]:
    """Upsert companies, contacts and raw facts. Returns ids of touched companies."""
    ids: dict[str, int] = {}
    for c in batch.companies:
        values = dict(dot_number=c.dot_number, mc_number=c.mc_number, name=c.name, dba_name=c.dba_name,
                      state=c.state, city=c.city, location=c.location, fleet_size=c.fleet_size, drivers=c.drivers,
                      operating_status=c.operating_status, added_at=c.added_at, attributes=c.attributes)
        stmt = insert(Company).values(values)
        update = {k: stmt.excluded[k] for k in values if k not in ("dot_number", "mc_number")}
        update["mc_number"] = func.coalesce(stmt.excluded.mc_number, Company.mc_number)
        update["updated_at"] = func.now()
        ids[c.dot_number] = db.scalar(
            stmt.on_conflict_do_update(index_elements=["dot_number"], set_=update).returning(Company.id))

        if c.contacts:
            db.execute(insert(Contact).values([
                dict(company_id=ids[c.dot_number], type=ct.type, value=ct.value, label=ct.label, source=ct.source,
                     source_url=ct.source_url) for ct in c.contacts
            ]).on_conflict_do_nothing(index_elements=["company_id", "type", "value"]))

    rows = [dict(source=e.source, record_type=e.record_type, external_id=e.external_id,
                 company_id=ids[e.company_dot], observed_at=e.observed_at, source_url=e.source_url,
                 payload=e.payload, payload_hash=_hash(e.payload))
            for e in batch.events if e.company_dot in ids]
    for part in _chunks(list({(r["source"], r["record_type"], r["external_id"]): r for r in rows}.values())):
        stmt = insert(SourceRecord).values(part)
        db.execute(stmt.on_conflict_do_update(
            index_elements=["source", "record_type", "external_id"],
            set_={k: stmt.excluded[k] for k in ("company_id", "observed_at", "source_url", "payload", "payload_hash")}
            | {"fetched_at": func.now()},
            where=SourceRecord.payload_hash != stmt.excluded.payload_hash,
        ))
    db.commit()
    return list(ids.values())


def process_company(db: Session, company: Company, rules: list, today: date) -> Lead | None:
    """Re-derive signals from stored facts, re-score, create/update the lead."""
    facts = [Fact(r.id, r.source, r.record_type, r.external_id, r.observed_at, r.source_url, r.payload)
             for r in db.scalars(select(SourceRecord).where(SourceRecord.company_id == company.id))]
    drafts = {d.dedupe_key: d for d in detect(facts, today)}

    existing = set(db.scalars(select(Signal.dedupe_key).where(Signal.company_id == company.id, Signal.origin == "rule")))
    for part in _chunks([dict(company_id=company.id, origin="rule", **vars(d)) for d in drafts.values()]):
        stmt = insert(Signal).values(part)
        db.execute(stmt.on_conflict_do_update(
            index_elements=["company_id", "dedupe_key"],
            set_={k: stmt.excluded[k] for k in ("type", "description", "severity", "source", "source_url",
                                                 "observed_at", "detected_by", "evidence", "source_record_id")}))
    # Rule signals are a pure function of facts: drop ones the rules no longer produce (e.g. aged out).
    db.execute(delete(Signal).where(Signal.company_id == company.id, Signal.origin == "rule",
                                    Signal.dedupe_key.not_in(list(drafts) or [""])))
    added = [d for k, d in drafts.items() if k not in existing]

    contact_types = set(db.scalars(select(Contact.type).where(Contact.company_id == company.id)))
    lines = [p["key"] for p in pitch(drafts.values())]
    view = scoring.CompanyView(company.operating_status, company.fleet_size,
                               [scoring.SignalView(d.type, d.observed_at, d.severity) for d in drafts.values()],
                               contact_types, lines)
    points, breakdown = scoring.score(view, rules, today)

    now = datetime.now(timezone.utc)
    lead = db.scalar(select(Lead).where(Lead.company_id == company.id))
    if lead is None:
        if not drafts or points < get_settings().lead_min_score:
            db.commit()
            return None
        lead = Lead(company_id=company.id, score=points, score_breakdown=breakdown, service_lines=lines, scored_at=now)
        db.add(lead)
        db.flush()
        db.add(LeadEvent(lead_id=lead.id, event_type="CREATED",
                         meta={"score": points, "signals": sorted({d.type for d in drafts.values()})}))
    else:
        if added:
            db.add(LeadEvent(lead_id=lead.id, event_type="SIGNALS_ADDED",
                             meta={"signals": [{"type": d.type, "description": d.description} for d in added[:20]],
                                   "count": len(added)}))
        if lead.score != points:
            db.add(LeadEvent(lead_id=lead.id, event_type="SCORE_CHANGED", meta={"from": lead.score, "to": points}))
        lead.score, lead.score_breakdown, lead.service_lines, lead.scored_at = points, breakdown, lines, now
    db.commit()
    return lead


def process_companies(db: Session, company_ids: list[int] | None = None, today: date | None = None) -> dict:
    scoring.seed_rules(db)
    rules = scoring.load_rules(db)
    today = today or date.today()
    q = select(Company) if company_ids is None else select(Company).where(Company.id.in_(company_ids))
    # ponytail: one company per transaction, sequential; batch or parallelize past ~10k companies per run
    leads = sum(process_company(db, c, rules, today) is not None for c in db.scalars(q).all())
    return {"processed": len(company_ids) if company_ids is not None else None, "leads": leads}


def run_collection(db: Session, source: str = "fmcsa", since: date | None = None, limit: int = 500) -> CollectorRun:
    s = get_settings()
    if since is None:
        last = db.scalar(select(CollectorRun.cursor).where(
            CollectorRun.source == source, CollectorRun.status == "success", CollectorRun.cursor.is_not(None))
            .order_by(CollectorRun.id.desc()).limit(1))
        since = (last - timedelta(days=s.collect_overlap_days) if last
                 else date.today() - timedelta(days=s.collect_initial_lookback_days))

    collector = COLLECTORS[source]()
    return _run(db, source, {"since": str(since), "limit": limit}, lambda: collector.normalize(collector.fetch(since, limit)))


def refresh_companies(db: Session, source: str = "fmcsa") -> CollectorRun:
    """Re-fetch full history for every company already tracked (picks up new inspections, insurance, etc.)."""
    dots = list(db.scalars(select(Company.dot_number).where(Company.dot_number.is_not(None))))
    collector = COLLECTORS[source]()
    return _run(db, source, {"refresh": len(dots)}, lambda: collector.normalize(collector.fetch_companies(dots, apply_target=False)))


def _run(db: Session, source: str, stats: dict, collect) -> CollectorRun:
    run = CollectorRun(source=source, stats=stats)
    db.add(run)
    db.commit()
    log.info("collector run %d: source=%s %s", run.id, source, stats)
    try:
        batch = collect()
        ids = ingest(db, batch)
        result = process_companies(db, ids)
        try:
            result["enrichment"] = enrich_leads(db)
        except Exception:  # enrichment is best-effort; collection already succeeded
            db.rollback()
            log.exception("collector run %d: enrichment failed", run.id)
        run.status = "success"
        # refreshes don't move the discovery cursor
        run.cursor = (batch.cursor or date.fromisoformat(stats["since"])) if "since" in stats else None
        run.stats = run.stats | batch.stats | result
    except Exception as e:
        db.rollback()
        run.status, run.error = "failed", f"{type(e).__name__}: {e}"[:2000]
        log.exception("collector run %d failed", run.id)
        raise
    finally:
        run.finished_at = datetime.now(timezone.utc)
        db.commit()
    log.info("collector run %d done: %s", run.id, run.stats)
    return run
