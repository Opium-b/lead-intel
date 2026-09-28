#!/usr/bin/env bash
# Deploy Lead Intel to an Ubuntu/Debian server over SSH.
#
#   deploy/deploy.sh ubuntu@SERVER_IP               # first run sets the server up; later runs update code
#   deploy/deploy.sh ubuntu@SERVER_IP --with-data   # also replace the server's database with this Mac's
#
# Works with root or a sudo user (Oracle/Google/AWS images use "ubuntu"). Open ports 22, 80 and 443 in the cloud
# provider's firewall; Docker-published ports bypass the host firewall, so none is configured here.
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

echo "==> server setup (Docker)"
ssh "$TARGET" 'set -e
  command -v docker >/dev/null || curl -fsSL https://get.docker.com | sudo sh
  # small free-tier machines run out of memory building images; 2 GB swap prevents that
  if [ ! -f /swapfile ] && [ "$(free -m | awk "/Mem:/ {print \$2}")" -lt 3000 ]; then
    sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile && sudo mkswap /swapfile >/dev/null && sudo swapon /swapfile
    echo "/swapfile none swap sw 0 0" | sudo tee -a /etc/fstab >/dev/null
  fi
  sudo mkdir -p /opt/leadintel && sudo chown "$(id -un)" /opt/leadintel'

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
ssh "$TARGET" "cd $DIR && sudo docker compose up -d --build --remove-orphans"

if [ "$WITH_DATA" = "--with-data" ]; then
  echo "==> copying this Mac's database to the server (replaces the server's data)"
  ssh "$TARGET" "cd $DIR && sudo docker compose stop api scheduler"
  "$PG_DUMP" --no-owner --no-privileges -Fc leadintel \
    | ssh "$TARGET" "cd $DIR && sudo docker compose exec -T db pg_restore -U leadintel -d leadintel --clean --if-exists --no-owner"
  ssh "$TARGET" "cd $DIR && sudo docker compose start api scheduler"
fi

SITE=$(ssh "$TARGET" "grep ^SITE_ADDRESS= $DIR/.env | cut -d= -f2-")
echo "==> waiting for $SITE"
for _ in $(seq 1 30); do
  if curl -fsS "$SITE/api/health" >/dev/null 2>&1; then echo "up: $SITE"; exit 0; fi
  sleep 5
done
echo "not answering yet; check: ssh $TARGET 'cd $DIR && sudo docker compose ps && sudo docker compose logs --tail 50'" >&2
exit 1
