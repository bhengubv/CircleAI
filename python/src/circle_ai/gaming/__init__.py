"""circle_ai.gaming — port of the CircleAI.Gaming assembly.

(3.3.0) Real domain types + in-memory board for the Gaming vertical: game
titles, play sessions, achievement unlocks, total/most-played time — plus the
static domain context. C# is the exact spec.

The C# ``GamingCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .gaming_domain_context import GamingDomainContext
from .gaming_primitives import (
    AchievementUnlock,
    GameTitle,
    IGamingBoard,
    InMemoryGamingBoard,
    PlaySession,
)

__all__ = [
    "GameTitle",
    "PlaySession",
    "AchievementUnlock",
    "IGamingBoard",
    "InMemoryGamingBoard",
    "GamingDomainContext",
]
