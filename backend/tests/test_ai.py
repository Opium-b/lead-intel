import json

import anthropic
import httpx2
from sqlalchemy import select

from app.ai import MODEL, Summarizer, facts_prompt, summarize_leads
from app.models import Lead
from app.pipeline import ingest, process_companies
from tests.test_core import TODAY, batch

SUMMARY = {"summary": "Acme had a fatal crash and an insurance cancellation.",
           "talking_points": ["Insurance cancellation effective soon", "Crash in OK"],
           "opener": "Hi John, I saw your liability policy with Old Ins is being cancelled."}


def fake_claude(requests: list[dict], stop_reason: str = "end_turn") -> anthropic.Anthropic:
    def handler(req: httpx2.Request) -> httpx2.Response:
        requests.append(json.loads(req.content))
        return httpx2.Response(200, json={
            "id": "msg_1", "type": "message", "role": "assistant", "model": MODEL, "stop_reason": stop_reason,
            "stop_sequence": None, "usage": {"input_tokens": 900, "output_tokens": 300},
            "content": [] if stop_reason == "refusal" else [{"type": "text", "text": json.dumps(SUMMARY)}]})
    return anthropic.Anthropic(api_key="test", max_retries=0,
                               http_client=httpx2.Client(transport=httpx2.MockTransport(handler)))


def test_summarizes_from_facts_only_once_per_evidence(db):
    process_companies(db, ingest(db, batch()), TODAY)
    requests: list[dict] = []
    summarizer = Summarizer(fake_claude(requests))
    assert summarize_leads(db, summarizer, min_score=1)["summarized"] == 1

    req = requests[0]
    assert req["model"] == MODEL and req["output_config"]["format"]["type"] == "json_schema"
    prompt = req["messages"][0]["content"]
    assert "ACME FREIGHT LLC" in prompt and "John Q Doe" in prompt and "CRASH" in prompt
    assert "2145550100" not in prompt and "ops@acmefreight.com" not in prompt  # contact details never leave

    lead = db.scalar(select(Lead))
    assert lead.ai_summary["summary"] == SUMMARY["summary"] and lead.ai_summary["model"] == MODEL
    assert lead.ai_summary_at is not None

    assert summarize_leads(db, summarizer, min_score=1)["summarized"] == 0  # evidence unchanged: no new spend
    assert len(requests) == 1


def test_refusal_is_skipped_not_stored(db):
    process_companies(db, ingest(db, batch()), TODAY)
    stats = summarize_leads(db, Summarizer(fake_claude([], stop_reason="refusal")), min_score=1)
    assert stats == {"summarized": 0, "skipped": 1, "failed": 0, "input_tokens": 900, "output_tokens": 300}
    assert db.scalar(select(Lead.ai_summary)) is None


def test_disabled_without_key(db):
    process_companies(db, ingest(db, batch()), TODAY)
    assert summarize_leads(db) is None


def test_prompt_has_no_relative_dates(db):
    process_companies(db, ingest(db, batch()), TODAY)
    lead = db.scalar(select(Lead))
    lead.score_breakdown = [{"label": "Hot right now", "reason": "crash on 2026-09-20 (7d ago)", "points": 10, "key": "hot_now"}]
    assert "- Hot right now: crash on 2026-09-20\n" in facts_prompt(lead)
