"""Phase 3: one Telegram message per qualifying lead, at most once (leads.notified_at).

Sent after scoring and enrichment, only when TELEGRAM_BOT_TOKEN and TELEGRAM_CHAT_ID are set.
Failures are logged and swallowed: lead generation never depends on Telegram.
"""
import logging
import time
from datetime import datetime, timezone
from html import escape

import httpx
from sqlalchemy import select
from sqlalchemy.orm import Session, selectinload

from app.config import get_settings
from app.models import Company, Lead, LeadEvent
from app.services_catalog import SERVICE_LINES

log = logging.getLogger(__name__)
SEND_INTERVAL = 1.1  # Telegram allows ~1 message/second per chat
HISTORY = 6  # timeline entries shown in a status update
STATUS_LABELS = {"NEW": "Not contacted yet", "REVIEWED": "Reviewed", "CONTACTED": "Contacted", "NO_ANSWER": "No answer",
                 "QUALIFIED": "Qualified", "DECLINED": "Declined", "DISQUALIFIED": "Disqualified", "CONVERTED": "Converted"}
CHANNEL_LABELS = {"phone": "Phone", "email": "Email", "sms": "SMS", "whatsapp": "WhatsApp", "in_person": "In person",
                  "other": "Other"}


def _phone(v: str) -> str:
    return f"({v[:3]}) {v[3:6]}-{v[6:]}" if len(v) == 10 else v


def _status(s: str) -> str:
    return STATUS_LABELS.get(s, s)


def _via(meta: dict) -> str:
    return f" via {CHANNEL_LABELS.get(meta['channel'], meta['channel'])}" if meta.get("channel") else ""


def _history_line(ev: LeadEvent) -> str | None:
    m = ev.meta
    text = {
        "STATUS_CHANGED": lambda: f"{_status(m.get('to', ''))}{_via(m)}" + (f" — {m['note']}" if m.get("note") else ""),
        "NOTE": lambda: f"Note{_via(m)}: {m.get('text', '')}",
        "NOTIFIED": lambda: "Alert sent to Telegram",
        "CREATED": lambda: f"Lead created (score {m.get('score')})",
    }.get(ev.event_type)
    return text and f"• {ev.created_at.astimezone():%d %b %H:%M} — {escape(text())}"


def format_lead(lead: Lead, dashboard: str) -> str:
    c, e = lead.company, escape
    first = lambda kind: next((x.value for x in sorted(c.contacts, key=lambda x: x.id) if x.type == kind), None)  # noqa: E731
    lines = [
        f"🔥 <b>{e(c.name)}</b> — score <b>{lead.score}</b>/100",
        e(" · ".join(filter(None, [f"USDOT {c.dot_number}" if c.dot_number else None, c.mc_number,
                                   c.city and c.state and f"{c.city.title()}, {c.state}",
                                   c.fleet_size is not None and f"{c.fleet_size} trucks"]))),
        f"Status: <b>{e(_status(lead.status))}</b>",
        "",
        "<b>Why:</b>",
        *[f"• {e(b['label'])} — {e(b['reason'])}" for b in lead.score_breakdown[:4]],
    ]
    if lead.ai_summary:
        lines += ["", f"🤖 <i>AI brief (check the facts):</i> {e(lead.ai_summary['summary'])}"]
    if lead.service_lines:
        lines += ["", "<b>Pitch:</b> " + e(", ".join(SERVICE_LINES[k][0] for k in lead.service_lines[:3] if k in SERVICE_LINES))]
    phone = first("phone")
    contact = [x for x in [first("person"), phone and _phone(phone), first("email"), c.website] if x]
    if contact:
        lines += ["", "<b>Contact:</b> " + e(" · ".join(contact))]
    lines += ["", f'<a href="{e(dashboard.rstrip("/"))}/leads/{lead.id}">Open in dashboard</a>']
    return "\n".join(lines)


def format_update(lead: Lead, event: LeadEvent, dashboard: str) -> str:
    """A status change or note from the dashboard, with the lead's recent history."""
    c, m, e = lead.company, event.meta, escape
    if event.event_type == "STATUS_CHANGED":
        head = f"📝 <b>{e(c.name)}</b>\n{e(_status(m.get('from', '')))} → {e(_status(m.get('to', '')))}" + (
            f" ({e(_via(m).removeprefix(' '))})" if m.get("channel") else "")
        note = m.get("note")
    else:
        head, note = f"💬 <b>{e(c.name)}</b>\nNote added" + (f" ({e(_via(m).removeprefix(' '))})" if m.get("channel") else ""), m.get("text")
    lines = [head, f"🕒 {event.created_at.astimezone():%d %b %Y %H:%M}"]
    if note:
        lines.append(f"🗒 {e(note)}")
    lines += ["", f"Status: <b>{e(_status(lead.status))}</b> · score {lead.score}"]
    history = [h for ev in sorted(lead.events, key=lambda x: x.id, reverse=True) if (h := _history_line(ev))][:HISTORY]
    if history:
        lines += ["", "<b>History:</b>", *history]
    lines += ["", f'<a href="{e(dashboard.rstrip("/"))}/leads/{lead.id}">Open in dashboard</a>']
    return "\n".join(lines)


