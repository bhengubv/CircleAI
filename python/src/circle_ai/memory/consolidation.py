# memory/consolidation.py
#
# Hierarchical memory consolidation — the "sleep cycle" engine. Ported from
# CircleAI.Memory.Consolidation (C#): SleepKind, CoreMemory, DailyMemorySummary,
# SemanticMemoryCluster, PersonaDeltaSnapshot, the four tier stores, the
# HeuristicSummarizer, and the MemoryConsolidator orchestration engine. Mirrors
# the TypeScript pilot (memory/consolidation.ts).
#
# Promotes episodic -> daily -> weekly (semantic) -> monthly (persona delta) ->
# core, and enforces retention. All time decisions go through an injectable
# clock so tests are deterministic. This is the in-memory port: identical
# algorithms and formulas to the C# reference, no persistence.
#
# C# `DateOnly` is represented here as Python's native ``datetime.date`` — a
# perfect DateOnly analogue that compares correctly with ``<``/``<=``/``>=``, so
# the range/idempotency/prune comparisons carry over unchanged. C# `Guid` ->
# ``uuid.UUID`` (via ``uuid.uuid4()``).

from __future__ import annotations

import math
import uuid
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone
from enum import Enum
from typing import Callable, Optional, Protocol, runtime_checkable

from .episodic_memory import EpisodicMemoryEntry
from .persona_state import PersonaState
from .stores import IEpisodicMemoryStore, IPersonaStore


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# ─────────────────────────────────────────────────────────────────────────────
# SleepKind + CoreMemoryKind
# ─────────────────────────────────────────────────────────────────────────────


class SleepKind(Enum):
    """Which tier of hierarchical consolidation a tick should run."""

    Daily = "Daily"
    """End-of-day: collapse the day's episodic entries into a DailyMemorySummary."""
    Weekly = "Weekly"
    """End-of-week: cluster the week's daily summaries into semantic topic groups."""
    Monthly = "Monthly"
    """End-of-month: compute the persona delta and write a PersonaDeltaSnapshot."""
    OnDemand = "OnDemand"
    """Caller-initiated pass — runs whichever tiers have work pending."""


class CoreMemoryKind(Enum):
    """Why a memory was promoted to the core tier."""

    UserAsserted = "UserAsserted"
    """A fact the user explicitly asked the AI to remember."""
    PatternInferred = "PatternInferred"
    """Inferred from interaction patterns — a long-standing preference / theme."""
    HighSalience = "HighSalience"
    """Promoted because of extreme salience."""
    HostProvided = "HostProvided"
    """Promoted by the host directly (profile sync, identity bootstrap)."""


# ─────────────────────────────────────────────────────────────────────────────
# Tier records
# ─────────────────────────────────────────────────────────────────────────────


@dataclass
class CoreMemory:
    """A core memory the AI will not forget. Compact by design.

    ``last_reinforced_utc`` and ``reinforcement_count`` are mutable (the store
    bumps them on reinforce); everything else is set once at construction.
    """

    statement: str = ""
    kind: CoreMemoryKind = CoreMemoryKind.UserAsserted
    topic: Optional[str] = None
    embedding: Optional[list[float]] = None
    source_memory_id: Optional[uuid.UUID] = None
    id: uuid.UUID = field(default_factory=uuid.uuid4)
    created_at_utc: datetime = field(default_factory=_utc_now)
    last_reinforced_utc: datetime = field(default_factory=_utc_now)
    reinforcement_count: int = 0


@dataclass(frozen=True)
class DailyMemorySummary:
    """Compressed record of a single calendar day's worth of episodic memory."""

    day: date
    summary: str = ""
    highlight_entries: list[EpisodicMemoryEntry] = field(default_factory=list)
    episode_count: int = 0
    topic_weights: dict[str, float] = field(default_factory=dict)
    topic_dispersion: float = 0.0
    salience: float = 0.0
    id: uuid.UUID = field(default_factory=uuid.uuid4)
    generated_at_utc: datetime = field(default_factory=_utc_now)


@dataclass(frozen=True)
class SemanticMemoryCluster:
    """Topic-coherent cluster of daily summaries — the "semantic memory" tier."""

    week_starting_monday: date
    topic: str = ""
    summary: str = ""
    centroid_embedding: Optional[list[float]] = None
    source_daily_ids: list[uuid.UUID] = field(default_factory=list)
    topic_weight: float = 0.0
    salience: float = 0.0
    id: uuid.UUID = field(default_factory=uuid.uuid4)
    generated_at_utc: datetime = field(default_factory=_utc_now)


@dataclass(frozen=True)
class PersonaDeltaSnapshot:
    """Diff between a PersonaState at the start and end of a consolidation period."""

    period_start: date
    period_end: date
    user_id: str = "default"
    verbosity_before: str = ""
    verbosity_after: str = ""
    formality_before: str = ""
    formality_after: str = ""
    new_topics: dict[str, float] = field(default_factory=dict)
    strengthened_topics: dict[str, float] = field(default_factory=dict)
    newly_disfavoured_topics: list[str] = field(default_factory=list)
    net_signal_delta: int = 0
    interactions_in_period: int = 0
    narrative: str = ""
    id: uuid.UUID = field(default_factory=uuid.uuid4)
    generated_at_utc: datetime = field(default_factory=_utc_now)


@dataclass(frozen=True)
class ConsolidationOutcome:
    """Outcome of a single consolidator tick."""

    kind: SleepKind
    daily_summaries_produced: int
    semantic_clusters_produced: int
    persona_deltas_produced: int
    core_promotions: int
    episodes_pruned: int
    dailies_pruned: int
    semantics_pruned: int
    ran_at_utc: datetime


