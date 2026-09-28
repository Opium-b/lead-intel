from typing import Literal

from fastapi import APIRouter, BackgroundTasks, Depends, HTTPException, Query
from sqlalchemy import func, or_, select
from sqlalchemy.orm import Session, selectinload

from app.db import SessionLocal, get_db
from app.models import Company, Contact, Lead, LeadEvent, LeadStatus, Signal
from app.notify import send_lead_update
from app.services_catalog import SERVICE_LINES, pitch
from app.schemas import EventOut, LeadDetail, LeadPage, LeadRow, LeadUpdate, NoteIn

router = APIRouter(prefix="/leads", tags=["leads"])
catalog_router = APIRouter(tags=["leads"])


@catalog_router.get("/service-lines")
def service_lines():
    return [{"key": k, "label": label, "services": services} for k, (label, services) in SERVICE_LINES.items()]

SORTS = {"score": Lead.score, "updated_at": Lead.updated_at, "name": Company.name, "state": Company.state,
         "signals": "signal_count"}
SEVERITY_ORDER = {"critical": 0, "high": 1, "medium": 2, "low": 3}


def _first_contact(kind: str):
    return (select(Contact.value).where(Contact.company_id == Company.id, Contact.type == kind)
            .order_by(Contact.id).limit(1).scalar_subquery())


@router.get("", response_model=LeadPage)
def list_leads(
    db: Session = Depends(get_db),
    q: str | None = Query(None, max_length=100),
    state: str | None = Query(None, pattern="^[A-Za-z]{2}$"),
    status: LeadStatus | None = None,
    min_score: int = Query(0, ge=0, le=100),
    signal_type: str | None = Query(None, pattern="^[A-Z_]{1,64}$"),
    service_line: str | None = Query(None, pattern="^[a-z_]{1,32}$"),
    sort: Literal["score", "-score", "updated_at", "-updated_at", "name", "-name", "state", "-state",
                  "signals", "-signals"] = "-score",
    page: int = Query(1, ge=1),
    page_size: int = Query(25, ge=1, le=100),
):
    sig = (select(Signal.company_id, func.count().label("signal_count"),
                  func.array_agg(func.distinct(Signal.type)).label("signal_types"))
           .group_by(Signal.company_id).subquery())
    stmt = (select(Lead, Company, func.coalesce(sig.c.signal_count, 0).label("signal_count"), sig.c.signal_types,
                   _first_contact("phone").label("phone"), _first_contact("email").label("email"),
                   _first_contact("person").label("person"))
            .join(Company, Company.id == Lead.company_id)
            .outerjoin(sig, sig.c.company_id == Company.id)
            .where(Lead.score >= min_score))
    if q:
        like = f"%{q.strip()}%"
        stmt = stmt.where(or_(Company.name.ilike(like), Company.dba_name.ilike(like), Company.dot_number == q.strip(),
                              Company.mc_number == q.strip()))
    if state:
        stmt = stmt.where(Company.state == state.upper())
    if status:
        stmt = stmt.where(Lead.status == status)
    if service_line:
        stmt = stmt.where(Lead.service_lines.contains([service_line]))
    if signal_type:
        stmt = stmt.where(select(Signal.id).where(Signal.company_id == Company.id, Signal.type == signal_type).exists())

    total = db.scalar(select(func.count()).select_from(stmt.subquery()))
    col = SORTS[sort.lstrip("-")]
    col = sig.c.signal_count if col == "signal_count" else col
    order = col.desc().nulls_last() if sort.startswith("-") else col.asc().nulls_last()
    rows = db.execute(stmt.order_by(order, Lead.id).offset((page - 1) * page_size).limit(page_size)).all()
    items = [LeadRow(id=l.id, company_id=c.id, name=c.name, dot_number=c.dot_number, state=c.state,
                     fleet_size=c.fleet_size, score=l.score, status=l.status, signal_count=n,
                     signal_types=sorted(types or []),
                     service_lines=l.service_lines or [], phone=phone, email=email, person=person,
                     updated_at=l.updated_at)
             for l, c, n, types, phone, email, person in rows]
    return LeadPage(items=items, total=total, page=page, page_size=page_size)


def _get_lead(db: Session, lead_id: int) -> Lead:
    lead = db.scalar(select(Lead).where(Lead.id == lead_id).options(
        selectinload(Lead.company).selectinload(Company.signals),
        selectinload(Lead.company).selectinload(Company.contacts),
        selectinload(Lead.events)))
    if not lead:
        raise HTTPException(404, "Lead not found")
    return lead


@router.get("/{lead_id}", response_model=LeadDetail)
def get_lead(lead_id: int, db: Session = Depends(get_db)):
    lead = _get_lead(db, lead_id)
    signals = sorted(lead.company.signals, key=lambda s: (SEVERITY_ORDER.get(s.severity, 9), -(s.observed_at.toordinal() if s.observed_at else 0)))
    return LeadDetail.model_validate({**{k: getattr(lead, k) for k in LeadDetail.model_fields
                                         if k not in ("signals", "contacts", "company", "pitch")},
                                      "company": lead.company, "signals": signals, "contacts": lead.company.contacts,
                                      "pitch": pitch(signals)})


def _telegram_update(lead_id: int) -> None:
    """Runs after the response: a slow or failing Telegram never delays or breaks the dashboard."""
    with SessionLocal() as db:
        send_lead_update(db, lead_id)


@router.patch("/{lead_id}", response_model=LeadDetail)
def update_lead(lead_id: int, body: LeadUpdate, tasks: BackgroundTasks, db: Session = Depends(get_db)):
    lead = _get_lead(db, lead_id)
    note = (body.note or "").strip()
    extra = ({"channel": body.channel} if body.channel else {}) | ({"note": note} if note else {})
    if lead.status != body.status:
        db.add(LeadEvent(lead_id=lead.id, event_type="STATUS_CHANGED", meta={"from": lead.status, "to": body.status} | extra))
        lead.status = body.status
    elif note:
        db.add(LeadEvent(lead_id=lead.id, event_type="NOTE", meta={"text": note} | extra))
    else:
        return get_lead(lead_id, db)
    db.commit()
    tasks.add_task(_telegram_update, lead_id)
    db.expire_all()
    return get_lead(lead_id, db)


@router.post("/{lead_id}/notes", response_model=EventOut, status_code=201)
def add_note(lead_id: int, body: NoteIn, tasks: BackgroundTasks, db: Session = Depends(get_db)):
    lead = _get_lead(db, lead_id)
    event = LeadEvent(lead_id=lead.id, event_type="NOTE", meta={"text": body.text.strip()})
    db.add(event)
    db.commit()
    tasks.add_task(_telegram_update, lead_id)
    return event
