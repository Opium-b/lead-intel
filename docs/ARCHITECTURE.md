# Logistics Lead Intelligence Platform — Architecture & Plan

Product question every screen must answer: **"Why is this company a lead?"** — with facts, sources and dates.
Rule of the system: *facts come from sources, scores come from configurable rules, AI (later) only annotates.*

---

## 1. Architecture

```
                   ┌─────────────── scheduled (cron / CLI / admin API) ───────────────┐
                   ▼                                                                   │
 Public sources ─► Collector.fetch() ─► Collector.normalize() ─► NormalizedBatch       │
 (FMCSA Socrata)       raw rows           CompanyRecord + EventRecord (typed)          │
                                                   │                                   │
                                                   ▼                                   │
                                   upsert companies  (unique dot_number)               │
                                   upsert source_records (unique source+type+ext_id)   │  ◄─ raw evidence, immutable facts
                                                   │                                   │
                                                   ▼                                   │
                                   Signal engine: rules(company, records) → signals    │  ◄─ deterministic, dedupe_key
                                                   │                                   │
                                                   ▼                                   │
                                   Scoring engine: scoring_rules (DB) → score+breakdown│  ◄─ explainable
                                                   │                                   │
                                   Lead upsert (unique company_id) + lead_events       │
                                                   │                                   │
                              score ≥ ENRICH_MIN ─►│─► Enrichment providers → contacts │  (phase 2)
                                                   │   (re-score after enrichment)     │
                              score ≥ NOTIFY_MIN ─►│─► Notifier (Telegram) once/lead   │  (phase 3, optional)
                                                   ▼
                                 PostgreSQL ◄── FastAPI (/api, bearer auth) ◄── Vue 3 dashboard
```

One process type (FastAPI) + one CLI entrypoint (`python -m app.cli run`) that shares the same pipeline code. No queue, no workers in V1.

## 2. Technology choices

| Concern | Choice | Why |
|---|---|---|
| API | FastAPI + Pydantic v2 | requested; typed validation at every boundary |
| ORM / migrations | SQLAlchemy 2.0 + Alembic | schema evolves without rewrites |
| DB | PostgreSQL 16+ (JSONB, `ON CONFLICT`) | idempotent upserts, flexible evidence payloads |
| HTTP | httpx | sync+async, timeouts, retries |
| Config | pydantic-settings (env vars) | secrets never in code |
| Scheduling | cron/systemd timer calling the CLI | zero infra; swap for APScheduler/Celery when needed |
| Frontend | Vue 3 + TypeScript + Vite + vue-router | requested; no UI kit in V1 |
| Tests | pytest against a real Postgres test DB | upsert/conflict logic must be tested on the real engine |
| Packaging | uv (backend), npm (frontend), Docker Compose (deploy) | |

## 3. Database schema

Core entities from the brief, plus four supporting tables that make traceability and idempotency possible.

```
companies        id PK, dot_number UNIQUE NULL, mc_number, name, dba_name, state, city, location,
                 fleet_size (power units), drivers, operating_status, website, added_at (carrier add date),
                 attributes JSONB, created_at, updated_at
source_records   id PK, source, record_type, external_id, company_id FK, observed_at, source_url,
                 payload JSONB, payload_hash, fetched_at        UNIQUE(source, record_type, external_id)
                 -- the raw, unmodified facts; signals point at these
signals          id PK, company_id FK, type, description, severity(low|medium|high|critical),
                 source, source_url, observed_at, detected_by (rule name), origin ('rule'|'ai'),
                 evidence JSONB (the exact fact fields), source_record_id FK NULL,
                 dedupe_key, created_at                           UNIQUE(company_id, dedupe_key)
contacts         id PK, company_id FK, type(phone|email|website|social|person), value, label,
                 source, source_url, created_at                   UNIQUE(company_id, type, value)
leads            id PK, company_id FK UNIQUE, score, score_breakdown JSONB, status, scored_at,
                 notified_at, created_at, updated_at
lead_events      id PK, lead_id FK, event_type (CREATED|SCORE_CHANGED|STATUS_CHANGED|NOTE|NOTIFIED|ENRICHED),
                 metadata JSONB, created_at
scoring_rules    id PK, key UNIQUE, label, kind, weight, params JSONB, enabled, updated_at
collector_runs   id PK, source, started_at, finished_at, status, cursor, stats JSONB, error
```

