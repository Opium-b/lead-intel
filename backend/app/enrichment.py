"""Phase 2 enrichment: find a lead's website and the contacts published on it.

No guessing. A website is accepted only when it is the company's own registered email domain, or when a
search result's site shows the company's USDOT number or registered phone. Every contact records the page
it was seen on, and robots.txt is honored for every page read.
"""
import ipaddress
import logging
import re
import socket
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from html.parser import HTMLParser
from urllib.parse import unquote, urljoin, urlsplit
from urllib.robotparser import RobotFileParser

import httpx
from sqlalchemy import or_, select
from sqlalchemy.dialects.postgresql import insert
from sqlalchemy.orm import Session

from app.collectors.base import ContactRecord
from app.config import get_settings
from app.models import Company, Contact, Lead, LeadEvent

log = logging.getLogger(__name__)

USER_AGENT = "leadintel/0.1 (lead research; honors robots.txt)"
SEARCH_URL = "https://api.search.brave.com/res/v1/web/search"
MAX_BYTES = 2_000_000  # per page
MAX_SUBPAGES = 2  # contact pages linked from the homepage
MAX_CANDIDATES = 5  # search results checked per company

FREE_MAIL = {
    "gmail.com", "googlemail.com", "yahoo.com", "ymail.com", "rocketmail.com", "hotmail.com", "outlook.com",
    "live.com", "msn.com", "aol.com", "icloud.com", "me.com", "mac.com", "protonmail.com", "proton.me", "gmx.com",
    "mail.com", "zoho.com", "yandex.com", "comcast.net", "att.net", "sbcglobal.net", "bellsouth.net", "verizon.net",
    "charter.net", "cox.net", "earthlink.net", "frontier.com", "windstream.net", "centurylink.net", "optonline.net",
    "roadrunner.com", "twc.com", "spectrum.net", "juno.com", "netzero.net",
}
# Search results on these hosts are never the company's own site, so they're skipped without fetching.
DIRECTORIES = {
    "facebook.com", "linkedin.com", "instagram.com", "x.com", "twitter.com", "youtube.com", "tiktok.com",
    "yelp.com", "bbb.org", "yellowpages.com", "mapquest.com", "manta.com", "bizapedia.com", "opencorporates.com",
    "dnb.com", "zoominfo.com", "indeed.com", "glassdoor.com", "google.com", "carriersource.io", "wikipedia.org",
}
SOCIAL = {"facebook.com": "Facebook", "linkedin.com": "LinkedIn", "instagram.com": "Instagram",
          "x.com": "X", "twitter.com": "X"}
DOMAIN = re.compile(r"(?:[a-z0-9-]+\.)+[a-z]{2,}")
EMAIL = re.compile(r"[a-z0-9._%+-]+@" + DOMAIN.pattern)
PHONE = re.compile(r"\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4}")


def _phone(raw: str) -> str | None:
    digits = re.sub(r"\D", "", raw)
    digits = digits[1:] if len(digits) == 11 and digits[0] == "1" else digits
    return digits if len(digits) == 10 else None


def _on(host: str, domains) -> str | None:
    """The entry of `domains` that host is, or is a subdomain of."""
    return next((d for d in domains if host == d or host.endswith("." + d)), None)


def _public_only(request: httpx.Request) -> None:
    """Refuse private/loopback targets: domains come from public registrations anyone can edit."""
    # ponytail: checked before connect, so DNS rebinding can slip past; pin resolved IPs if this ever runs multi-tenant
    try:
        infos = socket.getaddrinfo(request.url.host, None)
    except socket.gaierror as e:
        raise httpx.ConnectError(str(e), request=request)
    if not all(ipaddress.ip_address(i[4][0].split("%")[0]).is_global for i in infos):
        raise httpx.ConnectError("refusing non-public address", request=request)


class _Parser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.links: list[str] = []
        self.text: list[str] = []

    def handle_starttag(self, tag, attrs):
        if tag == "a" and (href := dict(attrs).get("href")):
            self.links.append(href.strip())

    def handle_data(self, data):
        self.text.append(data)


