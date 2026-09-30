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
                                 PostgreSQL ◄── ASP.NET Core (/api + Blazor dashboard, one app)
```

One C# program (`src/LeadIntel`): `dotnet run` serves the Blazor dashboard and the REST API; `dotnet run -- run` (and the other CLI commands in `Cli.cs`) runs the same pipeline code once; with `COLLECT_EVERY_HOURS > 0` an in-process `BackgroundService` runs it on a timer. No queue, no separate workers.

## 2. Technology choices

| Concern | Choice | Why |
|---|---|---|
| Language / runtime | C# 14 on .NET 10 | one language for backend, dashboard and tests |
| API | ASP.NET Core Minimal APIs | small, typed endpoints; records as request/response models |
| Dashboard | Blazor (interactive server rendering) | UI in C#/Razor, calls the services directly, no JS build |
| Data access | EF Core 10 + Npgsql (+ raw SQL for bulk `ON CONFLICT` upserts) | LINQ for queries, SQL where Postgres features matter |
| Schema | `Data/schema.sql`, applied on an empty database at startup | one reviewed DDL file |
| DB | PostgreSQL 16+ (JSONB, `ON CONFLICT`) | idempotent upserts, flexible evidence payloads |
| HTTP | `HttpClient` via `IHttpClientFactory` (typed clients) | timeouts, retries, per-source headers |
| Config | env vars / `.env` → `Settings` record | secrets never in code |
| Other libraries | AngleSharp (HTML), ClosedXML (Excel), Anthropic C# SDK (AI briefs) | |
| Scheduling | `Scheduler` BackgroundService, or launchd/cron calling the CLI | zero infra; swap for Hangfire/Quartz when needed |
| Tests | xUnit; `WebApplicationFactory` against a real Postgres test DB | upsert/conflict logic must be tested on the real engine |
| Packaging | `dotnet` CLI (NuGet), Docker Compose (deploy) | |

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

## 4. API design (all under `/api`; the dashboard's sign-in cookie or `Authorization: Bearer API_SECRET`)

```
GET   /api/health                         (public)
GET   /api/stats/overview                 counts + recent activity
GET   /api/leads?q=&state=&status=&min_score=&signal_type=&sort=-score&page=&page_size=
GET   /api/leads/{id}                     company, signals(+evidence), breakdown, contacts, events
PATCH /api/leads/{id}                     {status, channel?, note?}
POST  /api/leads/{id}/summary             (re)write the AI brief
POST  /api/leads/{id}/notes               {text}
GET   /api/stats/analytics                win rates, lift, weight suggestions
GET   /api/service-lines                  what we sell
GET   /api/scoring-rules                  PUT /api/scoring-rules/{key}  {weight, enabled, params}
POST  /api/admin/reprocess                recompute all signals and scores after rule changes
POST  /api/admin/collect                  {source, since?, limit?} → runs pipeline in background
GET   /api/admin/runs                     collector run history
```
Endpoints are mapped in `Program.cs` (`Api.MapApi`); bad input → 422, unknown lead → 404, missing/wrong secret → 401.

## 5. Collector architecture

```csharp
public class FmcsaCollector
{
    Task<RawFmcsa> FetchAsync(DateOnly since, int limit);           // raw Socrata rows
    static NormalizedBatch Normalize(RawFmcsa raw);                 // CompanyRecord[] + EventRecord[]
}
```
- `CompanyRecord`: identity fields (dot/mc/name/state…) plus `attributes`.
- `EventRecord`: `record_type`, `external_id`, `company_dot`, `observed_at`, `source_url`, `payload`.
- The pipeline only knows these two types, so a new source is one new collector class (see `Collectors/Records.cs`).
- **V1 source, FMCSA open data** (data.transportation.gov, Socrata, public, no auth; optional `SOCRATA_APP_TOKEN` raises rate limits):
  - `fx4q-ay7w` Vehicle Inspection File: inspections with violations/OOS since the cursor. This is the **discovery driver**: problem-first, not "scrape everyone".
  - `az4n-8mr2` Company Census: company profile for the discovered DOTs, plus a "new carriers" feed (`add_date ≥ cursor`).
  - `aayw-vxb3` Crash File: crashes for discovered DOTs (last 12 months).
  - `9mw4-x3tu` AuthHist and `qh9u-swkp` ActPendInsur (legacy L&I system): MC dockets, grants/revocations, insurance on file.
  - **Motus** (FMCSA's new registration system; new carriers exist only here). "All With History" sets lag a few
    days, so each is paired with its daily-difference set: AuthHist `yu5v-wbh6`+`dm5j-zc6c`, RevokeSuspend
    `wb4f-neki`+`e67p-xyd5`, Carrier `inys-ebih`+`nakq-58th` (which filings are on file), plus Insur `c5y8-a4uz`
    and InsHist `3uet-3z4i`. These are the structured form of FMCSA's daily registration-decision letters.
  - Discovery per run, besides inspections and new census adds: authorities **granted** since the cursor, authorities
    turned **pending**, **suspended for lapsed insurance** (fixed 60-day window: still-suspended carriers stay leads),
    **suspension notices** served, and insurance **cancellations** effective between 7 days ago and 45 days ahead
    (Motus `CANCEL` rows and legacy pending cancellations). Carriers found this way may have 1+ trucks.
- The cursor is a date with an N-day overlap, stored in `collector_runs`. Overlap is safe because every write is an upsert.
- Etiquette: page size ≤ 1000, timeouts, retry with backoff, identifying User-Agent.

## 6. Signal / rule architecture

```csharp
public interface ISignalRule
{
    IEnumerable<SignalDraft> Evaluate(IReadOnlyList<Fact> facts, DateOnly today);
}
```
Registry list: `SignalEngine.Rules` in `Signals/Rules.cs`. Rules include:

| Rule | Signal type | Fires when | Severity |
|---|---|---|---|
| OutOfServiceRule | `OUT_OF_SERVICE` | inspection with `oos_total>0` in last 365d | high |
| InspectionViolationRule | `INSPECTION_VIOLATION` | inspection with `viol_total>0`, no OOS | medium |
| RepeatedViolationRule | `REPEATED_VIOLATIONS` | ≥3 violating inspections in 365d | high |
| CrashRule | `CRASH` | crash in last 365d (fatal → critical, injury → high) | high/critical |
| NewCarrierRule | `NEW_CARRIER` | census `add_date` within 180d | low |
| StaleRegistrationRule | `STALE_MCS150` | MCS-150 last updated > 24 months ago (biennial update is required) | medium |
| InactiveStatusRule | `INACTIVE_STATUS` | census status ≠ active | medium |
| CensusChangeRule | `FLEET_GROWTH` / `FLEET_SHRINK` / `DRIVER_GROWTH` / `REACTIVATED` | a `census_change` fact in 365d: power units or drivers moved by ≥ max(2, 10%), or status → active | medium/low |
| HiringRule | `HIRING_DRIVERS` | driver-hiring language on the company's own website in the last 90d | medium |

**Change detection:** census rows are overwritten on upsert, so `LeadPipeline.CensusChangesAsync()` diffs each incoming row against the stored one first and records every change of `power_units`, `total_drivers` or `status_code` as a `census_change` source record (`<dot>:<field>:<from>-><to>`). Signals appear only as carriers file MCS-150 updates, so they accumulate over time.
**Hiring:** enrichment records one `website`/`hiring` source record per company (page + phrase). Job boards are not scraped (ToS).

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

`score = min(100, Σ matched weights)`. The breakdown is stored as `[{key,label,points,reason}]`, for example `+15 Recent violation — inspection 2026-09-12 (TX)`. Seed weights ship in `Scoring/ScoringEngine.cs` (`DefaultRules`) and are inserted if missing; after that the DB is the source of truth. `POST /api/admin/reprocess` (or `dotnet run -- reprocess`) recomputes all scores.

## 8. Enrichment architecture (phase 2)

`Services/Enrichment.cs`. `Enricher.EnrichAsync(company, emails, phones) -> (website, contacts, hiring)`, each contact with `source` and the exact page `source_url`. It runs at the end of every collector run and via `dotnet run -- enrich`, only for leads ≥ `ENRICH_MIN_SCORE`, best score first, at most `ENRICH_LIMIT` per run, and at most once per `ENRICH_TTL_DAYS` per company (`companies.enriched_at`).
1. `fmcsa_census` (in the collector): registered phone, cell phone, fax, email, company officers (`person`) and mailing address when it differs from the physical one.
2. `email_domain`: a non-free-mail registered email domain → `https://<domain>/`, accepted if it answers (the registration itself is the evidence).
3. `web_search` (only when `BRAVE_API_KEY` is set and step 2 found nothing): Brave Search for `"name" city state`. Directory/social/.gov hosts are skipped; a result's site is accepted **only if one of its pages shows the USDOT number or registered phone**.
4. `website`: homepage plus up to 2 linked contact pages, **only if robots.txt allows**; extracts emails and phones (links and plain text), Facebook/LinkedIn/Instagram/X and WhatsApp links, and contact forms (a page with a message box → `form`).