def send_lead_update(db: Session, lead_id: int, notifier: "TelegramNotifier | None" = None) -> bool | None:
    """Send the lead's latest status change or note. None when Telegram isn't configured; never raises."""
    s = get_settings()
    if notifier is None:
        if not (s.telegram_bot_token and s.telegram_chat_id):
            return None
        notifier = TelegramNotifier(s.telegram_bot_token, s.telegram_chat_id)
    lead = db.scalar(select(Lead).where(Lead.id == lead_id).options(
        selectinload(Lead.company), selectinload(Lead.events)))
    event = lead and max((ev for ev in lead.events if ev.event_type in ("STATUS_CHANGED", "NOTE")),
                         key=lambda ev: ev.id, default=None)
    if not event:
        return False
    try:
        notifier.send(format_update(lead, event, s.frontend_origin))
    except RuntimeError as err:
        log.warning("lead %d update not sent: %s", lead_id, err)
        return False
    return True


class TelegramNotifier:
    def __init__(self, token: str, chat_id: str, client: httpx.Client | None = None):
        self.token, self.chat_id = token, chat_id
        self.client = client or httpx.Client(timeout=15)

    def send(self, text: str) -> None:
        """Raises RuntimeError without the token in its message (the token is part of the URL)."""
        try:
            r = self.client.post(f"https://api.telegram.org/bot{self.token}/sendMessage", json={
                "chat_id": self.chat_id, "text": text, "parse_mode": "HTML", "disable_web_page_preview": True})
            body = r.json()
        except (httpx.HTTPError, ValueError) as e:
            raise RuntimeError(f"telegram unreachable: {type(e).__name__}") from None
        if not body.get("ok"):
            raise RuntimeError(f"telegram rejected message: {r.status_code} {body.get('description', '')}")

    def send_document(self, path, caption: str) -> None:
        """Upload a file (max 50 MB) with an HTML caption. Same error contract as send()."""
        try:
            with open(path, "rb") as f:
                r = self.client.post(f"https://api.telegram.org/bot{self.token}/sendDocument", timeout=120,
                                     data={"chat_id": self.chat_id, "caption": caption[:1024], "parse_mode": "HTML"},
                                     files={"document": (path.name, f)})
            body = r.json()
        except (httpx.HTTPError, ValueError) as e:
            raise RuntimeError(f"telegram unreachable: {type(e).__name__}") from None
        if not body.get("ok"):
            raise RuntimeError(f"telegram rejected document: {r.status_code} {body.get('description', '')}")


def notify_qualified_leads(db: Session, notifier: TelegramNotifier | None = None,
                           min_score: int | None = None, limit: int | None = None) -> dict | None:
    """Send leads with score >= NOTIFY_MIN_SCORE not yet notified, best first. None when not configured."""
    s = get_settings()
    if notifier is None:
        if not (s.telegram_bot_token and s.telegram_chat_id):
            return None
        notifier = TelegramNotifier(s.telegram_bot_token, s.telegram_chat_id)
    leads = db.scalars(select(Lead).where(
        Lead.notified_at.is_(None), Lead.score >= (s.notify_min_score if min_score is None else min_score),
    ).options(selectinload(Lead.company).selectinload(Company.contacts))
        .order_by(Lead.score.desc(), Lead.id).limit(limit or s.notify_limit)).all()

    stats = {"sent": 0, "failed": 0}
    for i, lead in enumerate(leads):
        if i:
            time.sleep(SEND_INTERVAL)
        try:
            notifier.send(format_lead(lead, s.frontend_origin))
        except RuntimeError as e:
            log.warning("lead %d not notified: %s", lead.id, e)  # stays unmarked: retried next run
            stats["failed"] += 1
            continue
        lead.notified_at = datetime.now(timezone.utc)
        db.add(LeadEvent(lead_id=lead.id, event_type="NOTIFIED", meta={"channel": "telegram", "score": lead.score}))
        db.commit()
        stats["sent"] += 1
    log.info("notifications: %s", stats)
    return stats