@dataclass
class Page:
    url: str
    contacts: list[ContactRecord] = field(default_factory=list)
    subpages: list[str] = field(default_factory=list)
    text: str = ""

    @classmethod
    def parse(cls, url: str, html: str) -> "Page":
        p = _Parser()
        p.feed(html)
        page = cls(url, text=" ".join(p.text))
        add = lambda t, v, label: page.contacts.append(ContactRecord(t, v, "website", url, label))  # noqa: E731
        for href in p.links:
            low = href.lower()
            if low.startswith("mailto:"):
                if EMAIL.fullmatch(email := unquote(href[7:]).split("?")[0].strip().lower()):
                    add("email", email, "Email on website")
            elif low.startswith("tel:"):
                if phone := _phone(href[4:]):
                    add("phone", phone, "Phone on website")
            else:
                full = urljoin(url, href).split("#")[0]
                parts = urlsplit(full)
                host, path = (parts.hostname or ""), parts.path.rstrip("/")
                if net := _on(host, SOCIAL):
                    if path and not any(w in path.lower() for w in ("share", "intent", "dialog")):
                        add("social", f"https://{host}{path}", f"{SOCIAL[net]} page")
                elif host == urlsplit(url).hostname and "contact" in path.lower() and full not in page.subpages:
                    page.subpages.append(full)
        return page

    def shows(self, dot: str | None, phones: set[str]) -> str | None:
        """What on this page proves it belongs to the company, if anything."""
        if dot and re.search(rf"(?<!\d){re.escape(dot)}(?!\d)", self.text):
            return f"USDOT {dot}"
        seen = {_phone(m.group()) for m in PHONE.finditer(self.text)} | {c.value for c in self.contacts if c.type == "phone"}
        return f"registered phone {hit}" if (hit := next(iter(seen & phones), None)) else None


class Enricher:
    def __init__(self, client: httpx.Client | None = None, search_key: str | None = None):
        self.client = client or httpx.Client(timeout=10, follow_redirects=True, headers={"User-Agent": USER_AGENT},
                                             event_hooks={"request": [_public_only]})
        self.search_key = search_key

    def enrich(self, company: Company, emails: list[str], phones: list[str]) -> tuple[str | None, list[ContactRecord]]:
        """Returns (website, contacts). Network errors on a site just mean nothing was found there."""
        for domain in dict.fromkeys(e.rsplit("@", 1)[-1].lower() for e in emails):
            if domain in FREE_MAIL or not DOMAIN.fullmatch(domain):
                continue
            if site := self._read_site(f"https://{domain}/"):
                url, pages = site
                return url, self._collect(ContactRecord(
                    "website", url, "email_domain", url, f"Domain of registered email @{domain}"), pages)

        if self.search_key:
            for base in self._search(company):
                if not (site := self._read_site(base)):
                    continue
                url, pages = site
                if proof := next(((p.url, why) for p in pages if (why := p.shows(company.dot_number, set(phones)))), None):
                    return url, self._collect(ContactRecord(
                        "website", url, "web_search", proof[0], f"Found by web search; page shows {proof[1]}"), pages)
        return None, []

    @staticmethod
    def _collect(website: ContactRecord, pages: list[Page]) -> list[ContactRecord]:
        found = {(website.type, website.value): website}
        for c in (c for p in pages for c in p.contacts):
            if len(c.value) <= 512:
                found.setdefault((c.type, c.value), c)
        return list(found.values())

    def _search(self, company: Company) -> list[str]:
        q = " ".join(filter(None, [f'"{company.dba_name or company.name}"', company.city, company.state]))
        r = self.client.get(SEARCH_URL, params={"q": q, "count": 10},
                            headers={"X-Subscription-Token": self.search_key, "Accept": "application/json"})
        r.raise_for_status()  # quota/auth problems surface as a failed company, retried next run
        roots = []
        for result in r.json().get("web", {}).get("results", []):
            parts = urlsplit(result.get("url", ""))
            if parts.scheme in ("http", "https") and parts.hostname and not _on(parts.hostname, DIRECTORIES) \
                    and not parts.hostname.endswith(".gov"):
                roots.append(f"{parts.scheme}://{parts.hostname}/")
        return list(dict.fromkeys(roots))[:MAX_CANDIDATES]

    def _read_site(self, base: str) -> tuple[str, list[Page]] | None:
        """(site url, pages read) if the site answers; pages is empty when robots.txt forbids reading them."""
        try:
            r = self.client.get(urljoin(base, "/robots.txt"))
        except httpx.HTTPError:
            return None
        robots = RobotFileParser()
        if r.status_code >= 500:
            robots.disallow_all = True
        elif r.status_code >= 400:
            robots.allow_all = True
        else:
            robots.parse(r.text.splitlines())
        if not robots.can_fetch(USER_AGENT, base):
            # ponytail: a parked domain also answers here; add a parked-page check if those show up as websites
            return base, []

        if not (home := self._page(base)):
            return None
        pages = [home]
        for sub in home.subpages[:MAX_SUBPAGES]:
            if robots.can_fetch(USER_AGENT, sub) and (page := self._page(sub)):
                pages.append(page)
        return home.url, pages

    def _page(self, url: str) -> Page | None:
        try:
            with self.client.stream("GET", url) as r:
                if r.status_code != 200 or "html" not in r.headers.get("content-type", ""):
                    return None
                body = b""
                for chunk in r.iter_bytes():
                    body += chunk
                    if len(body) >= MAX_BYTES:
                        break
                return Page.parse(str(r.url), body.decode(r.encoding or "utf-8", "replace"))
        except httpx.HTTPError:
            return None


