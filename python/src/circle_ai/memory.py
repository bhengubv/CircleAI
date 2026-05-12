# memory.py
#
# Python port of the Circle.AI.Memory portable layer.
#
# Covers:
#   AffectState          — five-dimensional affect model (HER affect layer)
#   EpisodicMemoryEntry  — one user↔assistant exchange + embedding
#   FeedbackSignal       — explicit user rating of a B! response
#   PersonaState         — evolving persona state for a specific user
#   Goal / GoalStatus / GoalPriority — user goal tracking
#   IAffectStore         — load/save affect
#   IEpisodicMemoryStore — episodic retrieval
#   IPersonaStore        — load/save persona
#   IFeedbackStore       — append and query feedback signals
#   IGoalStore           — CRUD for goals

from __future__ import annotations

import uuid
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum
from typing import Optional


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# ---------------------------------------------------------------------------
# AffectState
# ---------------------------------------------------------------------------

@dataclass
class AffectState:
    """B!'s current emotional/engagement state — the HER affect layer.

    Five float dimensions, all 0.0–1.0.  Persisted per-user and injected
    into the system prompt to shape response tone and initiative.

    CRITICAL: the math in ``apply_positive_signal``, ``apply_negative_signal``,
    and ``apply_idle_decay`` is byte-identical to the C# reference
    implementation.  Do not change the constants.
    """

    user_id: str = "default"
    last_updated_utc: datetime = field(default_factory=_utc_now)

    # 0=bored, 1=fascinated
    curiosity: float = 0.5
    # 0=disengaged, 1=fully engaged
    engagement: float = 0.5
    # 0=confident, 1=confused
    uncertainty: float = 0.2
    # 0=stranger, 1=deep rapport
    rapport: float = 0.0
    # 0=subdued, 1=energetic
    energy: float = 0.5

    # ------------------------------------------------------------------
    # Signal application  (math must match C# exactly — compare ≤ 1e-6)
    # ------------------------------------------------------------------

    def apply_positive_signal(self) -> None:
        """Apply a positive interaction: nudge Engagement and Rapport up."""
        self.engagement  = min(1.0, self.engagement  + 0.02)
        self.rapport     = min(1.0, self.rapport     + 0.01)
        self.uncertainty = max(0.0, self.uncertainty - 0.02)
        self.last_updated_utc = _utc_now()

    def apply_negative_signal(self) -> None:
        """Apply a negative interaction: nudge Engagement down."""
        self.engagement  = max(0.0, self.engagement  - 0.03)
        self.uncertainty = min(1.0, self.uncertainty + 0.03)
        self.last_updated_utc = _utc_now()

    def apply_idle_decay(self, idle_hours: float) -> None:
        """Apply idle-time decay: Engagement and Energy drift toward 0.5."""
        decay = min(0.3, idle_hours * 0.02)
        self.engagement = self._lerp(self.engagement, 0.5, decay)
        self.energy     = self._lerp(self.energy,     0.5, decay)
        self.last_updated_utc = _utc_now()

    @staticmethod
    def _lerp(a: float, b: float, t: float) -> float:
        t = max(0.0, min(1.0, t))
        return a + (b - a) * t

    # ------------------------------------------------------------------
    # System-prompt injection
    # ------------------------------------------------------------------

    def to_system_prompt_hint(self) -> str:
        """Compact affect hint for injection into the system prompt."""
        hints: list[str] = []

        if self.curiosity > 0.7:
            hints.append("You are deeply curious about this topic — ask a follow-up question.")
        if self.engagement > 0.7:
            hints.append("You are fully engaged — be enthusiastic and thorough.")
        if self.engagement < 0.3:
            hints.append("Keep your response brief and to the point.")
        if self.uncertainty > 0.6:
            hints.append("You are uncertain — ask a clarifying question before answering.")
        if self.rapport > 0.7:
            hints.append("You know this user well — use a warm, familiar tone.")
        if self.energy < 0.3:
            hints.append("Keep your response calm and measured.")
        if self.energy > 0.8:
            hints.append("You are energetic — be upbeat and concise.")

        if not hints:
            return ""
        return "[Affect state]\n" + "\n".join(hints) + "\n"


