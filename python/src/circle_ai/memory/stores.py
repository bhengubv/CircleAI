from __future__ import annotations

from typing import Optional, Protocol, runtime_checkable

from .affect_state import AffectState
from .episodic_memory import EpisodicMemoryEntry
from .feedback_signal import FeedbackSignal
from .goal import Goal
from .persona_state import PersonaState

from datetime import datetime


@runtime_checkable
class IAffectStore(Protocol):
    """Loads and persists AffectState for a specific user."""

    async def load_async(
        self, user_id: str, *, ct: Optional[object] = None
    ) -> AffectState:
        """Load affect state for user_id; return a fresh default when none found."""
        ...

    async def save_async(
        self, state: AffectState, *, ct: Optional[object] = None
    ) -> None:
        """Persist the affect state (crash-safe — write-then-swap)."""
        ...


@runtime_checkable
class IEpisodicMemoryStore(Protocol):
    """Persistent store for episodic memories (exchanges + embeddings)."""

    async def add_async(
        self, entry: EpisodicMemoryEntry, *, ct: Optional[object] = None
    ) -> None:
        """Append a new entry to the store."""
        ...

    async def search_async(
        self,
        query_embedding: Optional[list[float]],
        top_k: int = 5,
        *,
        ct: Optional[object] = None,
    ) -> list[EpisodicMemoryEntry]:
        """Return top-k entries by cosine similarity; fall back to recency."""
        ...

    async def get_recent_async(
        self, count: int = 10, *, ct: Optional[object] = None
    ) -> list[EpisodicMemoryEntry]:
        """Return the count most recent entries, newest-first."""
        ...

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        """Total number of entries currently stored."""
        ...

    async def prune_older_than_async(
        self, cutoff: datetime, *, ct: Optional[object] = None
    ) -> int:
        """Remove all entries older than cutoff; return count removed."""
        ...


@runtime_checkable
class IFeedbackStore(Protocol):
    """Persists user feedback signals for later analysis and adaptation."""

    async def add_async(
        self, signal: FeedbackSignal, *, ct: Optional[object] = None
    ) -> None:
        """Record a new feedback signal."""
        ...

    async def get_recent_async(
        self, count: int = 50, *, ct: Optional[object] = None
    ) -> list[FeedbackSignal]:
        """Return the count most recent signals, newest-first."""
        ...

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        """Total number of signals stored."""
        ...

    async def positive_ratio_async(
        self, *, ct: Optional[object] = None
    ) -> Optional[float]:
        """Fraction of stored signals that are Positive (0.0–1.0); None when empty."""
        ...


@runtime_checkable
class IGoalStore(Protocol):
    """Persists and retrieves Goal records for a user."""

    async def list_async(
        self, user_id: str, *, ct: Optional[object] = None
    ) -> list[Goal]:
        """Return all goals for user_id in any order."""
        ...

    async def get_async(
        self, id: str, *, ct: Optional[object] = None
    ) -> Optional[Goal]:
        """Return the goal with id, or None if not found."""
        ...

    async def upsert_async(
        self, goal: Goal, *, ct: Optional[object] = None
    ) -> Goal:
        """Insert or replace the goal; return the stored goal."""
        ...

    async def delete_async(
        self, id: str, *, ct: Optional[object] = None
    ) -> None:
        """Delete the goal with id; no-op if not found."""
        ...

    async def get_active_async(
        self, user_id: str, *, ct: Optional[object] = None
    ) -> list[Goal]:
        """Return all Active goals for user_id."""
        ...


@runtime_checkable
class IPersonaStore(Protocol):
    """Loads and persists PersonaState for a specific user."""

    async def load_async(
        self, user_id: str, *, ct: Optional[object] = None
    ) -> PersonaState:
        """Load persona for user_id; return a fresh default when none found."""
        ...

    async def save_async(
        self, persona: PersonaState, *, ct: Optional[object] = None
    ) -> None:
        """Persist the persona (crash-safe)."""
        ...
