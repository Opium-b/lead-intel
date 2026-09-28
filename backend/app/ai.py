"""Phase 5: an AI pre-call brief per lead, written by Claude from the lead's stored facts.

Interpretation only: stored on the lead (leads.ai_summary), never mixed into facts or signals, and labeled as AI
everywhere it is shown. Only business facts are sent - no phone numbers or emails. A lead is re-summarized only when
the facts it would be given change, so repeat runs cost nothing.
"""
import hashlib
import json
import logging
import re
from datetime import datetime, timezone

import anthropic
from sqlalchemy import select
from sqlalchemy.orm import Session, selectinload

from app.config import get_settings
from app.models import Company, Lead
from app.services_catalog import SERVICE_LINES

log = logging.getLogger(__name__)

MODEL = "claude-opus-5"
MAX_SIGNALS = 25
SCHEMA = {
    "type": "object",
    "properties": {
        "summary": {"type": "string", "description": "2-3 sentences: why this company is worth contacting now"},
        "talking_points": {"type": "array", "items": {"type": "string"},
                           "description": "2-4 specific points, each tied to a dated fact"},
        "opener": {"type": "string", "description": "one friendly first sentence for a call or email"},
    },
    "required": ["summary", "talking_points", "opener"],
    "additionalProperties": False,
}
SYSTEM = """You write short pre-call briefs for a sales rep who sells services to US trucking companies: \
compliance and audits, driver files and drug testing, ELD and hours of service, safety tech and training, \
maintenance, insurance, registration and permits, new-carrier setup, and back office.

You are given facts about one company from public FMCSA records and the company's own website, plus the \
services our rules matched to those facts. Use only these facts. Never invent numbers, names, dates, \
problems or needs, and don't speculate beyond what a fact supports. Refer to facts by their dates.

The opener must be friendly and helpful, never accusatory: don't lead with their violations or crashes as \
a criticism. If a contact person is named, you may address them by first name."""

SEVERITY_ORDER = {"critical": 0, "high": 1, "medium": 2, "low": 3}


def facts_prompt(lead: Lead) -> str:
    """Deterministic (sorted, absolute dates) so its hash only changes when the facts do."""
    c = lead.company
    person = next((x.value for x in sorted(c.contacts, key=lambda x: x.id) if x.type == "person"), None)
    signals = sorted(c.signals, key=lambda s: (SEVERITY_ORDER.get(s.severity, 9), -(s.observed_at or datetime.min.date()).toordinal(), s.id))
    lines = [
        f"Company: {c.name}" + (f" (DBA {c.dba_name})" if c.dba_name else ""),
        f"Location: {c.city or '?'}, {c.state or '?'} · fleet: {c.fleet_size} power units, {c.drivers} drivers",
        f"USDOT status: {'active' if c.operating_status == 'A' else c.operating_status} · registered: {c.added_at}",
        f"Contact person on registration: {person or 'unknown'}",
        f"Website: {c.website or 'none found'}",
        f"Lead score: {lead.score}/100 · our status: {lead.status}",
        "", "Score reasons:",
        # "(4d ago)" changes daily; dropping it keeps the hash (and the bill) stable while facts don't change
        *[f"- {b['label']}: {re.sub(r' [(]\d+d ago[)]', '', b['reason'])}" for b in lead.score_breakdown],
        "", "Services our rules matched (strongest first): "
        + ", ".join(SERVICE_LINES[k][0] for k in lead.service_lines if k in SERVICE_LINES),
        "", f"Facts ({min(len(signals), MAX_SIGNALS)} of {len(signals)}, most severe first):",
        *[f"- [{s.severity}] {s.type} on {s.observed_at}: {s.description}" for s in signals[:MAX_SIGNALS]],
    ]
    return "\n".join(lines)


class Summarizer:
    def __init__(self, client: anthropic.Anthropic):
        self.client = client

    def brief(self, prompt: str) -> tuple[dict | None, str, tuple[int, int]]:
        """(brief or None if refused/truncated, model that answered, (input, output) tokens)."""
        r = self.client.beta.messages.create(
            model=MODEL, max_tokens=16000, system=SYSTEM,
            messages=[{"role": "user", "content": prompt}],
            # medium effort: a short brief from given facts doesn't need deep reasoning; raise it if briefs get thin
            output_config={"effort": "medium", "format": {"type": "json_schema", "schema": SCHEMA}},
            betas=["server-side-fallback-2026-07-01"], fallbacks="default",  # a false-positive refusal retries elsewhere
        )
        usage = (r.usage.input_tokens, r.usage.output_tokens)
        if r.stop_reason != "end_turn":  # refusal (even after fallback) or max_tokens: nothing trustworthy to store
            log.warning("brief not stored: stop_reason=%s", r.stop_reason)
            return None, r.model, usage
        return json.loads(next(b.text for b in r.content if b.type == "text")), r.model, usage


def default_summarizer() -> Summarizer | None:
    key = get_settings().anthropic_api_key
    return Summarizer(anthropic.Anthropic(api_key=key)) if key else None


def summarize_lead(db: Session, lead: Lead, summarizer: Summarizer, stats: dict, force: bool = False) -> None:
    prompt = facts_prompt(lead)
    digest = hashlib.sha256(f"{MODEL}\n{SYSTEM}\n{prompt}".encode()).hexdigest()
    if not force and lead.ai_summary and lead.ai_summary.get("input_hash") == digest:
        return
    data, model, (tokens_in, tokens_out) = summarizer.brief(prompt)
    stats["input_tokens"] += tokens_in
    stats["output_tokens"] += tokens_out
    if data is None:
        stats["skipped"] += 1
        return
    lead.ai_summary = data | {"model": model, "input_hash": digest}
    lead.ai_summary_at = datetime.now(timezone.utc)
    db.commit()
    stats["summarized"] += 1


def new_stats() -> dict:
    return {"summarized": 0, "skipped": 0, "failed": 0, "input_tokens": 0, "output_tokens": 0}


def summarize_leads(db: Session, summarizer: Summarizer | None = None, min_score: int | None = None,
                    limit: int | None = None) -> dict | None:
    """Brief leads at/above AI_MIN_SCORE whose facts changed, best first, at most AI_LIMIT API calls.
    None when ANTHROPIC_API_KEY isn't set."""
    s = get_settings()
    summarizer = summarizer or default_summarizer()
    if summarizer is None:
        return None
    limit = limit or s.ai_limit
    leads = db.scalars(select(Lead).where(Lead.score >= (s.ai_min_score if min_score is None else min_score))
                       .options(selectinload(Lead.company).selectinload(Company.signals),
                                selectinload(Lead.company).selectinload(Company.contacts))
                       .order_by(Lead.score.desc(), Lead.id)).all()
    stats = new_stats()
    for lead in leads:
        if stats["summarized"] + stats["skipped"] + stats["failed"] >= limit:
            break
        try:
            summarize_lead(db, lead, summarizer, stats)
        except anthropic.AuthenticationError:
            log.error("Anthropic API key rejected; stopping AI briefs")
            stats["failed"] += 1
            break
        except anthropic.APIError as e:  # rate limits / 5xx were already retried by the SDK
            log.warning("lead %d brief failed: %s", lead.id, type(e).__name__)
            stats["failed"] += 1
    log.info("AI briefs: %s", stats)
    return stats
