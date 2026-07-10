"""test_syncable_entry_store.py

Verifies InMemorySyncableEntryStore convergence rules: higher-version wins,
tombstone-of-non-tombstone on tie, content-hash tiebreaker, get_since ordering,
and the per-type state vector.

Mirrors CircleAI.Memory.Sync.InMemorySyncableEntryStore (C# — the spec).
"""
from __future__ import annotations

from datetime import datetime, timezone

from circle_ai.memory.sync import InMemorySyncableEntryStore, SyncableEntry


def _entry(
    entity_type: str,
    entity_id: str,
    version: int,
    *,
    is_tombstone: bool = False,
    content_hash: str = "aa",
    payload: str = "p",
) -> SyncableEntry:
    return SyncableEntry(
        entity_type=entity_type,
        entity_id=entity_id,
        version=version,
        is_tombstone=is_tombstone,
        content_hash=content_hash,
        payload=payload,
        source_node_id="node",
        authored_at=datetime(2026, 1, 1, tzinfo=timezone.utc),
    )


# ── first write + get ─────────────────────────────────────────────────────────


async def test_first_apply_stores_and_returns_true() -> None:
    store = InMemorySyncableEntryStore()
    assert await store.apply_async(_entry("T", "a", 10)) is True
    got = await store.get_async("T", "a")
    assert got is not None and got.version == 10


async def test_get_returns_none_for_unknown_key() -> None:
    store = InMemorySyncableEntryStore()
    assert await store.get_async("T", "missing") is None


# ── version wins ──────────────────────────────────────────────────────────────


async def test_higher_version_wins() -> None:
    store = InMemorySyncableEntryStore()
    await store.apply_async(_entry("T", "a", 10))
    assert await store.apply_async(_entry("T", "a", 20)) is True
    got = await store.get_async("T", "a")
    assert got.version == 20


async def test_lower_version_is_rejected() -> None:
    store = InMemorySyncableEntryStore()
    await store.apply_async(_entry("T", "a", 20))
    assert await store.apply_async(_entry("T", "a", 10)) is False
    got = await store.get_async("T", "a")
    assert got.version == 20


# ── tie rules ─────────────────────────────────────────────────────────────────


async def test_tombstone_beats_non_tombstone_on_equal_version() -> None:
    store = InMemorySyncableEntryStore()
    await store.apply_async(_entry("T", "a", 10, is_tombstone=False, content_hash="ff"))
    # Even a lower content hash tombstone wins over a non-tombstone at same version.
    assert (
        await store.apply_async(
            _entry("T", "a", 10, is_tombstone=True, content_hash="00", payload="")
        )
        is True
    )
    got = await store.get_async("T", "a")
    assert got.is_tombstone is True


async def test_non_tombstone_does_not_replace_tombstone_on_equal_version() -> None:
    store = InMemorySyncableEntryStore()
    await store.apply_async(_entry("T", "a", 10, is_tombstone=True, content_hash="00"))
    assert (
        await store.apply_async(
            _entry("T", "a", 10, is_tombstone=False, content_hash="ff")
        )
        is False
    )
    got = await store.get_async("T", "a")
    assert got.is_tombstone is True


async def test_content_hash_tiebreaker_on_equal_version_and_tombstone_state() -> None:
    store = InMemorySyncableEntryStore()
    await store.apply_async(_entry("T", "a", 10, content_hash="aa"))
    assert await store.apply_async(_entry("T", "a", 10, content_hash="bb")) is True
    got = await store.get_async("T", "a")
    assert got.content_hash == "bb"
    # A lower hash at the same version loses.
    assert await store.apply_async(_entry("T", "a", 10, content_hash="a0")) is False
    assert (await store.get_async("T", "a")).content_hash == "bb"


# ── get_since ─────────────────────────────────────────────────────────────────


async def test_get_since_returns_strictly_newer_ascending() -> None:
    store = InMemorySyncableEntryStore()
    for v in (30, 10, 20):
        await store.apply_async(_entry("T", f"id{v}", v))
    result = await store.get_since_async("T", 10)
    assert [e.version for e in result] == [20, 30]  # 10 excluded, ascending


async def test_get_since_filters_by_entity_type() -> None:
    store = InMemorySyncableEntryStore()
    await store.apply_async(_entry("A", "x", 10))
    await store.apply_async(_entry("B", "y", 20))
    result = await store.get_since_async("A", 0)
    assert [e.entity_type for e in result] == ["A"]


# ── state vector ──────────────────────────────────────────────────────────────


async def test_state_vector_reports_max_version_per_type_sorted() -> None:
    store = InMemorySyncableEntryStore()
    await store.apply_async(_entry("Zebra", "1", 5))
    await store.apply_async(_entry("Zebra", "2", 12))
    await store.apply_async(_entry("Apple", "1", 7))
    vector = await store.get_state_vector_async()
    # Sorted ascending by entity_type (ordinal).
    assert [(e.entity_type, e.max_known_version) for e in vector] == [
        ("Apple", 7),
        ("Zebra", 12),
    ]


async def test_state_vector_omits_types_with_no_applied_entries() -> None:
    store = InMemorySyncableEntryStore()
    # A rejected lower-version write must not create/raise a type high-watermark.
    await store.apply_async(_entry("T", "a", 20))
    await store.apply_async(_entry("T", "a", 10))  # rejected
    vector = await store.get_state_vector_async()
    assert [(e.entity_type, e.max_known_version) for e in vector] == [("T", 20)]
