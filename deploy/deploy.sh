#!/usr/bin/env bash
# Deploy Lead Intel to an Ubuntu/Debian server over SSH.
#
#   deploy/deploy.sh root@SERVER_IP               # first run sets the server up; later runs update code
#   deploy/deploy.sh root@SERVER_IP --with-data   # also replace the server's database with this Mac's
#
# Server settings are created once and never overwritten: /opt/leadintel/.env (generated DB password, site address)
# and /opt/leadintel/backend/.env (copied from this Mac on the first run).
set -euo pipefail

TARGET=${1:?usage: deploy/deploy.sh user@host [--with-data]}
WITH_DATA=${2:-}
HOST=${TARGET#*@}
DIR=/opt/leadintel
PG_DUMP=${PG_DUMP:-/opt/homebrew/opt/postgresql@18/bin/pg_dump}
cd "$(dirname "$0")/.."

echo "==> server setup (Docker, firewall)"
ssh "$TARGET" 'set -e
  command -v docker >/dev/null || curl -fsSL https://get.docker.com | sh
  if command -v ufw >/dev/null; then ufw allow OpenSSH >/dev/null; ufw allow 80/tcp >/dev/null; ufw allow 443/tcp >/dev/null; ufw --force enable >/dev/null; fi
  mkdir -p /opt/leadintel'

echo "==> code"
rsync -az --delete --exclude .git --exclude node_modules --exclude .venv --exclude dist --exclude __pycache__ \
  --exclude .pytest_cache --exclude .env --exclude backups ./ "$TARGET:$DIR/"

echo "==> settings (first run only)"
if ! ssh "$TARGET" "test -f $DIR/.env"; then
  ssh "$TARGET" "umask 077; printf 'POSTGRES_PASSWORD=%s\nSITE_ADDRESS=https://%s.sslip.io\nCOLLECT_EVERY_HOURS=6\n' \
    \"\$(openssl rand -hex 24)\" '${HOST//./-}' > $DIR/.env"
fi
if ! ssh "$TARGET" "test -f $DIR/backend/.env"; then
  scp -q backend/.env "$TARGET:$DIR/backend/.env"
  ssh "$TARGET" "chmod 600 $DIR/backend/.env"
fi

echo "==> build + start"
ssh "$TARGET" "cd $DIR && docker compose up -d --build --remove-orphans"

if [ "$WITH_DATA" = "--with-data" ]; then
  echo "==> copying this Mac's database to the server (replaces the server's data)"
  ssh "$TARGET" "cd $DIR && docker compose stop api scheduler"
  "$PG_DUMP" --no-owner --no-privileges -Fc leadintel \
    | ssh "$TARGET" "cd $DIR && docker compose exec -T db pg_restore -U leadintel -d leadintel --clean --if-exists --no-owner"
  ssh "$TARGET" "cd $DIR && docker compose start api scheduler"
fi

SITE=$(ssh "$TARGET" "grep ^SITE_ADDRESS= $DIR/.env | cut -d= -f2-")
echo "==> waiting for $SITE"
for _ in $(seq 1 30); do
  if curl -fsS "$SITE/api/health" >/dev/null 2>&1; then echo "up: $SITE"; exit 0; fi
  sleep 5
done
echo "not answering yet; check: ssh $TARGET 'cd $DIR && docker compose ps && docker compose logs --tail 50'" >&2
exit 1
