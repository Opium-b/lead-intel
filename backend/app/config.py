from functools import lru_cache

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    database_url: str = "postgresql+psycopg://localhost/leadintel"
    api_secret: str = Field(min_length=16)
    frontend_origin: str = "http://localhost:5173"

    socrata_app_token: str | None = None
    collect_states: list[str] = []  # empty = nationwide
    collect_overlap_days: int = 3
    collect_initial_lookback_days: int = 14

    # Target profile: who we collect history for and turn into leads
    target_min_fleet: int = 5
    target_max_fleet: int = 500
    target_new_carrier_min_fleet: int = 1  # carriers registered in the last 180 days may be smaller
    target_active_only: bool = True

    lead_min_score: int = 1  # companies scoring below this don't become leads

    # Phase 2 enrichment: website + contacts for leads at/above this score, re-checked every TTL days
    enrich_min_score: int = 30
    enrich_ttl_days: int = 30
    enrich_limit: int = 100  # companies per run
    brave_api_key: str | None = None  # optional: web search when the registered email is free-mail

    # Phase 5 AI briefs (Claude). Off unless a key is set; each brief costs roughly $0.02-0.05.
    anthropic_api_key: str | None = None
    ai_min_score: int = 60
    ai_limit: int = 25  # API calls per run

    telegram_bot_token: str | None = None
    telegram_chat_id: str | None = None
    notify_min_score: int = 70
    notify_limit: int = 20  # messages per run; the rest go out on later runs


@lru_cache
def get_settings() -> Settings:
    return Settings()
