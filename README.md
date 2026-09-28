# Lead Intel — logistics/trucking lead intelligence

Public FMCSA data → companies → evidence-backed signals → explainable score → leads → dashboard.
Design: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Run locally

Requires Python 3.12+ with `uv`, Node 20+, PostgreSQL.

```bash
createdb leadintel && createdb leadintel_test

cd backend
cp .env.example .env            # set API_SECRET to a long random string
uv sync
uv run alembic upgrade head
uv run python -m app.cli run --limit 300    # collect from FMCSA + build leads
uv run uvicorn app.main:app --port 8000     # API, docs at http://localhost:8000/docs

cd ../frontend
npm install && npm run dev                  # http://localhost:5173, sign in with API_SECRET
```

## Everyday commands

| | |
|---|---|
| `uv run python -m app.cli run` | incremental collection (continues from the last run's cursor) |
| `uv run python -m app.cli reprocess` | re-derive signals and scores after changing rules/weights |
| `uv run pytest` | backend tests (uses `leadintel_test` DB) |
| `PUT /api/scoring-rules/{key}` | change a weight, then `POST /api/admin/reprocess` |

Target profile (`.env`): `TARGET_MIN_FLEET`, `TARGET_MAX_FLEET`, `TARGET_ACTIVE_ONLY`, `COLLECT_STATES`.
