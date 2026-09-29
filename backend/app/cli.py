"""Usage:
  python -m app.cli run [--source fmcsa] [--since YYYY-MM-DD] [--limit 500]   # collect + process
  python -m app.cli refresh                                                   # re-fetch tracked companies
  python -m app.cli enrich [--limit 100]                                      # websites + contacts for due leads
  python -m app.cli notify [--test]                                           # send pending Telegram alerts
  python -m app.cli summarize [--limit 25]                                   # AI briefs for leads whose facts changed
  python -m app.cli report [--date YYYY-MM-DD] [--once]                       # FMCSA daily decisions Excel -> Telegram
  python -m app.cli reprocess                                                 # re-derive signals/scores
  python -m app.cli reset-scoring                                             # restore default weights
"""
import argparse
import logging
from datetime import date

from app.db import SessionLocal
from app import scoring
from app.ai import summarize_leads
from app.config import get_settings
from app.daily_report import send_daily_report
from app.enrichment import enrich_leads
from app.notify import TelegramNotifier, notify_qualified_leads
from app.pipeline import process_companies, refresh_companies, run_collection


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    logging.getLogger("httpx").setLevel(logging.WARNING)
    p = argparse.ArgumentParser(prog="app.cli")
    sub = p.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run")
    r.add_argument("--source", default="fmcsa")
    r.add_argument("--since", type=date.fromisoformat)
    r.add_argument("--limit", type=int, default=500)
    sub.add_parser("refresh")
    e = sub.add_parser("enrich")
    e.add_argument("--limit", type=int)
    n = sub.add_parser("notify")
    n.add_argument("--test", action="store_true", help="send one test message to check the bot setup")
    a = sub.add_parser("summarize")
    a.add_argument("--limit", type=int)
    rep = sub.add_parser("report")
    rep.add_argument("--date", type=date.fromisoformat, help="day to report (default: yesterday)")
    rep.add_argument("--once", action="store_true", help="skip if that day's report was already sent")
    sub.add_parser("reprocess")
    sub.add_parser("reset-scoring")
    args = p.parse_args()

    with SessionLocal() as db:
        if args.cmd == "run":
            run_collection(db, args.source, args.since, args.limit)
        elif args.cmd == "refresh":
            refresh_companies(db)
        elif args.cmd == "enrich":
            enrich_leads(db, limit=args.limit)
        elif args.cmd == "notify":
            s = get_settings()
            if not (s.telegram_bot_token and s.telegram_chat_id):
                raise SystemExit("Set TELEGRAM_BOT_TOKEN and TELEGRAM_CHAT_ID in .env first")
            if args.test:
                TelegramNotifier(s.telegram_bot_token, s.telegram_chat_id).send("✅ Lead Intel is connected.")
                logging.info("test message sent")
            else:
                notify_qualified_leads(db)
        elif args.cmd == "summarize":
            if summarize_leads(db, limit=args.limit) is None:
                raise SystemExit("Set ANTHROPIC_API_KEY in .env first")
        elif args.cmd == "report":
            send_daily_report(db, args.date, args.once)
        elif args.cmd == "reset-scoring":
            scoring.reset_rules(db)
            logging.info("scoring rules reset to defaults; run 'reprocess' to apply")
        else:
            logging.info("reprocess: %s", process_companies(db))


if __name__ == "__main__":
    main()
