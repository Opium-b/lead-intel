#!/bin/zsh
# Daily run on this Mac (launchd: ~/Library/LaunchAgents/com.leadintel.daily.plist):
# collect FMCSA decisions -> leads -> enrichment -> Telegram alerts, then the daily-decisions Excel to Telegram.
# ponytail: runs only while the Mac is on (launchd catches up after sleep); the server's scheduler replaces this.
set -u
export PATH="$HOME/.local/bin:/opt/homebrew/bin:$PATH"
cd "${0:A:h}/../backend" || exit 1
echo "=== $(date) ==="

# After an unclean shutdown postgres leaves postmaster.pid behind; if its PID now belongs to another
# process, postgres refuses to start forever. Remove it only when no postgres is actually running.
PGDATA=/opt/homebrew/var/postgresql@18
if ! pg_isready -q; then
  if [[ -f $PGDATA/postmaster.pid ]] && ! pgrep -x postgres >/dev/null; then
    echo "removing stale postmaster.pid"
    rm -f $PGDATA/postmaster.pid
  fi
  brew services restart postgresql@18
  for i in {1..30}; do pg_isready -q && break; sleep 1; done
fi

uv run python -m app.cli run || echo "run failed"
uv run python -m app.cli report --once || echo "report failed"