@dataclass(frozen=True)
class MemoryConsolidationOptions:
    """Retention windows + core-promotion thresholds.

    Defaults follow the hierarchical-memory plan: 7-day episodic window, 30-day
    daily window, 12-month semantic window, salience >= 0.80 promotes daily to
    core, >= 0.75 promotes weekly clusters.
    """

    episodic_retention_days: int = 7
    daily_retention_days: int = 30
    semantic_retention_days: int = 365
    daily_core_promotion_threshold: float = 0.80
    weekly_core_promotion_threshold: float = 0.75


# ─────────────────────────────────────────────────────────────────────────────
# Day helpers — datetime.date arithmetic
# ─────────────────────────────────────────────────────────────────────────────


def day_key_of(dt: datetime) -> date:
    """UTC calendar day of a datetime.

    Mirrors C# ``DateOnly.FromDateTime(dt.UtcDateTime)``: an aware datetime is
    normalised to UTC before its date is taken; a naive datetime is treated as
    already-UTC (its own date).
    """
    if dt.tzinfo is not None:
        dt = dt.astimezone(timezone.utc)
    return dt.date()


def monday_of(d: date) -> date:
    """The Monday of the week containing ``d``.

    Python ``date.weekday()`` is Mon=0..Sun=6, so Monday-of is simply
    ``d - weekday()`` days — equivalent to the C# ``((int)dow + 6) % 7`` with
    Sunday=0.
    """
    return d - timedelta(days=d.weekday())


def month_first_day_of(d: date) -> date:
    """First day of the month containing ``d``."""
    return d.replace(day=1)


# ─────────────────────────────────────────────────────────────────────────────
# Cosine — FULL cosine (differs from the episodic store's dot-only cosine).
# ─────────────────────────────────────────────────────────────────────────────


def cosine_full(a: list[float], b: list[float]) -> float:
    """Full cosine similarity: dot / (||a||*||b||).

    Returns 0 on a length mismatch or a near-zero denominator. This does NOT
    assume the vectors are L2-normalised, so it differs from the episodic
    store's dot-product cosine — both are kept.
    """
    if len(a) != len(b):
        return 0.0
    dot = 0.0
    mag_a = 0.0
    mag_b = 0.0
    for i in range(len(a)):
        dot += a[i] * b[i]
        mag_a += a[i] * a[i]
        mag_b += b[i] * b[i]
    denom = math.sqrt(mag_a) * math.sqrt(mag_b)
    # sys.float_info.epsilon is the C# double.Epsilon analogue; a zero vector
    # yields denom == 0.0 which trips this guard regardless of the exact bound.
    return 0.0 if denom < 2.220446049250313e-16 else dot / denom


def _clamp(x: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, x))


def _has_embedding(e: EpisodicMemoryEntry) -> bool:
    return e.embedding is not None and len(e.embedding) > 0


# ─────────────────────────────────────────────────────────────────────────────
# Store Protocols
# ─────────────────────────────────────────────────────────────────────────────


@runtime_checkable
class IDailyMemoryStore(Protocol):
    """Persistent store for tier-2 daily summaries."""

    async def upsert_async(
        self, summary: DailyMemorySummary, *, ct: Optional[object] = None
    ) -> None:
        """Add a daily summary. Replaces any existing entry for the same day."""
        ...

    async def get_async(
        self, day: date, *, ct: Optional[object] = None
    ) -> Optional[DailyMemorySummary]:
        """Return the summary for the given day, or None when none exists."""
        ...

    async def get_range_async(
        self, from_inclusive: date, to_inclusive: date, *, ct: Optional[object] = None
    ) -> list[DailyMemorySummary]:
        """Return all summaries between from/to inclusive (day-ordered)."""
        ...

    async def prune_older_than_async(
        self, cutoff: date, *, ct: Optional[object] = None
    ) -> int:
        """Remove summaries older than cutoff. Return count removed."""
        ...

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        """Total summaries currently stored."""
        ...


@runtime_checkable
class ISemanticMemoryStore(Protocol):
    """Persistent store for tier-3 semantic memory clusters."""

    async def add_async(
        self, cluster: SemanticMemoryCluster, *, ct: Optional[object] = None
    ) -> None:
        """Add a cluster."""
        ...

    async def get_week_async(
        self, week_starting_monday: date, *, ct: Optional[object] = None
    ) -> list[SemanticMemoryCluster]:
        """Return all clusters for the given week, ordered by topic_weight desc."""
        ...

    async def search_async(
        self,
        query_embedding: Optional[list[float]],
        top_k: int = 5,
        *,
        ct: Optional[object] = None,
    ) -> list[SemanticMemoryCluster]:
        """Top-top_k clusters by centroid cosine; recency fallback when None."""
        ...

    async def prune_older_than_async(
        self, cutoff: date, *, ct: Optional[object] = None
    ) -> int:
        """Remove clusters whose week start is before cutoff."""
        ...

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        """Total clusters currently stored."""
        ...


@runtime_checkable
class IPersonaDeltaStore(Protocol):
    """Persistent store for tier-4 persona-delta snapshots. Retained forever."""

    async def add_async(
        self, snapshot: PersonaDeltaSnapshot, *, ct: Optional[object] = None
    ) -> None:
        """Add a delta snapshot."""
        ...

    async def get_for_user_async(
        self, user_id: str, *, ct: Optional[object] = None
    ) -> list[PersonaDeltaSnapshot]:
        """Return all snapshots for the given user, ordered by period_start."""
        ...

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        """Total snapshots currently stored."""
        ...


