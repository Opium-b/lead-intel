# Lead Intel — logistics/trucking lead intelligence (C# / .NET 10)

Public FMCSA data → companies → evidence-backed signals → explainable score → leads → dashboard, Telegram alerts and AI pre-call briefs.
Design: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Deploying: [docs/DEPLOY.md](docs/DEPLOY.md).

One ASP.NET Core app (`src/LeadIntel`): a **Blazor** dashboard, a **REST API** (Minimal APIs), and the data pipeline
(collectors, signal rules, scoring, enrichment, Telegram, Claude briefs, Excel report) — all C#. PostgreSQL via EF Core.

## Run locally

Requires the .NET 10 SDK and PostgreSQL.

```bash
createdb leadintel && createdb leadintel_test
cp src/LeadIntel/.env.example src/LeadIntel/.env   # then set API_SECRET (16+ chars)

cd src/LeadIntel
dotnet run -- run --limit 300        # collect from FMCSA + build leads (tables are created on first start)
dotnet run                           # dashboard + API at http://localhost:8000, sign in with API_SECRET
```

Open `LeadIntel.slnx` in Visual Studio, Rider or VS Code (C# Dev Kit) to browse and debug.

## Everyday commands (in `src/LeadIntel`)

| | |
|---|---|
| `dotnet run -- run` | incremental collection (continues from the last run's cursor), then enrich → AI briefs → Telegram |
| `dotnet run -- refresh` | re-fetch full history for every tracked company |
| `dotnet run -- enrich` | find websites + contacts for due leads |
| `dotnet run -- notify [--test]` | send pending Telegram alerts; `--test` checks the bot |
| `dotnet run -- summarize` | AI briefs for leads whose facts changed |
| `dotnet run -- report [--date YYYY-MM-DD] [--once]` | FMCSA daily registration decisions (8 categories) as Excel, sent to Telegram |
| `dotnet run -- reprocess` | re-derive signals and scores after changing rules/weights |
| `dotnet run -- reset-scoring` | restore default weights |
| `dotnet test` (repo root) | xUnit tests (the API test uses the `leadintel_test` database) |
| `deploy/mac-daily.sh` | daily `run` + `report --once` on a Mac (launchd `~/Library/LaunchAgents/com.leadintel.daily.plist`, 20:00) |

REST API: `Authorization: Bearer API_SECRET`, e.g. `GET /api/leads?min_score=70&signal_type=CRASH`, `PATCH /api/leads/{id}`,
`GET /api/stats/overview`, `PUT /api/scoring-rules/{key}` then `POST /api/admin/reprocess`. Full list in docs/ARCHITECTURE.md §4.

Settings (`src/LeadIntel/.env`): target profile `TARGET_MIN_FLEET`, `TARGET_MAX_FLEET`, `TARGET_ACTIVE_ONLY`, `COLLECT_STATES`;
enrichment `ENRICH_MIN_SCORE`, `ENRICH_TTL_DAYS`, `ENRICH_LIMIT`, optional `BRAVE_API_KEY`; Telegram `TELEGRAM_BOT_TOKEN`,
`TELEGRAM_CHAT_ID`, `NOTIFY_MIN_SCORE`, `NOTIFY_LIMIT`; AI briefs `ANTHROPIC_API_KEY`, `AI_MIN_SCORE`, `AI_LIMIT`;
`FRONTEND_ORIGIN` (dashboard address used in Telegram links); `COLLECT_EVERY_HOURS` (> 0: the app also collects on a timer).
