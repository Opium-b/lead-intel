import json

import httpx
from sqlalchemy import select

from app.models import Company, Lead, LeadEvent
from app.notify import TelegramNotifier, notify_qualified_leads, send_lead_update
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


def test_status_update_message_with_history(db):
    setup_lead(db)
    lead = db.scalar(select(Lead))
    db.add(LeadEvent(lead_id=lead.id, event_type="STATUS_CHANGED",
                     meta={"from": "NEW", "to": "NO_ANSWER", "channel": "phone", "note": "voicemail"}))
    db.add(LeadEvent(lead_id=lead.id, event_type="STATUS_CHANGED",
                     meta={"from": "NO_ANSWER", "to": "DECLINED", "channel": "email", "note": "has a <provider>"}))
    lead.status = "DECLINED"
    db.commit()
    sent: list[dict] = []
    assert send_lead_update(db, lead.id, TelegramNotifier("TOKEN", "42", client=fake_telegram(sent))) is True
    text = sent[0]["text"]
    assert "No answer → Declined" in text and "via Email" in text and "has a &lt;provider&gt;" in text
    assert "Status: <b>Declined</b>" in text
    assert "No answer via Phone — voicemail" in text  # earlier contact attempt shown in the history
    assert text.index("Declined via Email") < text.index("No answer via Phone")  # newest first


def test_alert_shows_status(db):
    setup_lead(db)
    sent: list[dict] = []
    notify_qualified_leads(db, TelegramNotifier("TOKEN", "42", client=fake_telegram(sent)), min_score=1)
    assert "Status: <b>Not contacted yet</b>" in sent[0]["text"]