@runtime_checkable
class ICoreMemoryStore(Protocol):
    """Persistent store for tier-5 core memories — things the AI will not forget."""

    async def add_async(
        self, memory: CoreMemory, *, ct: Optional[object] = None
    ) -> None:
        """Add a core memory."""
        ...

    async def get_async(
        self, id: uuid.UUID, *, ct: Optional[object] = None
    ) -> Optional[CoreMemory]:
        """Return a core memory by id, or None when not found."""
        ...

    async def search_async(
        self,
        query_embedding: Optional[list[float]],
        top_k: int = 5,
        *,
        ct: Optional[object] = None,
    ) -> list[CoreMemory]:
        """Top-top_k core memories by embedding cosine; reinforcement fallback."""
        ...

    async def list_all_async(
        self, *, ct: Optional[object] = None
    ) -> list[CoreMemory]:
        """All core memories in reinforcement order (most reinforced first)."""
        ...

    async def reinforce_async(
        self, id: uuid.UUID, *, ct: Optional[object] = None
    ) -> None:
        """Increment reinforcement_count and bump last_reinforced_utc. No-op when unknown."""
        ...

    async def remove_async(
        self, id: uuid.UUID, *, ct: Optional[object] = None
    ) -> bool:
        """Remove a core memory."""
        ...

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        """Total core memories currently stored."""
        ...


# ─────────────────────────────────────────────────────────────────────────────
# In-memory store implementations
# ─────────────────────────────────────────────────────────────────────────────


class InMemoryDailyMemoryStore:
    """In-memory :class:`IDailyMemoryStore`."""

    def __init__(self) -> None:
        self._store: dict[date, DailyMemorySummary] = {}

    async def upsert_async(
        self, summary: DailyMemorySummary, *, ct: Optional[object] = None
    ) -> None:
        if summary is None:
            raise ValueError("summary required")
        self._store[summary.day] = summary

    async def get_async(
        self, day: date, *, ct: Optional[object] = None
    ) -> Optional[DailyMemorySummary]:
        return self._store.get(day)

    async def get_range_async(
        self, from_inclusive: date, to_inclusive: date, *, ct: Optional[object] = None
    ) -> list[DailyMemorySummary]:
        return sorted(
            (
                s
                for s in self._store.values()
                if from_inclusive <= s.day <= to_inclusive
            ),
            key=lambda s: s.day,
        )

    async def prune_older_than_async(
        self, cutoff: date, *, ct: Optional[object] = None
    ) -> int:
        to_remove = [d for d in self._store if d < cutoff]
        for d in to_remove:
            del self._store[d]
        return len(to_remove)

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        return len(self._store)


class InMemorySemanticMemoryStore:
    """In-memory :class:`ISemanticMemoryStore`."""

    def __init__(self) -> None:
        self._store: list[SemanticMemoryCluster] = []

    async def add_async(
        self, cluster: SemanticMemoryCluster, *, ct: Optional[object] = None
    ) -> None:
        if cluster is None:
            raise ValueError("cluster required")
        self._store.append(cluster)

    async def get_week_async(
        self, week_starting_monday: date, *, ct: Optional[object] = None
    ) -> list[SemanticMemoryCluster]:
        return sorted(
            (c for c in self._store if c.week_starting_monday == week_starting_monday),
            key=lambda c: c.topic_weight,
            reverse=True,
        )

    async def search_async(
        self,
        query_embedding: Optional[list[float]],
        top_k: int = 5,
        *,
        ct: Optional[object] = None,
    ) -> list[SemanticMemoryCluster]:
        if query_embedding is None:
            return sorted(
                self._store, key=lambda c: c.generated_at_utc, reverse=True
            )[:top_k]
        scored = [
            (c, cosine_full(query_embedding, c.centroid_embedding))
            for c in self._store
            if c.centroid_embedding is not None
        ]
        scored.sort(key=lambda x: x[1], reverse=True)
        return [c for c, _ in scored[:top_k]]

    async def prune_older_than_async(
        self, cutoff: date, *, ct: Optional[object] = None
    ) -> int:
        before = len(self._store)
        self._store = [c for c in self._store if not (c.week_starting_monday < cutoff)]
        return before - len(self._store)

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        return len(self._store)


class InMemoryPersonaDeltaStore:
    """In-memory :class:`IPersonaDeltaStore`."""

    def __init__(self) -> None:
        self._store: list[PersonaDeltaSnapshot] = []

    async def add_async(
        self, snapshot: PersonaDeltaSnapshot, *, ct: Optional[object] = None
    ) -> None:
        if snapshot is None:
            raise ValueError("snapshot required")
        self._store.append(snapshot)

    async def get_for_user_async(
        self, user_id: str, *, ct: Optional[object] = None
    ) -> list[PersonaDeltaSnapshot]:
        return sorted(
            (s for s in self._store if s.user_id == user_id),
            key=lambda s: s.period_start,
        )

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        return len(self._store)


