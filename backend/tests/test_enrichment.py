import httpx
from sqlalchemy import func, select

from app.enrichment import Enricher, enrich_leads
from app.models import Company, Contact, LeadEvent
from app.pipeline import ingest, process_companies
from tests.test_core import TODAY, batch

HOME = """<html><body><h1>Acme Freight</h1>
<a href="mailto:Info@AcmeFreight.com?subject=hi">Email us</a>
<a href="tel:+1 (214) 555-0100">Call</a>
<a href="https://www.facebook.com/acmefreight/">fb</a>
<a href="https://www.facebook.com/sharer/sharer.php?u=x">share</a>
<p>Sales: Sales@AcmeFreight.com · (214) 555-0177 · <img src="logo@2x.png"> hero@2x.png</p>
<a href="https://wa.me/12145550188">WhatsApp</a>
<a href="/contact">Contact</a></body></html>"""
CONTACT = '<p>Dispatch: <a href="tel:214.555.0199">214.555.0199</a></p><form><textarea name="m"></textarea></form>'


def fake_web(pages: dict[str, tuple[int, str]], seen: list[str]) -> httpx.Client:
    def handler(req: httpx.Request) -> httpx.Response:
        seen.append(f"{req.url.host}{req.url.path}")
        if req.url.host == "api.search.brave.com":
            assert req.headers["X-Subscription-Token"] == "key"
            return httpx.Response(200, json={"web": {"results": [{"url": u} for u in pages["_search"][1].split()]}})
        status, body = pages.get(f"{req.url.host}{req.url.path}", (404, ""))
        return httpx.Response(status, text=body, headers={"content-type": "text/html; charset=utf-8"})
    return httpx.Client(transport=httpx.MockTransport(handler), follow_redirects=True)


def company(**kw) -> Company:
    return Company(**{"name": "ACME FREIGHT LLC", "dot_number": "123", "city": "DALLAS", "state": "TX"} | kw)


def test_website_from_registered_email_domain():
    seen: list[str] = []
    web = fake_web({"acmefreight.com/": (200, HOME), "acmefreight.com/contact": (200, CONTACT)}, seen)
    site, contacts = Enricher(web).enrich(company(), ["ops@acmefreight.com"], [])
    assert site == "https://acmefreight.com/"
    found = {(c.type, c.value, c.source, c.source_url) for c in contacts}
    assert found == {
        ("website", "https://acmefreight.com/", "email_domain", "https://acmefreight.com/"),
        ("email", "info@acmefreight.com", "website", "https://acmefreight.com/"),
        ("phone", "2145550100", "website", "https://acmefreight.com/"),
        ("social", "https://www.facebook.com/acmefreight", "website", "https://acmefreight.com/"),
        ("phone", "2145550199", "website", "https://acmefreight.com/contact"),
        ("email", "sales@acmefreight.com", "website", "https://acmefreight.com/"),  # plain text, not a link
        ("phone", "2145550177", "website", "https://acmefreight.com/"),
        ("social", "https://wa.me/12145550188", "website", "https://acmefreight.com/"),
        ("form", "https://acmefreight.com/contact", "website", "https://acmefreight.com/contact"),
    }


def test_free_mail_and_robots_disallow():
    seen: list[str] = []
    assert Enricher(fake_web({}, seen)).enrich(company(), ["acme@gmail.com"], []) == (None, [])
    assert seen == []  # no search key, free-mail domain: nothing fetched

    web = fake_web({"acmefreight.com/robots.txt": (200, "User-agent: *\nDisallow: /"), "acmefreight.com/": (200, HOME)}, seen)
    site, contacts = Enricher(web).enrich(company(), ["ops@acmefreight.com"], [])
    assert site == "https://acmefreight.com/" and [c.type for c in contacts] == ["website"]
    assert "acmefreight.com/" not in seen  # site exists (robots answered) but pages were not read


def test_search_result_needs_dot_or_phone_on_page():
    seen: list[str] = []
    web = fake_web({
        "_search": (200, "https://www.yelp.com/biz/acme https://other-acme.com/ https://acme-trucking.com/"),
        "other-acme.com/": (200, "<p>Acme Freight, Ohio. USDOT 99999</p>"),
        "acme-trucking.com/": (200, "<p>Acme Freight LLC · USDOT #123 · (214) 555-0100</p>"),
    }, seen)
    site, contacts = Enricher(web, search_key="key").enrich(company(), ["acme@gmail.com"], ["2145550100"])
    assert site == "https://acme-trucking.com/"
    assert ("website", "web_search") in {(c.type, c.source) for c in contacts}
    assert not any("yelp.com" in s for s in seen)  # directories are skipped without fetching


def test_enrich_leads_is_idempotent_and_respects_ttl(db):
    process_companies(db, ingest(db, batch()), TODAY)
    web = fake_web({"acmefreight.com/": (200, HOME), "acmefreight.com/contact": (200, CONTACT)}, [])
    first = enrich_leads(db, Enricher(web))
    assert first["enriched"] == 1 and first["contacts"] == 8
    assert enrich_leads(db, Enricher(web))["enriched"] == 0  # within TTL: skipped

    db.execute(Company.__table__.update().values(enriched_at=None))
    db.commit()
    assert enrich_leads(db, Enricher(web))["contacts"] == 0  # re-run: nothing duplicated
    assert db.scalar(select(Company.website)) == "https://acmefreight.com/"
    assert db.scalar(select(func.count()).select_from(Contact)) == 6 + 8
    assert [e.event_type for e in db.scalars(select(LeadEvent).where(LeadEvent.event_type == "ENRICHED"))] == ["ENRICHED"]
