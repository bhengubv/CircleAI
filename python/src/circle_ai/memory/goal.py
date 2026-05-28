from __future__ import annotations

import copy
from dataclasses import dataclass
from datetime import datetime
from enum import Enum
from typing import Optional


class GoalStatus(Enum):
    """Lifecycle state of a Goal."""
    ACTIVE = "Active"
    COMPLETED = "Completed"
    ABANDONED = "Abandoned"


class GoalPriority(Enum):
    """Relative importance of a Goal."""
    LOW = "Low"
    NORMAL = "Normal"
    HIGH = "High"


@dataclass
class Goal:
    """A user goal that B! tracks and proactively helps with."""

    id: str
    user_id: str
    title: str
    description: str
    status: GoalStatus
    priority: GoalPriority
    created_utc: datetime
    due_utc: Optional[datetime] = None
    completed_utc: Optional[datetime] = None
    notes: Optional[str] = None
    progress: float = 0.0  # [0.0, 1.0]

    def advance_progress(self, delta: float) -> "Goal":
        """Return a new Goal with progress clamped to [0.0, 1.0]."""
        g = copy.copy(self)
        g.progress = max(0.0, min(1.0, self.progress + delta))
        return g