- **Fact vs interpretation**: `signals.origin='rule'` rows are derived only from `evidence` copied out of a `source_record`. AI output (later) goes in `origin='ai'` rows or a separate `interpretations` table. It never overwrites facts.
- **Idempotency**: every write is an upsert on a natural key (`dot_number`, `(source,type,external_id)`, `(company_id,dedupe_key)`, `(company_id,type,value)`). `leads.notified_at` makes notifications at-most-once.
- **Growth path**: new sources add `record_type`s rather than tables. Companies without a DOT (brokers, shippers) are allowed because `dot_number` is nullable. `attributes` JSONB holds source-specific fields until they need a column.

## 4. API design (all under `/api`, bearer `API_SECRET`)

```
GET   /api/health                         (public)
GET   /api/stats/overview                 counts + recent activity
GET   /api/leads?q=&state=&status=&min_score=&signal_type=&sort=-score&page=&page_size=
GET   /api/leads/{id}                     company, signals(+evidence), breakdown, contacts, events
PATCH /api/leads/{id}                     {status}
POST  /api/leads/{id}/notes               {text}
GET   /api/scoring-rules                  PUT /api/scoring-rules/{key}  {weight, enabled, params}
POST  /api/admin/rescore                  recompute all scores after rule changes
POST  /api/admin/collect                  {source, since?, limit?} → runs pipeline in background
GET   /api/admin/runs                     collector run history
```
OpenAPI is generated at `/docs`.

## 5. Collector architecture

```python
class Collector(Protocol):
    name: str
    def fetch(self, since: date | None, limit: int) -> RawBatch: ...
    def normalize(self, raw: RawBatch) -> NormalizedBatch: ...   # CompanyRecord[], EventRecord[]
```
- `CompanyRecord`: identity fields (dot/mc/name/state…) plus `attributes`.
- `EventRecord`: `record_type`, `external_id`, `company_dot`, `observed_at`, `source_url`, `payload`.
- The pipeline only knows these two types, so a new source is one new module plus registry entry.
- **V1 source, FMCSA open data** (data.transportation.gov, Socrata, public, no auth; optional `SOCRATA_APP_TOKEN` raises rate limits):
  - `fx4q-ay7w` Vehicle Inspection File: inspections with violations/OOS since the cursor. This is the **discovery driver**: problem-first, not "scrape everyone".
  - `az4n-8mr2` Company Census: company profile for the discovered DOTs, plus a "new carriers" feed (`add_date ≥ cursor`).
  - `aayw-vxb3` Crash File: crashes for discovered DOTs (last 12 months).
  - Later: `9mw4-x3tu` AuthHist (MC numbers, authority grants/revocations), `sa6p-acbp` Revocations.
- The cursor is a date with an N-day overlap, stored in `collector_runs`. Overlap is safe because every write is an upsert.
- Etiquette: page size ≤ 1000, timeouts, retry with backoff, identifying User-Agent.

## 6. Signal / rule architecture

```python
class SignalRule:
    name: str
    def evaluate(self, company: Company, records: list[SourceRecord], now: date) -> list[SignalDraft]
```
Registry list in `signals/rules.py`. V1 rules:

