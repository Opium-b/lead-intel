import os

os.environ["DATABASE_URL"] = os.environ.get("TEST_DATABASE_URL", "postgresql+psycopg://localhost/leadintel_test")
os.environ.setdefault("API_SECRET", "test-secret-test-secret")

import pytest  # noqa: E402

from app.db import Base, SessionLocal, engine  # noqa: E402
import app.models  # noqa: E402,F401


@pytest.fixture
def db():
    Base.metadata.drop_all(engine)
    Base.metadata.create_all(engine)
    with SessionLocal() as s:
        yield s