class InMemoryCoreMemoryStore:
    """In-memory :class:`ICoreMemoryStore`."""

    def __init__(self) -> None:
        self._store: dict[uuid.UUID, CoreMemory] = {}

    async def add_async(
        self, memory: CoreMemory, *, ct: Optional[object] = None
    ) -> None:
        if memory is None:
            raise ValueError("memory required")
        self._store[memory.id] = memory

    async def get_async(
        self, id: uuid.UUID, *, ct: Optional[object] = None
    ) -> Optional[CoreMemory]:
        return self._store.get(id)

    async def search_async(
        self,
        query_embedding: Optional[list[float]],
        top_k: int = 5,
        *,
        ct: Optional[object] = None,
    ) -> list[CoreMemory]:
        if query_embedding is None:
            return sorted(self._store.values(), key=_reinforcement_key, reverse=True)[
                :top_k
            ]
        scored = [
            (m, cosine_full(query_embedding, m.embedding))
            for m in self._store.values()
            if m.embedding is not None
        ]
        scored.sort(key=lambda x: x[1], reverse=True)
        return [m for m, _ in scored[:top_k]]

    async def list_all_async(
        self, *, ct: Optional[object] = None
    ) -> list[CoreMemory]:
        return sorted(self._store.values(), key=_reinforcement_key, reverse=True)

    async def reinforce_async(
        self, id: uuid.UUID, *, ct: Optional[object] = None
    ) -> None:
        memory = self._store.get(id)
        if memory is not None:
            memory.reinforcement_count += 1
            memory.last_reinforced_utc = _utc_now()

    async def remove_async(
        self, id: uuid.UUID, *, ct: Optional[object] = None
    ) -> bool:
        if id in self._store:
            del self._store[id]
            return True
        return False

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        return len(self._store)


def _reinforcement_key(m: CoreMemory) -> tuple[int, datetime]:
    """Sort key: reinforcement_count desc, then last_reinforced_utc desc.

    Both fields are ascending in the tuple and the callers pass
    ``reverse=True``, which reproduces C#'s ``OrderByDescending(count)
    .ThenByDescending(lastReinforced)``.
    """
    return (m.reinforcement_count, m.last_reinforced_utc)


# ─────────────────────────────────────────────────────────────────────────────
# IMemorySummarizer + HeuristicSummarizer
# ─────────────────────────────────────────────────────────────────────────────


@runtime_checkable
class IMemorySummarizer(Protocol):
    """Produces the text + scores for each consolidation tier."""

    async def summarize_day_async(
        self,
        day: date,
        entries: list[EpisodicMemoryEntry],
        *,
        ct: Optional[object] = None,
    ) -> DailyMemorySummary:
        """Produce a DailyMemorySummary from the day's episodic entries."""
        ...

    async def consolidate_week_async(
        self,
        week_starting_monday: date,
        days_in_week: list[DailyMemorySummary],
        *,
        ct: Optional[object] = None,
    ) -> list[SemanticMemoryCluster]:
        """Produce zero or more SemanticMemoryCluster records from a week's dailies."""
        ...

    async def derive_persona_delta_async(
        self,
        before: PersonaState,
        after: PersonaState,
        days_in_period: list[DailyMemorySummary],
        *,
        ct: Optional[object] = None,
    ) -> PersonaDeltaSnapshot:
        """Compute the PersonaDeltaSnapshot across the period."""
        ...