| Rule | Signal type | Fires when | Severity |
|---|---|---|---|
| OutOfServiceRule | `OUT_OF_SERVICE` | inspection with `oos_total>0` in last 365d | high |
| InspectionViolationRule | `INSPECTION_VIOLATION` | inspection with `viol_total>0`, no OOS | medium |
| RepeatedViolationRule | `REPEATED_VIOLATIONS` | ≥3 violating inspections in 365d | high |
| CrashRule | `CRASH` | crash in last 365d (fatal → critical, injury → high) | high/critical |
| NewCarrierRule | `NEW_CARRIER` | census `add_date` within 180d | low |
| StaleRegistrationRule | `STALE_MCS150` | MCS-150 last updated > 24 months ago (biennial update is required) | medium |
| InactiveStatusRule | `INACTIVE_STATUS` | census status ≠ active | medium |

Every draft carries `evidence` (the exact fields used) plus `source_url` and `observed_at`. The `dedupe_key` is derived from the record (for example `OUT_OF_SERVICE:insp:79765752`), so re-running rules never duplicates signals.

## 7. Scoring architecture

`scoring_rules` rows are evaluated by a small set of **factor kinds**. Adding a rule is a DB row; adding a new *kind* is one function.

| kind | params | points when |
|---|---|---|
| `signal_type` | `{type}` | company has ≥1 signal of that type |
| `signal_count` | `{min}` | total signals ≥ min |
| `recent_signal` | `{days}` | any signal observed within N days |
| `active_company` | – | operating_status is active |
| `fleet_size` | `{min,max}` | fleet size in range |
| `has_contact` | `{types}` | a contact of those types exists |

`score = min(100, Σ matched weights)`. The breakdown is stored as `[{key,label,points,reason}]`, for example `+15 Recent violation — inspection 2026-09-12 (TX)`. Seed weights ship in `scoring.py` (DEFAULT_RULES) and are inserted if missing; after that the DB is the source of truth. `POST /admin/rescore` recomputes all scores.

## 8. Enrichment architecture (phase 2)

`app/enrichment.py`. `Enricher.enrich(company, emails, phones) -> (website, list[ContactRecord])`, each contact with `source` and the exact page `source_url`. It runs at the end of every collector run and via `cli enrich`, only for leads ≥ `ENRICH_MIN_SCORE`, best score first, at most `ENRICH_LIMIT` per run, and at most once per `ENRICH_TTL_DAYS` per company (`companies.enriched_at`).
1. `fmcsa_census`: registered phone/email (in the V1 collector).
2. `email_domain`: a non-free-mail registered email domain → `https://<domain>/`, accepted if it answers (the registration itself is the evidence).
3. `web_search` (only when `BRAVE_API_KEY` is set and step 2 found nothing): Brave Search for `"name" city state`. Directory/social/.gov hosts are skipped; a result's site is accepted **only if one of its pages shows the USDOT number or registered phone**.
4. `website`: homepage plus up to 2 linked contact pages, **only if robots.txt allows**; extracts `mailto:`, `tel:` and Facebook/LinkedIn/Instagram/X links.

No guessing: nothing is stored that was not observed, and every contact records where it was seen. Pages are capped at 2 MB, requests time out after 10 s, and private/loopback addresses are refused. A failed company is not marked enriched, so it is retried next run; a new contact adds an `ENRICHED` lead event and triggers a re-score.

## 9. Telegram architecture (phase 3)

`notifications/` has a `Notifier` protocol and a `TelegramNotifier` (Bot API `sendMessage`, HTML parse mode, escaped values).
- The pipeline calls `notify_qualified_leads()` after scoring, but only when `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID` are set.
- Send condition: `score ≥ NOTIFY_MIN_SCORE AND notified_at IS NULL`. `notified_at` is set in the same transaction, after a successful send.
- Failures are logged and swallowed, so lead generation never depends on Telegram.

## 10. Frontend architecture

`frontend/` uses Vite + Vue 3 + TS + vue-router. `src/api.ts` is a typed fetch wrapper that adds the bearer token from localStorage, and there is one view per page:
- `OverviewView`: stat tiles plus a recent activity feed.
- `LeadsView`: table with search, state/status/min-score filters, sortable columns and pagination, all driven by server-side query params.
- `LeadDetailView`: company card, score breakdown, signals (each with evidence and source link), contacts with sources, status select, notes and timeline.

