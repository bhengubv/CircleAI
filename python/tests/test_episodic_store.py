"""test_episodic_store.py

Verifies InMemoryEpisodicStore: cosine similarity search, recency fallback,
FIFO capacity eviction, prune, and count. Mirrors the TypeScript pilot
(episodic_store.test.ts) and Go port (episodic_store_test.go).
"""
from __future__ import annotations

import uuid
from datetime import datetime, timezone
from typing import Optional

import pytest

from circle_ai.memory.episodic_memory import EpisodicMemoryEntry
from circle_ai.memory.in_memory_episodic_store import InMemoryEpisodicStore


def _entry(
    id: str,
    *,
    user_text: str = "u",
    assistant_text: str = "a",
    recorded_at: Optional[datetime] = None,
    embedding: Optional[list[float]] = None,
) -> EpisodicMemoryEntry:
    return EpisodicMemoryEntry(
        id=uuid.uuid5(uuid.NAMESPACE_OID, id),
        recorded_at_utc=recorded_at or datetime(2026, 1, 1, tzinfo=timezone.utc),
        user_text=user_text,
        assistant_text=assistant_text,
        embedding=embedding,
    )


def _id(name: str) -> uuid.UUID:
    return uuid.uuid5(uuid.NAMESPACE_OID, name)


# ── cosine search ─────────────────────────────────────────────────────────────


async def test_ranks_the_nearest_embedding_first() -> None:
    store = InMemoryEpisodicStore()
    await store.add_async(_entry("x", user_text="x-axis", embedding=[1.0, 0.0]))
    await store.add_async(_entry("y", user_text="y-axis", embedding=[0.0, 1.0]))

    hits = await store.search_async([1.0, 0.0], 2)
    assert len(hits) == 2
    assert hits[0].id == _id("x")
    assert hits[1].id == _id("y")


async def test_respects_top_k() -> None:
    store = InMemoryEpisodicStore()
    await store.add_async(_entry("a", embedding=[1.0, 0.0]))
    await store.add_async(_entry("b", embedding=[0.9, 0.1]))
    await store.add_async(_entry("c", embedding=[0.0, 1.0]))

    hits = await store.search_async([1.0, 0.0], 1)
    assert len(hits) == 1
    assert hits[0].id == _id("a")


async def test_ignores_entries_whose_embedding_dimension_differs() -> None:
    store = InMemoryEpisodicStore()
    await store.add_async(_entry("ok", embedding=[1.0, 0.0]))
    await store.add_async(_entry("wrongdim", embedding=[1.0, 0.0, 0.0]))

    hits = await store.search_async([1.0, 0.0], 5)
    assert len(hits) == 1
    assert hits[0].id == _id("ok")


# ── recency fallback ──────────────────────────────────────────────────────────


async def test_returns_newest_first_when_query_embedding_is_none() -> None:
    store = InMemoryEpisodicStore()
    await store.add_async(
        _entry("old", recorded_at=datetime(2026, 1, 1, tzinfo=timezone.utc))
    )
    await store.add_async(
        _entry("new", recorded_at=datetime(2026, 6, 1, tzinfo=timezone.utc))
    )

    hits = await store.search_async(None, 5)
    assert hits[0].id == _id("new")
    assert hits[1].id == _id("old")


async def test_treats_empty_embedding_as_no_embedding() -> None:
    store = InMemoryEpisodicStore()
    await store.add_async(
        _entry("old", recorded_at=datetime(2026, 1, 1, tzinfo=timezone.utc))
    )
    await store.add_async(
        _entry("new", recorded_at=datetime(2026, 6, 1, tzinfo=timezone.utc))
    )

    hits = await store.search_async([], 1)
    assert hits[0].id == _id("new")


# ── capacity + maintenance ────────────────────────────────────────────────────


async def test_evicts_oldest_entries_beyond_max_entries_fifo() -> None:
    store = InMemoryEpisodicStore(2)
    await store.add_async(_entry("a"))
    await store.add_async(_entry("b"))
    await store.add_async(_entry("c"))

    assert await store.count_async() == 2
    recent = await store.get_recent_async(10)
    ids = sorted(str(e.id) for e in recent)
    assert ids == sorted([str(_id("b")), str(_id("c"))])  # 'a' evicted


async def test_prunes_entries_older_than_cutoff_and_returns_removed_count() -> None:
    store = InMemoryEpisodicStore()
    await store.add_async(
        _entry("old", recorded_at=datetime(2026, 1, 1, tzinfo=timezone.utc))
    )
    await store.add_async(
        _entry("new", recorded_at=datetime(2026, 6, 1, tzinfo=timezone.utc))
    )

    removed = await store.prune_older_than_async(
        datetime(2026, 3, 1, tzinfo=timezone.utc)
    )
    assert removed == 1
    assert await store.count_async() == 1
    remaining = await store.get_recent_async(10)
    assert remaining[0].id == _id("new")


def test_rejects_a_non_positive_max_entries() -> None:
    with pytest.raises(ValueError):
        InMemoryEpisodicStore(0)
