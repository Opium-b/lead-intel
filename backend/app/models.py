from datetime import date, datetime
from enum import StrEnum

from sqlalchemy import BigInteger, Date, DateTime, ForeignKey, Integer, String, Text, UniqueConstraint, func
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db import Base



class LeadStatus(StrEnum):
    NEW = "NEW"
    REVIEWED = "REVIEWED"
    CONTACTED = "CONTACTED"
    QUALIFIED = "QUALIFIED"
    DISQUALIFIED = "DISQUALIFIED"
    CONVERTED = "CONVERTED"


class Severity(StrEnum):
    LOW = "low"
    MEDIUM = "medium"
    HIGH = "high"
    CRITICAL = "critical"


class TimestampMixin:
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), server_default=func.now(), onupdate=func.now()
    )


class Company(TimestampMixin, Base):
    __tablename__ = "companies"

    id: Mapped[int] = mapped_column(primary_key=True)
    dot_number: Mapped[str | None] = mapped_column(String(16), unique=True)
    mc_number: Mapped[str | None] = mapped_column(String(16), index=True)
    name: Mapped[str] = mapped_column(String(255))
    dba_name: Mapped[str | None] = mapped_column(String(255))
    state: Mapped[str | None] = mapped_column(String(2), index=True)
    city: Mapped[str | None] = mapped_column(String(128))
    location: Mapped[str | None] = mapped_column(String(512))
    fleet_size: Mapped[int | None] = mapped_column(Integer)
    drivers: Mapped[int | None] = mapped_column(Integer)
    operating_status: Mapped[str | None] = mapped_column(String(32))
    website: Mapped[str | None] = mapped_column(String(512))
    added_at: Mapped[date | None] = mapped_column(Date)  # carrier registration date at source
    enriched_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))  # last website/contact lookup
    attributes: Mapped[dict] = mapped_column(JSONB, default=dict)

    signals: Mapped[list["Signal"]] = relationship(back_populates="company", order_by="Signal.observed_at.desc()")
    contacts: Mapped[list["Contact"]] = relationship(back_populates="company")
    lead: Mapped["Lead | None"] = relationship(back_populates="company")


class SourceRecord(Base):
    """Raw, unmodified fact as fetched from a source. Signals point here as evidence."""

    __tablename__ = "source_records"
    __table_args__ = (UniqueConstraint("source", "record_type", "external_id"),)

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True)
    source: Mapped[str] = mapped_column(String(64))
    record_type: Mapped[str] = mapped_column(String(64))
    external_id: Mapped[str] = mapped_column(String(128))
    company_id: Mapped[int | None] = mapped_column(ForeignKey("companies.id", ondelete="CASCADE"), index=True)
    observed_at: Mapped[date | None] = mapped_column(Date)
    source_url: Mapped[str | None] = mapped_column(Text)
    payload: Mapped[dict] = mapped_column(JSONB)
    payload_hash: Mapped[str] = mapped_column(String(64))
    fetched_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())


class Signal(Base):
    __tablename__ = "signals"
    __table_args__ = (UniqueConstraint("company_id", "dedupe_key"),)

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True)
    company_id: Mapped[int] = mapped_column(ForeignKey("companies.id", ondelete="CASCADE"), index=True)
    type: Mapped[str] = mapped_column(String(64), index=True)
    description: Mapped[str] = mapped_column(Text)
    severity: Mapped[str] = mapped_column(String(16))
    source: Mapped[str] = mapped_column(String(64))
    source_url: Mapped[str | None] = mapped_column(Text)
    observed_at: Mapped[date | None] = mapped_column(Date, index=True)
    detected_by: Mapped[str] = mapped_column(String(64))
    origin: Mapped[str] = mapped_column(String(8), default="rule")  # 'rule' = fact-derived, 'ai' = interpretation
    evidence: Mapped[dict] = mapped_column(JSONB, default=dict)
    source_record_id: Mapped[int | None] = mapped_column(ForeignKey("source_records.id", ondelete="SET NULL"))
    dedupe_key: Mapped[str] = mapped_column(String(255))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now(), index=True)

    company: Mapped[Company] = relationship(back_populates="signals")


class Contact(Base):
    __tablename__ = "contacts"
    __table_args__ = (UniqueConstraint("company_id", "type", "value"),)

    id: Mapped[int] = mapped_column(primary_key=True)
    company_id: Mapped[int] = mapped_column(ForeignKey("companies.id", ondelete="CASCADE"), index=True)
    type: Mapped[str] = mapped_column(String(16))  # phone | email | website | social | person
    value: Mapped[str] = mapped_column(String(512))
    label: Mapped[str | None] = mapped_column(String(128))
    source: Mapped[str] = mapped_column(String(64))
    source_url: Mapped[str | None] = mapped_column(Text)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())

    company: Mapped[Company] = relationship(back_populates="contacts")


class Lead(TimestampMixin, Base):
    __tablename__ = "leads"

    id: Mapped[int] = mapped_column(primary_key=True)
    company_id: Mapped[int] = mapped_column(ForeignKey("companies.id", ondelete="CASCADE"), unique=True)
    score: Mapped[int] = mapped_column(Integer, index=True)
    score_breakdown: Mapped[list] = mapped_column(JSONB, default=list)
    service_lines: Mapped[list] = mapped_column(JSONB, default=list, server_default="[]")  # ranked keys, see services_catalog
    status: Mapped[str] = mapped_column(String(16), default=LeadStatus.NEW, index=True)
    scored_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    notified_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))

    company: Mapped[Company] = relationship(back_populates="lead")
    events: Mapped[list["LeadEvent"]] = relationship(back_populates="lead", order_by="LeadEvent.created_at.desc()")


class LeadEvent(Base):
    __tablename__ = "lead_events"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True)
    lead_id: Mapped[int] = mapped_column(ForeignKey("leads.id", ondelete="CASCADE"), index=True)
    event_type: Mapped[str] = mapped_column(String(32))
    # 'metadata' is reserved on declarative classes; the column keeps the name.
    meta: Mapped[dict] = mapped_column("metadata", JSONB, default=dict)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now(), index=True)

    lead: Mapped[Lead] = relationship(back_populates="events")


class ScoringRule(Base):
    __tablename__ = "scoring_rules"

    id: Mapped[int] = mapped_column(primary_key=True)
    key: Mapped[str] = mapped_column(String(64), unique=True)
    label: Mapped[str] = mapped_column(String(128))
    kind: Mapped[str] = mapped_column(String(32))
    weight: Mapped[int] = mapped_column(Integer)
    params: Mapped[dict] = mapped_column(JSONB, default=dict)
    enabled: Mapped[bool] = mapped_column(default=True)
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), server_default=func.now(), onupdate=func.now()
    )


class CollectorRun(Base):
    __tablename__ = "collector_runs"

    id: Mapped[int] = mapped_column(primary_key=True)
    source: Mapped[str] = mapped_column(String(64), index=True)
    started_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), server_default=func.now())
    finished_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    status: Mapped[str] = mapped_column(String(16), default="running")  # running | success | failed
    cursor: Mapped[date | None] = mapped_column(Date)  # data watermark reached by this run
    stats: Mapped[dict] = mapped_column(JSONB, default=dict)
    error: Mapped[str | None] = mapped_column(Text)
