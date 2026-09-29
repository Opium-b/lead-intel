"""Motus source record ids include the USDOT number

Revision ID: 9d3f6a1c2e57
Revises: 7c1e2d9a4b10
Create Date: 2026-09-29 17:30:00.000000

Rows without a docket (voluntary suspension notices) shared one id per day across companies, so each
company's row overwrote the previous one. The old rows can't be re-keyed reliably (long ids were hashed),
so they're deleted; run `python -m app.cli refresh` afterwards to fetch them again under the new ids.
Signals keep working meanwhile (source_record_id is SET NULL) and are rebuilt by the refresh.
"""
from typing import Sequence, Union

from alembic import op


revision: str = '9d3f6a1c2e57'
down_revision: Union[str, Sequence[str], None] = '7c1e2d9a4b10'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.execute("DELETE FROM source_records WHERE source = 'fmcsa' AND external_id LIKE 'motus:%'")


def downgrade() -> None:
    pass  # raw facts are re-fetchable; nothing to restore