# ---------------------------------------------------------------------------
# EpisodicMemoryEntry
# ---------------------------------------------------------------------------

@dataclass
class EpisodicMemoryEntry:
    """A single recorded episode (one user↔assistant exchange)."""

    id: uuid.UUID = field(default_factory=uuid.uuid4)
    recorded_at_utc: datetime = field(default_factory=_utc_now)
    user_text: str = ""
    assistant_text: str = ""
    app_context: Optional[str] = None
    # L2-normalised embedding; None if embedding backend was unavailable
    embedding: Optional[list[float]] = None
    # Arbitrary key-value tags (e.g. locale, sentiment)
    tags: Optional[dict[str, str]] = None


# ---------------------------------------------------------------------------
# FeedbackSignal
# ---------------------------------------------------------------------------

class FeedbackPolarity(Enum):
    """Polarity of a user feedback signal."""
    Positive   =  1
    Negative   = -1
    Correction =  0


@dataclass
class FeedbackSignal:
    """A single user-feedback event tied to a specific B! response."""

    id: uuid.UUID = field(default_factory=uuid.uuid4)
    recorded_at_utc: datetime = field(default_factory=_utc_now)
    episode_id: Optional[uuid.UUID] = None
    user_text: str = ""
    assistant_text: str = ""
    polarity: FeedbackPolarity = FeedbackPolarity.Positive
    corrected_text: Optional[str] = None
    comment: Optional[str] = None


# ---------------------------------------------------------------------------
# PersonaState
# ---------------------------------------------------------------------------

@dataclass
class PersonaState:
    """B!'s dynamic persona state for a specific user.

    Persisted between sessions and injected into the system prompt to shape
    tone, vocabulary, and topical depth.
    """

    user_id: str = "default"
    last_updated_utc: datetime = field(default_factory=_utc_now)

    # Communication style
    verbosity: str = "balanced"   # "brief" | "balanced" | "detailed"
    formality: str = "neutral"    # "casual" | "neutral" | "formal"
    preferred_locale: Optional[str] = None  # IETF BCP-47; None = match device

    # Interest signals
    topic_weights: dict[str, float] = field(default_factory=dict)
    disfavoured_topics: set[str] = field(default_factory=set)

    # Interaction stats
    total_interactions: int = 0
    positive_signals: int = 0
    negative_signals: int = 0

    @property
    def satisfaction_score(self) -> Optional[float]:
        """Derived satisfaction 0.0–1.0; None when fewer than 10 signals."""
        total = self.positive_signals + self.negative_signals
        if total < 10:
            return None
        return self.positive_signals / total

    def to_system_prompt_hint(self) -> str:
        """Compact persona instruction block for the B! system prompt."""
        hints: list[str] = []

        if self.verbosity != "balanced":
            hints.append(f"Keep responses {self.verbosity}.")

        if self.formality == "casual":
            hints.append("Use a casual, friendly tone.")
        elif self.formality == "formal":
            hints.append("Maintain a formal, professional tone.")

        if self.preferred_locale:
            hints.append(
                f"Respond in the language appropriate for locale {self.preferred_locale}."
            )

        if not hints:
            return ""
        return "[User preferences]\n" + "\n".join(hints) + "\n"


# ---------------------------------------------------------------------------
# Goal
# ---------------------------------------------------------------------------

class GoalStatus(Enum):
    """Lifecycle state of a Goal."""
    Active    = "Active"
    Completed = "Completed"
    Abandoned = "Abandoned"


class GoalPriority(Enum):
    """Relative importance of a Goal."""
    Low    = "Low"
    Normal = "Normal"
    High   = "High"


@dataclass(frozen=True)
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


# ---------------------------------------------------------------------------
# Store ABCs
# ---------------------------------------------------------------------------

