from __future__ import annotations

import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum
from typing import Optional


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class FeedbackPolarity(Enum):
    """Polarity of a user feedback signal."""
    POSITIVE = 1
    NEGATIVE = -1
    CORRECTION = 0


@dataclass
class FeedbackSignal:
    """A single user-feedback event tied to a specific B! response."""

    id: uuid.UUID = field(default_factory=uuid.uuid4)
    recorded_at_utc: datetime = field(default_factory=_utc_now)
    episode_id: Optional[uuid.UUID] = None
    user_text: str = ""
    assistant_text: str = ""
    polarity: FeedbackPolarity = FeedbackPolarity.POSITIVE
    corrected_text: Optional[str] = None
    comment: Optional[str] = None
