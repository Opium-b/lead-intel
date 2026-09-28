from datetime import date, datetime
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field

from app.models import LeadStatus


class ORM(BaseModel):
    model_config = ConfigDict(from_attributes=True)


class CompanyOut(ORM):
    id: int
    name: str
    dba_name: str | None
    dot_number: str | None
    mc_number: str | None
    state: str | None
    city: str | None
    location: str | None
    fleet_size: int | None
    drivers: int | None
    operating_status: str | None
    website: str | None
    added_at: date | None
    attributes: dict[str, Any]


class SignalOut(ORM):
    id: int
    type: str
    description: str
    severity: str
    source: str
    source_url: str | None
    observed_at: date | None
    detected_by: str
    origin: str
    evidence: dict[str, Any]
    created_at: datetime


class ContactOut(ORM):
    id: int
    type: str
    value: str
    label: str | None
    source: str
    source_url: str | None


class EventOut(ORM):
    id: int
    event_type: str
    meta: dict[str, Any]
    created_at: datetime


class LeadRow(BaseModel):
    id: int
    company_id: int
    name: str
    dot_number: str | None
    state: str | None
    fleet_size: int | None
    score: int
    status: str
    signal_count: int
    signal_types: list[str]
    service_lines: list[str]
    phone: str | None
    email: str | None
    person: str | None
    updated_at: datetime


class LeadPage(BaseModel):
    items: list[LeadRow]
    total: int
    page: int
    page_size: int


class PitchReason(BaseModel):
    type: str
    count: int


class Pitch(BaseModel):
    key: str
    label: str
    services: list[str]
    strength: int
    reasons: list[PitchReason]


class LeadDetail(ORM):
    id: int
    score: int
    score_breakdown: list[dict[str, Any]]
    pitch: list[Pitch]
    status: str
    scored_at: datetime | None
    notified_at: datetime | None
    ai_summary: dict[str, Any] | None
    ai_summary_at: datetime | None
    created_at: datetime
    updated_at: datetime
    company: CompanyOut
    signals: list[SignalOut]
    contacts: list[ContactOut]
    events: list[EventOut]


class LeadUpdate(BaseModel):
    status: LeadStatus
    channel: Literal["phone", "email", "sms", "whatsapp", "in_person", "other"] | None = None  # how we reached them
    note: str | None = Field(None, max_length=2000)  # outcome of the contact


class NoteIn(BaseModel):
    text: str = Field(min_length=1, max_length=5000)


class ScoringRuleOut(ORM):
    key: str
    label: str
    kind: str
    weight: int
    params: dict[str, Any]
    enabled: bool


class ScoringRuleUpdate(BaseModel):
    weight: int | None = Field(None, ge=-100, le=100)
    enabled: bool | None = None
    label: str | None = Field(None, min_length=1, max_length=128)
    params: dict[str, Any] | None = None


class CollectIn(BaseModel):
    source: str = "fmcsa"
    since: date | None = None
    limit: int = Field(500, ge=1, le=5000)


class RunOut(ORM):
    id: int
    source: str
    started_at: datetime
    finished_at: datetime | None
    status: str
    cursor: date | None
    stats: dict[str, Any]
    error: str | None