class HeuristicSummarizer:
    """Heuristic :class:`IMemorySummarizer` that requires no LLM.

    Produces summaries entirely from structural signals — embedding clustering,
    topic-weight aggregation, length-and-recency salience. Formulas are
    identical to the C# HeuristicSummarizer.
    """

    def __init__(
        self,
        *,
        highlight_count: int = 5,
        min_days_per_topic_for_cluster: int = 2,
        clock: Optional[Callable[[], datetime]] = None,
    ) -> None:
        self.highlight_count = highlight_count
        self.min_days_per_topic_for_cluster = min_days_per_topic_for_cluster
        self._clock: Callable[[], datetime] = clock or _utc_now

    # ── summarize_day_async ───────────────────────────────────────────────

    async def summarize_day_async(
        self,
        day: date,
        entries: list[EpisodicMemoryEntry],
        *,
        ct: Optional[object] = None,
    ) -> DailyMemorySummary:
        if entries is None:
            raise ValueError("entries required")

        if len(entries) == 0:
            return DailyMemorySummary(
                day=day,
                summary=f"No exchanges recorded on {day}.",
                episode_count=0,
                generated_at_utc=self._clock(),
            )

        topic_weights = _aggregate_topic_weights(entries)
        dispersion = _mean_pairwise_cosine_distance(entries)
        highlights = _select_highlights(entries, self.highlight_count)
        salience = _compute_daily_salience(len(entries), topic_weights, dispersion)
        summary = _build_daily_summary_text(day, len(entries), topic_weights, highlights)

        return DailyMemorySummary(
            day=day,
            summary=summary,
            highlight_entries=highlights,
            episode_count=len(entries),
            topic_weights=topic_weights,
            topic_dispersion=dispersion,
            salience=salience,
            generated_at_utc=self._clock(),
        )

    # ── consolidate_week_async ────────────────────────────────────────────

    async def consolidate_week_async(
        self,
        week_starting_monday: date,
        days_in_week: list[DailyMemorySummary],
        *,
        ct: Optional[object] = None,
    ) -> list[SemanticMemoryCluster]:
        if days_in_week is None:
            raise ValueError("days_in_week required")
        if len(days_in_week) == 0:
            return []

        # Tally how many days each topic appeared in and its cumulative weight.
        # Topic labels arrive already lowercased/trimmed from _aggregate_topic_weights,
        # so plain dict keys reproduce StringComparer.OrdinalIgnoreCase here.
        topic_to_days: dict[str, list[DailyMemorySummary]] = {}
        topic_to_weight: dict[str, float] = {}

        for d in days_in_week:
            for topic, w in d.topic_weights.items():
                topic_to_days.setdefault(topic, []).append(d)
                topic_to_weight[topic] = topic_to_weight.get(topic, 0.0) + w

        total_weight = sum(topic_to_weight.values())
        if total_weight <= 0:
            total_weight = 1.0

        clusters: list[SemanticMemoryCluster] = []
        topics_by_weight_desc = sorted(
            topic_to_weight.keys(), key=lambda t: topic_to_weight[t], reverse=True
        )
        for topic in topics_by_weight_desc:
            contributing_days = topic_to_days[topic]
            if len(contributing_days) < self.min_days_per_topic_for_cluster:
                continue

            centroid = _centroid_of_highlights(contributing_days)
            weight = topic_to_weight[topic]
            cluster_salience = min(
                1.0, (weight / total_weight) + (len(contributing_days) / 7.0) * 0.25
            )

            clusters.append(
                SemanticMemoryCluster(
                    week_starting_monday=week_starting_monday,
                    topic=topic,
                    summary=_build_weekly_cluster_text(topic, contributing_days),
                    centroid_embedding=centroid,
                    source_daily_ids=[d.id for d in contributing_days],
                    topic_weight=weight,
                    salience=cluster_salience,
                    generated_at_utc=self._clock(),
                )
            )
        return clusters

    # ── derive_persona_delta_async ────────────────────────────────────────

    async def derive_persona_delta_async(
        self,
        before: PersonaState,
        after: PersonaState,
        days_in_period: list[DailyMemorySummary],
        *,
        ct: Optional[object] = None,
    ) -> PersonaDeltaSnapshot:
        if before is None:
            raise ValueError("before required")
        if after is None:
            raise ValueError("after required")
        if days_in_period is None:
            raise ValueError("days_in_period required")

        new_topics: dict[str, float] = {}
        strengthened: dict[str, float] = {}
        for topic, after_w in after.topic_weights.items():
            before_w = before.topic_weights.get(topic, 0.0)
            delta = after_w - before_w
            if before_w <= 0 and after_w > 0:
                new_topics[topic] = after_w
            elif delta > 0:
                strengthened[topic] = delta

        disfavoured_new = [
            t for t in after.disfavoured_topics if t not in before.disfavoured_topics
        ]

        net_signals = (after.positive_signals - before.positive_signals) - (
            after.negative_signals - before.negative_signals
        )
        interactions = after.total_interactions - before.total_interactions

        if len(days_in_period) > 0:
            period_start = min(d.day for d in days_in_period)
            period_end = max(d.day for d in days_in_period)
        else:
            period_start = day_key_of(after.last_updated_utc)
            period_end = day_key_of(after.last_updated_utc)

        narrative = _build_persona_narrative(
            before,
            after,
            new_topics,
            strengthened,
            disfavoured_new,
            net_signals,
            interactions,
            period_start,
            period_end,
        )

        return PersonaDeltaSnapshot(
            user_id=after.user_id,
            period_start=period_start,
            period_end=period_end,
            verbosity_before=before.verbosity,
            verbosity_after=after.verbosity,
            formality_before=before.formality,
            formality_after=after.formality,
            new_topics=new_topics,
            strengthened_topics=strengthened,
            newly_disfavoured_topics=disfavoured_new,
            net_signal_delta=net_signals,
            interactions_in_period=interactions,
            narrative=narrative,
            generated_at_utc=self._clock(),
        )


# ── Summarizer helpers — topic + dispersion ─────────────────────────────────


def _aggregate_topic_weights(entries: list[EpisodicMemoryEntry]) -> dict[str, float]:
    """Topic weights from "topic" (+1) and pipe-split "topics" (each +1), lowercased."""
    weights: dict[str, float] = {}
    for e in entries:
        if e.tags is None:
            continue
        t = e.tags.get("topic")
        if t is not None and len(t.strip()) > 0:
            _accumulate_topic(weights, t, 1.0)
        multi = e.tags.get("topics")
        if multi is not None and len(multi.strip()) > 0:
            for p in multi.split("|"):
                if len(p) == 0:  # RemoveEmptyEntries
                    continue
                _accumulate_topic(weights, p, 1.0)
    return weights


def _accumulate_topic(dct: dict[str, float], topic: str, weight: float) -> None:
    key = topic.strip().lower()
    if len(key) == 0:
        return
    dct[key] = dct.get(key, 0.0) + weight


def _mean_pairwise_cosine_distance(entries: list[EpisodicMemoryEntry]) -> float:
    """Mean over all pairs of (1 - clamp(fullCosine,-1,1)); 0 when <2 embedded entries."""
    with_embeddings = [e for e in entries if _has_embedding(e)]
    if len(with_embeddings) < 2:
        return 0.0

    total = 0.0
    pairs = 0
    for i in range(len(with_embeddings)):
        for j in range(i + 1, len(with_embeddings)):
            sim = cosine_full(
                with_embeddings[i].embedding, with_embeddings[j].embedding
            )
            total += 1.0 - _clamp(sim, -1.0, 1.0)
            pairs += 1
    return 0.0 if pairs == 0 else _clamp(total / pairs, 0.0, 1.0)


def _select_highlights(
    entries: list[EpisodicMemoryEntry], count: int
) -> list[EpisodicMemoryEntry]:
    """Top-``count`` entries by salience proxy (or all when <=count), re-sorted by time."""
    if len(entries) <= count:
        return sorted(entries, key=lambda e: e.recorded_at_utc)

    scored = [(e, _entry_salience_proxy(e, entries)) for e in entries]
    # OrderByDescending(score).ThenByDescending(recordedAt): sort ascending on the
    # negated key tuple's fields via reverse=True on (score, recordedAt).
    scored.sort(key=lambda x: (x[1], x[0].recorded_at_utc), reverse=True)
    top = [e for e, _ in scored[:count]]
    return sorted(top, key=lambda e: e.recorded_at_utc)


