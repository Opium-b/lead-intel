"""company enriched_at

Revision ID: 2fa47bcc0bb8
Revises: aa963dd40d7c
Create Date: 2026-09-28 12:49:17.343296

"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


# revision identifiers, used by Alembic.
revision: str = '2fa47bcc0bb8'
down_revision: Union[str, Sequence[str], None] = 'aa963dd40d7c'
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    """Upgrade schema."""
    op.add_column('companies', sa.Column('enriched_at', sa.DateTime(timezone=True), nullable=True))


def downgrade() -> None:
    """Downgrade schema."""
    op.drop_column('companies', 'enriched_at')
