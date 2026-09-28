from dataclasses import dataclass, field
from datetime import date
from typing import Any, Protocol


@dataclass
class Fact:
    """Read-only view of a stored source record handed to rules."""

    id: int | None
    source: str
    record_type: str
    external_id: str
    observed_at: date | None
    source_url: str | None
    payload: dict[str, Any]


@dataclass
class SignalDraft:
    type: str
    description: str
    severity: str
    source: str
    source_url: str | None
    observed_at: date | None
    dedupe_key: str
    detected_by: str
    evidence: dict[str, Any] = field(default_factory=dict)
    source_record_id: int | None = None


class SignalRule(Protocol):
    name: str

    def evaluate(self, facts: list[Fact], today: date) -> list[SignalDraft]: ...