def enrich_leads(db: Session, enricher: Enricher | None = None, limit: int | None = None) -> dict:
    """Enrich leads at or above ENRICH_MIN_SCORE not enriched within ENRICH_TTL_DAYS, best score first."""
    from app.pipeline import process_companies  # pipeline imports this module

    s = get_settings()
    enricher = enricher or Enricher(search_key=s.brave_api_key)
    now = datetime.now(timezone.utc)
    due = db.scalars(select(Company).join(Lead).where(
        Lead.score >= s.enrich_min_score,
        or_(Company.enriched_at.is_(None), Company.enriched_at < now - timedelta(days=s.enrich_ttl_days)),
    ).order_by(Lead.score.desc()).limit(limit or s.enrich_limit)).all()

    stats = {"enriched": 0, "websites": 0, "contacts": 0, "failed": 0}
    changed = []
    for company in due:
        known = db.execute(select(Contact.type, Contact.value).where(Contact.company_id == company.id)).all()
        try:
            website, found = enricher.enrich(company, [v for t, v in known if t == "email"],
                                             [v for t, v in known if t == "phone"])
        except Exception:
            log.exception("enrichment failed for company %d", company.id)  # not marked enriched: retried next run
            stats["failed"] += 1
            continue
        new = db.scalars(insert(Contact).values([
            dict(company_id=company.id, type=c.type, value=c.value, label=c.label, source=c.source,
                 source_url=c.source_url) for c in found
        ]).on_conflict_do_nothing(index_elements=["company_id", "type", "value"]).returning(Contact.type)).all() if found else []
        company.website = website or company.website
        company.enriched_at = now
        if new:
            db.add(LeadEvent(lead_id=company.lead.id, event_type="ENRICHED",
                             meta={"website": website, "contacts": len(new), "types": sorted(set(new))}))
            changed.append(company.id)
        db.commit()
        stats["enriched"] += 1
        stats["websites"] += website is not None
        stats["contacts"] += len(new)

    if changed:
        process_companies(db, changed)  # contacts feed the has_contact scoring rule
    log.info("enrichment: %s", stats)
    return stats