class IAffectStore(ABC):
    """Loads and persists AffectState for a specific user."""

    @abstractmethod
    async def load_async(
        self, user_id: str, *, ct: Optional[object] = None
    ) -> AffectState:
        """Load affect state for *user_id*; return a fresh default when none found."""
        ...

    @abstractmethod
    async def save_async(
        self, state: AffectState, *, ct: Optional[object] = None
    ) -> None:
        """Persist the affect state (crash-safe — write-then-swap)."""
        ...


class IEpisodicMemoryStore(ABC):
    """Persistent store for episodic memories (exchanges + embeddings)."""

    @abstractmethod
    async def add_async(
        self, entry: EpisodicMemoryEntry, *, ct: Optional[object] = None
    ) -> None:
        """Append a new entry to the store."""
        ...

    @abstractmethod
    async def search_async(
        self,
        query_embedding: Optional[list[float]],
        top_k: int = 5,
        *,
        ct: Optional[object] = None,
    ) -> list[EpisodicMemoryEntry]:
        """Return top-k entries by cosine similarity; fall back to recency."""
        ...

    @abstractmethod
    async def get_recent_async(
        self, count: int = 10, *, ct: Optional[object] = None
    ) -> list[EpisodicMemoryEntry]:
        """Return the *count* most recent entries, newest-first."""
        ...

    @abstractmethod
    async def count_async(self, *, ct: Optional[object] = None) -> int:
        """Total number of entries currently stored."""
        ...

    @abstractmethod
    async def prune_older_than_async(
        self, cutoff: datetime, *, ct: Optional[object] = None
    ) -> int:
        """Remove all entries older than *cutoff*; return count removed."""
        ...


class IPersonaStore(ABC):
    """Loads and persists PersonaState for a specific user."""

    @abstractmethod
    async def load_async(
        self, user_id: str, *, ct: Optional[object] = None
    ) -> PersonaState:
        """Load persona for *user_id*; return a fresh default when none found."""
        ...

    @abstractmethod
    async def save_async(
        self, persona: PersonaState, *, ct: Optional[object] = None
    ) -> None:
        """Persist the persona (crash-safe)."""
        ...


class IFeedbackStore(ABC):
    """Persists user feedback signals for later analysis and adaptation."""

    @abstractmethod
    async def add_async(
        self, signal: FeedbackSignal, *, ct: Optional[object] = None
    ) -> None:
        """Record a new feedback signal."""
        ...

    @abstractmethod
    async def get_recent_async(
        self, count: int = 50, *, ct: Optional[object] = None
    ) -> list[FeedbackSignal]:
        """Return the *count* most recent signals, newest-first."""
        ...

    @abstractmethod
    async def count_async(self, *, ct: Optional[object] = None) -> int:
        """Total number of signals stored."""
        ...

    @abstractmethod
    async def positive_ratio_async(
        self, *, ct: Optional[object] = None
    ) -> Optional[float]:
        """Fraction of stored signals that are Positive (0.0–1.0); None when empty."""
        ...


class IGoalStore(ABC):
    """Persists and retrieves Goal records for a user."""

    @abstractmethod
    async def list_async(
        self, user_id: str, *, ct: Optional[object] = None
    ) -> list[Goal]:
        """Return all goals for *user_id* in any order."""
        ...

    @abstractmethod
    async def get_async(
        self, id: str, *, ct: Optional[object] = None
    ) -> Optional[Goal]:
        """Return the goal with *id*, or None if not found."""
        ...

    @abstractmethod
    async def upsert_async(
        self, goal: Goal, *, ct: Optional[object] = None
    ) -> Goal:
        """Insert or replace the goal; return the stored goal."""
        ...

    @abstractmethod
    async def delete_async(
        self, id: str, *, ct: Optional[object] = None
    ) -> None:
        """Delete the goal with *id*; no-op if not found."""
        ...

    @abstractmethod
    async def get_active_async(
        self, user_id: str, *, ct: Optional[object] = None
    ) -> list[Goal]:
        """Return all Active goals for *user_id*."""
        ...
