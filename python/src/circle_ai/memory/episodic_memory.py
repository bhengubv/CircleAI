from __future__ import annotations

import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Optional


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass
class EpisodicMemoryEntry:
    """A single recorded episode (one user-assistant exchange)."""

    id: uuid.UUID = field(default_factory=uuid.uuid4)
    recorded_at_utc: datetime = field(default_factory=_utc_now)
    user_text: str = ""
    assistant_text: str = ""
    app_context: Optional[str] = None
    embedding: Optional[list[float]] = None
    tags: Optional[dict[str, str]] = None
