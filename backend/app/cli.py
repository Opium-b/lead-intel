"""Usage:
  python -m app.cli run [--source fmcsa] [--since YYYY-MM-DD] [--limit 500]   # collect + process
  python -m app.cli refresh                                                   # re-fetch tracked companies
  python -m app.cli enrich [--limit 100]                                      # websites + contacts for due leads
  python -m app.cli reprocess                                                 # re-derive signals/scores
  python -m app.cli reset-scoring                                             # restore default weights
"""
import argparse
import logging
from datetime import date

from app.db import SessionLocal
from app import scoring
from app.enrichment import enrich_leads
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
        elif args.cmd == "reset-scoring":
            scoring.reset_rules(db)
            logging.info("scoring rules reset to defaults; run 'reprocess' to apply")
        else:
            logging.info("reprocess: %s", process_companies(db))


if __name__ == "__main__":
    main()
