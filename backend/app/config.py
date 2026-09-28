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

    telegram_bot_token: str | None = None
    telegram_chat_id: str | None = None
    notify_min_score: int = 70


@lru_cache
def get_settings() -> Settings:
    return Settings()
