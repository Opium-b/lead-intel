# Deploying Lead Intel

One small VPS runs everything with Docker Compose: `db` (Postgres 18), `api` (FastAPI; applies migrations on start),
`web` (Caddy: serves the dashboard, proxies `/api`, gets HTTPS certificates automatically), `scheduler`
(`cli run` every `COLLECT_EVERY_HOURS`: collect → enrich → AI briefs → Telegram) and `backup` (daily `pg_dump`,
14 kept in `/opt/leadintel/backups`).

## First deploy

1. Rent an Ubuntu 24.04 VPS (1 vCPU / 2 GB is enough) and add this Mac's SSH key (`~/.ssh/id_ed25519.pub`) when creating it.
2. From the repo root: `deploy/deploy.sh root@SERVER_IP --with-data`
3. Open `https://SERVER-IP-WITH-DASHES.sslip.io` and sign in with `API_SECRET` from `backend/.env`.

Without a domain the site uses `sslip.io`, a free DNS name that maps to the IP, so Let's Encrypt can issue a real
certificate. With a domain: point an A record at the server, set `SITE_ADDRESS=https://your.domain` in
`/opt/leadintel/.env`, then `docker compose up -d`.

## Updating

`deploy/deploy.sh root@SERVER_IP`: syncs code and rebuilds. Server settings and data are kept.

## Everyday server commands (in `/opt/leadintel`)

| | |
|---|---|
| `docker compose ps` / `docker compose logs -f scheduler` | status / watch collection runs |
| `docker compose exec api python -m app.cli run` | run a collection now |
| `nano backend/.env && docker compose up -d` | change app settings (keys, thresholds) |
| `gunzip -c backups/leadintel-DATE.sql.gz \| docker compose exec -T db psql -U leadintel leadintel` | restore a backup (into an empty database) |

Once the server runs the scheduler, don't also run `cli run` or `cli notify` on your Mac against its own database,
or the same leads get alerted from both.