def _entry_salience_proxy(
    entry: EpisodicMemoryEntry, all_entries: list[EpisodicMemoryEntry]
) -> float:
    length_score = min(
        1.0, (len(entry.user_text) + len(entry.assistant_text)) / 800.0
    )
    uniqueness_score = 0.5
    if _has_embedding(entry):
        others = [
            e for e in all_entries if e.id != entry.id and _has_embedding(e)
        ]
        if len(others) > 0:
            total = 0.0
            for e in others:
                total += cosine_full(entry.embedding, e.embedding)
            mean_sim = total / len(others)
            uniqueness_score = 1.0 - _clamp(mean_sim, -1.0, 1.0)
    return (length_score * 0.6) + (uniqueness_score * 0.4)


def _compute_daily_salience(
    episode_count: int, topic_weights: dict[str, float], dispersion: float
) -> float:
    """Daily salience = volume*0.4 + dispersion*0.3 + topicConcentration*0.3."""
    volume_score = min(1.0, episode_count / 30.0)
    if len(topic_weights) == 0:
        topic_concentration = 0.5
    else:
        max_w = max(topic_weights.values())
        sum_w = sum(topic_weights.values())
        topic_concentration = min(1.0, max_w / max(1.0, sum_w))
    return (volume_score * 0.4) + (dispersion * 0.3) + (topic_concentration * 0.3)


def _centroid_of_highlights(
    days: list[DailyMemorySummary],
) -> Optional[list[float]]:
    """Mean of all highlight embeddings across contributing days; None when none."""
    all_embeddings: list[list[float]] = []
    for d in days:
        for e in d.highlight_entries:
            if _has_embedding(e):
                all_embeddings.append(e.embedding)  # type: ignore[arg-type]
    if len(all_embeddings) == 0:
        return None
    dim = len(all_embeddings[0])
    centroid = [0.0] * dim
    for e in all_embeddings:
        for i in range(min(dim, len(e))):
            centroid[i] += e[i]
    for i in range(dim):
        centroid[i] /= len(all_embeddings)
    return centroid


# ── Summarizer helpers — text builders ──────────────────────────────────────


def _build_daily_summary_text(
    day: date,
    count: int,
    topics: dict[str, float],
    highlights: list[EpisodicMemoryEntry],
) -> str:
    top_topics = [
        kv[0]
        for kv in sorted(topics.items(), key=lambda kv: kv[1], reverse=True)[:3]
    ]

    topics_clause = (
        f" Top topics: {', '.join(top_topics)}." if len(top_topics) > 0 else ""
    )

    highlight_clause = (
        f' Standout moment: "{_truncate(highlights[0].user_text, 120)}".'
        if len(highlights) > 0
        else ""
    )

    return (
        f"On {day} you had {count} "
        + ("exchange." if count == 1 else "exchanges.")
        + topics_clause
        + highlight_clause
    )


def _build_weekly_cluster_text(
    topic: str, contributing_days: list[DailyMemorySummary]
) -> str:
    total_episodes = sum(d.episode_count for d in contributing_days)
    return (
        f"Across {len(contributing_days)} days this week you returned to "
        f'"{topic}" — {total_episodes} exchanges in total.'
    )


def _build_persona_narrative(
    before: PersonaState,
    after: PersonaState,
    new_topics: dict[str, float],
    strengthened: dict[str, float],
    disfavoured: list[str],
    net_signals: int,
    interactions: int,
    period_start: date,
    period_end: date,
) -> str:
    parts: list[str] = []
    parts.append(
        f"Between {period_start} and {period_end}, {interactions} interactions were recorded."
    )
    if len(new_topics) > 0:
        parts.append("New interests appeared: " + ", ".join(_top_n_keys(new_topics, 3)) + ".")
    if len(strengthened) > 0:
        parts.append(
            "Existing interests deepened around "
            + ", ".join(_top_n_keys(strengthened, 3))
            + "."
        )
    if len(disfavoured) > 0:
        parts.append("Topics now avoided: " + ", ".join(disfavoured) + ".")
    if before.verbosity != after.verbosity:
        parts.append(
            f"Preferred verbosity shifted from {before.verbosity} to {after.verbosity}."
        )
    if before.formality != after.formality:
        parts.append(
            f"Preferred tone shifted from {before.formality} to {after.formality}."
        )
    if net_signals != 0:
        parts.append(
            f"Net feedback was positive (+{net_signals})."
            if net_signals > 0
            else f"Net feedback was negative ({net_signals})."
        )
    return " ".join(parts)


def _top_n_keys(m: dict[str, float], n: int) -> list[str]:
    """Keys of ``m`` ordered by value desc, top-n."""
    return [kv[0] for kv in sorted(m.items(), key=lambda kv: kv[1], reverse=True)[:n]]


def _truncate(s: str, max_len: int) -> str:
    if s is None or len(s) == 0:
        return ""
    if len(s) <= max_len:
        return s
    return s[:max_len].rstrip() + "…"


# ─────────────────────────────────────────────────────────────────────────────
# IMemoryConsolidator + MemoryConsolidator
# ─────────────────────────────────────────────────────────────────────────────


@runtime_checkable
class IMemoryConsolidator(Protocol):
    """Promotes lower-tier memory into higher tiers and enforces retention."""

    async def tick_async(
        self, kind: SleepKind, *, ct: Optional[object] = None
    ) -> ConsolidationOutcome:
        """Run the consolidation pass for the given kind.

        OnDemand runs every tier with work pending. Returns the breakdown of
        what was produced and pruned.
        """
        ...


