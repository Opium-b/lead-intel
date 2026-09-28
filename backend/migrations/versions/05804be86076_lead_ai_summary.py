"""lead ai summary

Revision ID: 05804be86076
Revises: 2fa47bcc0bb8
Create Date: 2026-09-28 14:34:19.253819

"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import postgresql


# revision identifiers, used by Alembic.
revision: str = '05804be86076'
down_revision: Union[str, Sequence[str], None] = '2fa47bcc0bb8'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    """Upgrade schema."""
    op.add_column('leads', sa.Column('ai_summary', postgresql.JSONB(astext_type=sa.Text()), nullable=True))
    op.add_column('leads', sa.Column('ai_summary_at', sa.DateTime(timezone=True), nullable=True))


def downgrade() -> None:
    """Downgrade schema."""
    op.drop_column('leads', 'ai_summary_at')
    op.drop_column('leads', 'ai_summary')