No state library; each view fetches its own data.

## 11. Security model

- Secrets only come from env vars (`DATABASE_URL`, `API_SECRET`, `TELEGRAM_*`, `SOCRATA_APP_TOKEN`). A `.env.example` is committed and `.env` is gitignored.
- Every `/api` route except `/health` requires `Authorization: Bearer <API_SECRET>`, compared with `hmac.compare_digest`. This is single-tenant V1; per-user accounts/roles (JWT + `users` table) come when a second user exists.
- Pydantic validates all request bodies and query params (enums for status, bounded page size). External payloads are parsed defensively and stored raw in JSONB, never interpolated into SQL (ORM only).
- CORS is restricted to `FRONTEND_ORIGIN`. Logs record counts, ids and durations, and never tokens or full payloads.
- Collectors never bypass auth, CAPTCHAs or robots.txt, and use only public data.

## 12. Deployment architecture

`docker-compose.yml` defines three services:
- `db`: postgres.
- `api`: uvicorn, which runs `alembic upgrade head` on start.
- `web`: nginx serving the built Vue app and proxying `/api`.

Scheduling uses host cron, or a compose `scheduler` service looping `python -m app.cli run` every N hours. A single VM is enough for V1. Add backups (`pg_dump`) before real use.

## 13. MVP phases

| Phase | Deliverable | Done when |
|---|---|---|
| **1 Core pipeline** | schema + migrations, FMCSA collector, 7 signal rules, scoring engine, leads, API, dashboard (3 pages) | `cli run` on live FMCSA data produces scored leads visible with evidence in UI |
| 2 Enrichment | email-domain + website providers, robots-aware | contacts with sources on qualifying leads |
| 3 Telegram | notifier, threshold, dedupe | one message per qualifying lead, disable-able |
| 4 More rules/sources | AuthHist (MC, revocations), fleet change & status change detection (snapshot diff), hiring signals | |
| 5 AI assist | evidence summaries stored as `origin='ai'`, clearly labeled | |
| 6 Analytics | conversion by signal type, weight tuning | |

## 14. Testing strategy

- **Unit** (no network): signal rules and scoring kinds on in-memory fixtures, and normalization using recorded FMCSA JSON samples.
- **Integration** (Postgres test DB): pipeline twice on the same fixtures → identical row counts (idempotency), rescore after a weight change, API auth (401 without token).
- **Contract smoke** (manual/CI-optional): a live `fetch(limit=5)` against Socrata to detect schema drift.
- Frontend: `vue-tsc` type check. Add E2E later.

## 15. Risks / limitations

- **"Problem" ≠ need**: an OOS inspection is a fact, not proof the company wants a service. Scores express priority, not truth, and the UI shows facts.
- **Data latency and quality**: FMCSA files update daily but inspections post with delays. Records can be amended (`change_date`), so upserts keep the latest version.
- **Rate limits**: Socrata throttles anonymous clients. The app token is free.
- **Contacts**: FMCSA registration phones and emails are often owner-operators' personal details. Treat them as registered business contacts, respect outreach laws (TCPA, CAN-SPAM) and honor opt-outs. That compliance workflow is not in V1.
- **Reviews/complaints/social**: many platforms prohibit scraping in their ToS. Those sources are only added via official APIs or licensed feeds.
- **Volume**: nationwide inspections run to thousands per day. V1 caps per-run volume and allows a state filter.

## 16. Exact first implementation steps

1. `backend/`: uv project, config, db, models, Alembic initial migration.
2. `collectors/fmcsa.py` + `collectors/base.py`, verified with a live `fetch(limit=20)`.
3. `signals/` rules + unit tests on recorded fixtures.
4. `scoring/` engine + defaults seeding + unit tests.
5. `pipeline.py` (upserts, signals, scoring, leads, events) + CLI + idempotency integration test.
6. FastAPI routes + auth.
7. `frontend/` with the three views wired to the API.
8. Live run: `python -m app.cli run --limit 300` → check the dashboard.