class MemoryConsolidator:
    """Default :class:`IMemoryConsolidator` implementation."""

    def __init__(
        self,
        episodic: IEpisodicMemoryStore,
        daily: IDailyMemoryStore,
        semantic: ISemanticMemoryStore,
        persona_delta: IPersonaDeltaStore,
        core: ICoreMemoryStore,
        persona_store: IPersonaStore,
        summarizer: IMemorySummarizer,
        options: Optional[MemoryConsolidationOptions] = None,
        clock: Optional[Callable[[], datetime]] = None,
        user_id: str = "default",
    ) -> None:
        if episodic is None:
            raise ValueError("episodic required")
        if daily is None:
            raise ValueError("daily required")
        if semantic is None:
            raise ValueError("semantic required")
        if persona_delta is None:
            raise ValueError("persona_delta required")
        if core is None:
            raise ValueError("core required")
        if persona_store is None:
            raise ValueError("persona_store required")
        if summarizer is None:
            raise ValueError("summarizer required")
        self._episodic = episodic
        self._daily = daily
        self._semantic = semantic
        self._persona_delta = persona_delta
        self._core = core
        self._persona_store = persona_store
        self._summarizer = summarizer
        self._options = options or MemoryConsolidationOptions()
        self._clock: Callable[[], datetime] = clock or _utc_now
        self._user_id = user_id

    async def tick_async(
        self, kind: SleepKind, *, ct: Optional[object] = None
    ) -> ConsolidationOutcome:
        now = self._clock()
        dailies = 0
        clusters = 0
        deltas = 0
        core_promoted = 0
        episodes_pruned = 0
        dailies_pruned = 0
        semantics_pruned = 0

        if kind is SleepKind.Daily or kind is SleepKind.OnDemand:
            produced, promoted_from_daily = await self._run_daily(now)
            dailies = produced
            core_promoted += promoted_from_daily
            episodes_pruned += await self._prune_episodic(now)

        if kind is SleepKind.Weekly or kind is SleepKind.OnDemand:
            produced, promoted_from_weekly = await self._run_weekly(now)
            clusters = produced
            core_promoted += promoted_from_weekly
            dailies_pruned += await self._prune_dailies(now)

        if kind is SleepKind.Monthly or kind is SleepKind.OnDemand:
            deltas = await self._run_monthly(now)
            semantics_pruned += await self._prune_semantics(now)

        return ConsolidationOutcome(
            kind=kind,
            daily_summaries_produced=dailies,
            semantic_clusters_produced=clusters,
            persona_deltas_produced=deltas,
            core_promotions=core_promoted,
            episodes_pruned=episodes_pruned,
            dailies_pruned=dailies_pruned,
            semantics_pruned=semantics_pruned,
            ran_at_utc=now,
        )

    # ── Daily pass ─────────────────────────────────────────────────────────

    async def _run_daily(self, now: datetime) -> tuple[int, int]:
        recent = await self._episodic.get_recent_async(2**63 - 1)
        if len(recent) == 0:
            return (0, 0)

        # Group episodes by their calendar day (UTC).
        today = day_key_of(now)
        by_day: dict[date, list[EpisodicMemoryEntry]] = {}
        for e in recent:
            key = day_key_of(e.recorded_at_utc)
            by_day.setdefault(key, []).append(e)

        produced = 0
        promoted = 0
        for day, group in by_day.items():
            if not (day < today):  # only fully completed days
                continue

            existing = await self._daily.get_async(day)
            if existing is not None and existing.episode_count == len(group):
                continue  # idempotent skip — already consolidated this day

            ordered = sorted(group, key=lambda e: e.recorded_at_utc)
            summary = await self._summarizer.summarize_day_async(day, ordered)
            await self._daily.upsert_async(summary)
            produced += 1

            if summary.salience >= self._options.daily_core_promotion_threshold:
                promoted += await self._promote_daily_to_core(summary)
        return (produced, promoted)

    # ── Weekly pass ────────────────────────────────────────────────────────

    async def _run_weekly(self, now: datetime) -> tuple[int, int]:
        today = day_key_of(now)
        this_monday = monday_of(today)
        last_monday = this_monday - timedelta(days=7)
        last_sunday = last_monday + timedelta(days=6)

        last_week = await self._daily.get_range_async(last_monday, last_sunday)
        if len(last_week) == 0:
            return (0, 0)

        # Idempotency: if we already have clusters for this week, skip.
        existing = await self._semantic.get_week_async(last_monday)
        if len(existing) > 0:
            return (0, 0)

        clusters = await self._summarizer.consolidate_week_async(last_monday, last_week)
        promoted = 0
        for c in clusters:
            await self._semantic.add_async(c)
            if c.salience >= self._options.weekly_core_promotion_threshold:
                promoted += await self._promote_cluster_to_core(c)
        return (len(clusters), promoted)

    # ── Monthly pass ───────────────────────────────────────────────────────

    async def _run_monthly(self, now: datetime) -> int:
        today = day_key_of(now)
        # Consider the most recently completed full month.
        first_of_this_month = month_first_day_of(today)
        last_month_end = first_of_this_month - timedelta(days=1)
        last_month_start = month_first_day_of(last_month_end)

        # Idempotency: skip if we already have a delta whose period_start falls in
        # the previous month (compared by month-year, not exact dates).
        existing_deltas = await self._persona_delta.get_for_user_async(self._user_id)
        if any(
            d.period_start.year == last_month_start.year
            and d.period_start.month == last_month_start.month
            for d in existing_deltas
        ):
            return 0

        days = await self._daily.get_range_async(last_month_start, last_month_end)
        if len(days) == 0:
            return 0

        loaded = await self._persona_store.load_async(self._user_id)
        after = loaded if loaded is not None else _new_persona(self._user_id)

        # For "before", reconstruct from the most recent prior delta if one exists;
        # otherwise treat as a fresh persona.
        priors = sorted(
            (d for d in existing_deltas if d.period_end < last_month_start),
            key=lambda d: d.period_end,
            reverse=True,
        )
        prior = priors[0] if len(priors) > 0 else None
        before = (
            _new_persona(self._user_id)
            if prior is None
            else _reconstruct_persona_before(after, days, prior)
        )

        delta = await self._summarizer.derive_persona_delta_async(before, after, days)
        await self._persona_delta.add_async(delta)
        return 1

    # ── Core promotions ────────────────────────────────────────────────────

    async def _promote_daily_to_core(self, summary: DailyMemorySummary) -> int:
        # FirstOrDefault on TopicWeights.OrderByDescending — None topic when empty.
        top_topic: Optional[str] = None
        top_weight = -math.inf
        for k, v in summary.topic_weights.items():
            if v > top_weight:
                top_weight = v
                top_topic = k

        statement = (
            f"On {summary.day} an unusually meaningful day was recorded."
            if top_topic is None
            else f'"{top_topic}" mattered enough on {summary.day} to be remembered.'
        )

        embedding: Optional[list[float]] = None
        for h in summary.highlight_entries:
            if h.embedding is not None and len(h.embedding) > 0:
                embedding = h.embedding
                break

        memory = CoreMemory(
            statement=statement,
            kind=CoreMemoryKind.HighSalience,
            topic=top_topic,
            embedding=embedding,
            source_memory_id=summary.id,
            created_at_utc=self._clock(),
            last_reinforced_utc=self._clock(),
        )
        await self._core.add_async(memory)
        return 1

    async def _promote_cluster_to_core(self, cluster: SemanticMemoryCluster) -> int:
        memory = CoreMemory(
            statement=(
                f'"{cluster.topic}" has been a recurring theme '
                f"(week of {cluster.week_starting_monday})."
            ),
            kind=CoreMemoryKind.PatternInferred,
            topic=cluster.topic,
            embedding=cluster.centroid_embedding,
            source_memory_id=cluster.id,
            created_at_utc=self._clock(),
            last_reinforced_utc=self._clock(),
        )
        await self._core.add_async(memory)
        return 1

    # ── Retention ──────────────────────────────────────────────────────────

    async def _prune_episodic(self, now: datetime) -> int:
        cutoff = now - timedelta(days=self._options.episodic_retention_days)
        return await self._episodic.prune_older_than_async(cutoff)

    async def _prune_dailies(self, now: datetime) -> int:
        cutoff = day_key_of(now) - timedelta(days=self._options.daily_retention_days)
        return await self._daily.prune_older_than_async(cutoff)

    async def _prune_semantics(self, now: datetime) -> int:
        cutoff = day_key_of(now) - timedelta(
            days=self._options.semantic_retention_days
        )
        return await self._semantic.prune_older_than_async(cutoff)


