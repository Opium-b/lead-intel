"""hot_now covers the Motus insurance signals

Revision ID: 7c1e2d9a4b10
Revises: 05804be86076
Create Date: 2026-09-28 20:30:00.000000

"""
from typing import Sequence, Union

from alembic import op


revision: str = '7c1e2d9a4b10'
down_revision: Union[str, Sequence[str], None] = '05804be86076'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None

NEW = '["INSURANCE_SUSPENDED", "SUSPENSION_NOTICE", "INSURANCE_NEEDED"]'


def upgrade() -> None:
    # seed_rules never overwrites a stored rule, so extend the existing row's type list in place
    op.execute(f"""UPDATE scoring_rules SET params = jsonb_set(params, '{{types}}', (params->'types') || '{NEW}'::jsonb)
                   WHERE key = 'hot_now' AND NOT (params->'types') @> '["INSURANCE_NEEDED"]'::jsonb""")


def downgrade() -> None:
    op.execute("""UPDATE scoring_rules SET params = jsonb_set(params, '{types}', (SELECT coalesce(jsonb_agg(t), '[]')
                  FROM jsonb_array_elements(params->'types') t WHERE t #>> '{}' NOT IN
                  ('INSURANCE_SUSPENDED', 'SUSPENSION_NOTICE', 'INSURANCE_NEEDED'))) WHERE key = 'hot_now'""")
