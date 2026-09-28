"""Source-agnostic contract. The pipeline only ever sees these types."""
from dataclasses import dataclass, field
from datetime import date
from typing import Any, Protocol


@dataclass
class ContactRecord:
    type: str  # phone | email | website
    value: str
    source: str
    source_url: str | None = None
    label: str | None = None


@dataclass
class CompanyRecord:
    dot_number: str
    name: str
    dba_name: str | None = None
    mc_number: str | None = None
    state: str | None = None
    city: str | None = None
    location: str | None = None
    fleet_size: int | None = None
    drivers: int | None = None
    operating_status: str | None = None
    added_at: date | None = None
    attributes: dict[str, Any] = field(default_factory=dict)
    contacts: list[ContactRecord] = field(default_factory=list)


@dataclass
class EventRecord:
    """One raw fact (inspection, crash, registration snapshot...) tied to a company."""

    source: str
    record_type: str
    external_id: str
    company_dot: str
    observed_at: date | None
    source_url: str
    payload: dict[str, Any]


@dataclass
class NormalizedBatch:
    companies: list[CompanyRecord]
    events: list[EventRecord]
    cursor: date | None  # newest data date seen; next run starts from here
    stats: dict[str, int] = field(default_factory=dict)


class Collector(Protocol):
    name: str

    def fetch(self, since: date, limit: int) -> Any: ...

    def normalize(self, raw: Any) -> NormalizedBatch: ...