def _reconstruct_persona_before(
    after: PersonaState,
    days_in_period: list[DailyMemorySummary],
    prior: PersonaDeltaSnapshot,
) -> PersonaState:
    """Approximate the persona at the start of the period.

    Subtracts the in-period gains from the current persona. Conservative — when
    in doubt it shows no change. Faithful port of ReconstructPersonaBeforeAsync.
    """
    before = PersonaState()
    before.user_id = after.user_id
    before.verbosity = prior.verbosity_after
    before.formality = prior.formality_after
    before.preferred_locale = after.preferred_locale
    episode_sum = sum(d.episode_count for d in days_in_period)
    before.total_interactions = after.total_interactions - episode_sum
    before.positive_signals = max(
        0, after.positive_signals - _clamp_positive(prior.net_signal_delta)
    )
    before.negative_signals = after.negative_signals

    # Carry over topic weights minus the strongest in-period gains.
    before.topic_weights = {}
    for topic, w in after.topic_weights.items():
        delta = prior.strengthened_topics.get(topic)
        before.topic_weights[topic] = max(0.0, w - delta) if delta is not None else w
    before.disfavoured_topics = set(after.disfavoured_topics)
    return before


def _new_persona(user_id: str) -> PersonaState:
    p = PersonaState()
    p.user_id = user_id
    return p


def _clamp_positive(v: int) -> int:
    return 0 if v < 0 else v


# ─────────────────────────────────────────────────────────────────────────────
# InMemoryPersonaStore — satisfies the existing IPersonaStore Protocol
# ─────────────────────────────────────────────────────────────────────────────


class InMemoryPersonaStore:
    """In-memory :class:`IPersonaStore`.

    Keyed by user_id; :meth:`load_async` returns a fresh default
    :class:`PersonaState` (stamped with the requested user_id) when no persona
    has been persisted for that user.
    """

    def __init__(self) -> None:
        self._store: dict[str, PersonaState] = {}

    async def load_async(
        self, user_id: str, *, ct: Optional[object] = None
    ) -> PersonaState:
        existing = self._store.get(user_id)
        if existing is not None:
            return existing
        fresh = PersonaState()
        fresh.user_id = user_id
        return fresh

    async def save_async(
        self, persona: PersonaState, *, ct: Optional[object] = None
    ) -> None:
        if persona is None:
            raise ValueError("persona required")
        self._store[persona.user_id] = persona
