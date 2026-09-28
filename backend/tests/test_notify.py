import json

import httpx
from sqlalchemy import select

from app.models import Company, Lead, LeadEvent
from app.notify import TelegramNotifier, notify_qualified_leads
from app.pipeline import ingest, process_companies
from tests.test_core import TODAY, batch


def fake_telegram(sent: list[dict], ok: bool = True) -> httpx.Client:
    def handler(req: httpx.Request) -> httpx.Response:
        assert req.url.path == "/botTOKEN/sendMessage"
        sent.append(json.loads(req.content))
        if not ok:
            return httpx.Response(400, json={"ok": False, "description": "Bad Request: chat not found"})
        return httpx.Response(200, json={"ok": True, "result": {}})
    return httpx.Client(transport=httpx.MockTransport(handler))


def setup_lead(db):
    process_companies(db, ingest(db, batch()), TODAY)
    db.execute(Company.__table__.update().values(name="ACME <FREIGHT> & SONS"))
    db.commit()


def test_notifies_once_with_escaped_details(db):
    setup_lead(db)
    sent: list[dict] = []
    notifier = TelegramNotifier("TOKEN", "42", client=fake_telegram(sent))
    assert notify_qualified_leads(db, notifier, min_score=1) == {"sent": 1, "failed": 0}
    msg = sent[0]
    assert msg["chat_id"] == "42" and msg["parse_mode"] == "HTML"
    assert "ACME &lt;FREIGHT&gt; &amp; SONS" in msg["text"]  # company data can't inject markup
    assert "John Q Doe" in msg["text"] and "(214) 555-0100" in msg["text"] and "USDOT 123" in msg["text"]
    lead = db.scalar(select(Lead))
    assert lead.notified_at is not None
    assert [e.event_type for e in db.scalars(select(LeadEvent).where(LeadEvent.event_type == "NOTIFIED"))] == ["NOTIFIED"]

    assert notify_qualified_leads(db, notifier, min_score=1) == {"sent": 0, "failed": 0}  # at most once
    assert len(sent) == 1


def test_below_threshold_and_failures_are_not_marked(db):
    setup_lead(db)
    sent: list[dict] = []
    assert notify_qualified_leads(db, TelegramNotifier("TOKEN", "42", client=fake_telegram(sent)), min_score=101)["sent"] == 0
    assert sent == []

    failing = TelegramNotifier("TOKEN", "42", client=fake_telegram(sent, ok=False))
    assert notify_qualified_leads(db, failing, min_score=1) == {"sent": 0, "failed": 1}  # logged, never raised
    assert db.scalar(select(Lead.notified_at)) is None  # retried next run


def test_disabled_without_credentials(db):
    setup_lead(db)
    assert notify_qualified_leads(db) is None  # no TELEGRAM_* in the test env