No guessing: nothing is stored that was not observed, and every contact records where it was seen. Pages are capped at 2 MB, requests time out after 10 s, and private/loopback addresses are refused at connect time (`SocketsHttpHandler.ConnectCallback`, which also covers DNS rebinding). A failed company is not marked enriched, so it is retried next run; a new contact adds an `ENRICHED` lead event and triggers a re-score.

## 8b. AI briefs (phase 5)

`Services/AiBriefs.cs`: one Claude call via the Anthropic C# SDK (`claude-opus-5-5`, structured JSON output, medium effort, server-side refusal fallback) per lead → `{summary, talking_points, opener}` stored in `leads.ai_summary` with the answering model and an input hash. It is interpretation, never a fact: it doesn't create signals or change scores, and the dashboard and Telegram label it as AI.
- Input is built only from stored facts (company profile, score reasons, matched services, top 25 signals) and the registered contact person's name. **Phone numbers and emails are never sent.** The prompt uses absolute dates only, so its hash changes only when the facts do, and unchanged leads are never re-sent.
- Runs after enrichment in every collector run, for leads ≥ `AI_MIN_SCORE` (60), best first, at most `AI_LIMIT` (25) calls per run; also `dotnet run -- summarize` and `POST /api/leads/{id}/summary` (the dashboard's Regenerate button). Off unless `ANTHROPIC_API_KEY` is set.
- Refusals and truncated answers are skipped, not stored; API errors are logged and never fail a run. Roughly 1.2k input tokens per lead, about $0.02–0.05 each.

## 8c. Analytics (phase 6)

`LeadService.AnalyticsAsync()` (pure math in `LeadService.Compute`) → `GET /api/stats/analytics` → the dashboard's Analytics page. Outcomes come from lead statuses: won = QUALIFIED/CONVERTED, lost = DECLINED/DISQUALIFIED, worked = anything past NEW/REVIEWED.
- Funnel by status; win rate and **lift** per signal type (Laplace-smoothed win rate with the signal ÷ overall, so 1/1 isn't read as certain); win rate by score band (does the score predict wins?); attempts / no-answer / won / lost per contact channel from the contact log.
- Weight suggestions for enabled, positive `signal_type` rules once ≥ 10 leads are decided and ≥ 5 carry the signal: `suggested = round(weight × lift / 5) × 5`. They are advice; the page's Apply button calls `PUT /api/scoring-rules/{key}` then `POST /api/admin/reprocess`.

## 9. Telegram architecture (phase 3)

`Services/Telegram.cs`: `TelegramNotifier` (Bot API `sendMessage`/`sendDocument`, HTML parse mode, every value escaped) and `LeadNotifications.NotifyQualifiedLeadsAsync()`.
- Runs at the end of every collector run, after enrichment (so messages carry fresh contacts), only when `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHAT_ID` are set. Also `dotnet run -- notify`, and `dotnet run -- notify --test` to check the bot setup.
- Send condition: `score ≥ NOTIFY_MIN_SCORE AND notified_at IS NULL`, best score first, at most `NOTIFY_LIMIT` per run, ~1 msg/s. `notified_at` and a `NOTIFIED` lead event are written after a successful send, so each lead is sent at most once.
- Message: name, score, USDOT/MC/location/fleet, top 4 score reasons, top 3 service lines to pitch, contact person/phone/email/website, dashboard link.
- Failures are logged (without the token, which lives in the URL) and swallowed; an unsent lead is retried next run. Lead generation never depends on Telegram.

## 10. Dashboard architecture (Blazor)

`src/LeadIntel/Components/` is a Blazor Web App in the same process as the API. Pages use interactive server rendering: the browser keeps a WebSocket to the server, and components call `LeadService` directly (no HTTP round trip, no JavaScript to write). `LeadService` opens one `DbContext` per call, so long-lived components never share a context.
- `Login.razor`: static server-rendered form; checks `API_SECRET` and signs in with a cookie (30 days, sliding).
- `OverviewPage`: stat tiles, recent activity, signals by type, last collection run.
- `LeadsPage`: search, state/status/service-line/signal/min-score filters, sortable columns, paging (server-side).
- `LeadDetailPage`: AI brief, contact log (status + channel + note → Telegram), what to pitch, company card, score breakdown, contacts with sources, signals with evidence, notes and timeline.
- `AnalyticsPage`: funnel, win rates by score band / signal / channel, one-click weight suggestions.

Styles are one plain CSS file (`wwwroot/app.css`), light and dark.

## 11. Security model

- Secrets only come from env vars (`DATABASE_URL`, `API_SECRET`, `TELEGRAM_*`, `SOCRATA_APP_TOKEN`). A `.env.example` is committed and `.env` is gitignored.
- Every `/api` route except `/health` requires `Authorization: Bearer <API_SECRET>`, compared in constant time (`CryptographicOperations.FixedTimeEquals`). The dashboard exchanges it once for an auth cookie; forms carry antiforgery tokens. This is single-tenant V1; per-user accounts/roles (JWT + `users` table) come when a second user exists.
- `Validate` checks every filter and body (known statuses/channels, patterns for state/signal/service line, bounded page size). External payloads are parsed defensively and stored raw in JSONB; SQL is always parameterized (EF Core or `NpgsqlParameter`).
- The dashboard and API share one origin, so no CORS is needed. Links built from scraped data are only rendered when they are http(s). Logs record counts, ids and durations, and never tokens or full payloads.
- Collectors never bypass auth, CAPTCHAs or robots.txt, and use only public data.

## 12. Deployment architecture

`docker-compose.yml` (see `Dockerfile`, a two-stage .NET SDK → ASP.NET runtime build) defines:
- `db`: Postgres 18.
- `app`: the C# app (dashboard + API + scheduler every `COLLECT_EVERY_HOURS`); creates the tables on an empty database.
- `web`: Caddy with automatic HTTPS, reverse-proxying everything (including the Blazor WebSocket) to `app`.
- `backup`: a daily `pg_dump`, 14 kept.

On a Mac without a server, `deploy/mac-daily.sh` (launchd, 20:00) runs `dotnet run -- run` and `report --once`.

## 13. MVP phases

| Phase | Deliverable | Done when |
|---|---|---|
| **1 Core pipeline** | schema + migrations, FMCSA collector, 7 signal rules, scoring engine, leads, API, dashboard (3 pages) | `cli run` on live FMCSA data produces scored leads visible with evidence in UI |
| 2 Enrichment | email-domain + website providers, robots-aware | contacts with sources on qualifying leads |
| 3 Telegram | notifier, threshold, dedupe | one message per qualifying lead, disable-able |
| 4 More rules/sources | AuthHist (MC, revocations), fleet change & status change detection (snapshot diff), hiring signals | ✅ change + hiring signals scored and pitched |
| 5 AI assist | evidence summaries stored as `origin='ai'`, clearly labeled | ✅ `leads.ai_summary` briefs (see §8b) |
| 6 Analytics | conversion by signal type, weight tuning | ✅ `LeadService.Compute`, `GET /api/stats/analytics`, Analytics page |

## 14. Testing strategy

- **Unit** (no network): signal rules and scoring kinds on in-memory fixtures, and normalization using recorded FMCSA JSON samples.
- **Integration** (Postgres test DB): pipeline twice on the same fixtures → identical row counts (idempotency), rescore after a weight change, API auth (401 without token).
- **Contract smoke** (manual/CI-optional): a live `fetch(limit=5)` against Socrata to detect schema drift.
- Dashboard: compiled Razor (type-checked by the C# compiler); checked by hand in a browser. Add bUnit/Playwright tests later.

## 15. Risks / limitations

- **"Problem" ≠ need**: an OOS inspection is a fact, not proof the company wants a service. Scores express priority, not truth, and the UI shows facts.
- **Data latency and quality**: FMCSA files update daily but inspections post with delays. Records can be amended (`change_date`), so upserts keep the latest version.
- **Rate limits**: Socrata throttles anonymous clients. The app token is free.
- **Contacts**: FMCSA registration phones and emails are often owner-operators' personal details. Treat them as registered business contacts, respect outreach laws (TCPA, CAN-SPAM) and honor opt-outs. That compliance workflow is not in V1.
- **Reviews/complaints/social**: many platforms prohibit scraping in their ToS. Those sources are only added via official APIs or licensed feeds.
- **Volume**: nationwide inspections run to thousands per day. V1 caps per-run volume and allows a state filter.

## 16. Project layout

```
LeadIntel.slnx
src/LeadIntel/
  Program.cs              startup, DI, REST API, sign-in cookie
  Cli.cs                  dotnet run -- run | refresh | enrich | notify | summarize | report | reprocess | reset-scoring
  Config/Settings.cs      env vars / .env
  Data/                   EF Core entities, DbContext, schema.sql
  Collectors/             FMCSA + Motus (Socrata) → NormalizedBatch
  Signals/                ISignalRule + 23 rules
  Scoring/                ScoringEngine (weights in DB), ServicesCatalog (what we sell)
  Pipeline/               LeadPipeline (upserts, signals, score, lead events), CollectionRunner
  Services/               LeadService, Enrichment, AiBriefs, Telegram, DailyReport, Jobs/Scheduler
  Components/             Blazor dashboard (App, Routes, Layout, Pages)
  wwwroot/app.css
tests/LeadIntel.Tests/    xUnit: CoreTests, ServiceTests, ApiTests
```
