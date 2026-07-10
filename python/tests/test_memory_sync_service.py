"""test_memory_sync_service.py

Verifies MemorySyncService push/receive orchestration over an in-memory
ISyncChannel double, plus the SyncReconciliation version-vector helpers.

Mirrors CircleAI.Sync.MemorySyncService and CircleAI.Sync.SyncPrimitives
(C# — the spec).
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timezone

import pytest

from circle_ai.memory.in_memory_episodic_store import InMemoryEpisodicStore
from circle_ai.sync import (
    ISyncChannel,
    MemorySyncService,
    SyncDelta,
    SyncDeliveryMode,
    SyncDomainKeys,
    SyncReconciliation,
    VersionVector,
)


class _FakeChannel(ISyncChannel):
    """In-memory ISyncChannel — records pushes, streams a scripted inbound
    sequence, and tracks last sequence per (owner, domain).
    """

    def __init__(self, inbound: list[SyncDelta] | None = None) -> None:
        self.pushed: list[SyncDelta] = []
        self._inbound = list(inbound or [])
        self._closed = asyncio.Event()

    async def push_delta_async(self, delta, *, ct=None) -> None:
        self.pushed.append(delta)

    async def receive_deltas_async(self, owner_id, *, ct=None):
        for d in self._inbound:
            if d.owner_id == owner_id:
                yield d
        # Then block until the service stops (mimics a live stream).
        await self._closed.wait()

    async def get_last_sequence_async(self, owner_id, domain_key, *, ct=None) -> int:
        seqs = [
            d.sequence
            for d in self.pushed
            if d.owner_id == owner_id and d.domain_key == domain_key
        ]
        return max(seqs) if seqs else 0

    def close(self) -> None:
        self._closed.set()


def _delta(owner: str, source: str, domain: str, payload: bytes, seq: int) -> SyncDelta:
    return SyncDelta(
        owner_id=owner,
        source_device_id=source,
        target_device_id="",
        domain_key=domain,
        payload=payload,
        sequence=seq,
        delivery_mode=SyncDeliveryMode.GUARANTEED,
        ttl=None,
        created_at=datetime.now(timezone.utc),
    )


# ── push ──────────────────────────────────────────────────────────────────────


async def test_push_builds_broadcast_delta_with_local_source() -> None:
    channel = _FakeChannel()
    svc = MemorySyncService(channel, InMemoryEpisodicStore(), "device-1")
    await svc.push_memory_delta_async("owner-1", SyncDomainKeys.EPISODIC_MEMORY, b"blob")

    assert len(channel.pushed) == 1
    d = channel.pushed[0]
    assert d.owner_id == "owner-1"
    assert d.source_device_id == "device-1"
    assert d.target_device_id == ""  # broadcast
    assert d.domain_key == SyncDomainKeys.EPISODIC_MEMORY
    assert d.payload == b"blob"
    assert d.delivery_mode == SyncDeliveryMode.GUARANTEED
    assert d.ttl is None
    assert d.sequence > 0


async def test_push_honours_explicit_delivery_mode() -> None:
    channel = _FakeChannel()
    svc = MemorySyncService(channel, InMemoryEpisodicStore(), "device-1")
    await svc.push_memory_delta_async(
        "o", "persona", b"x", mode=SyncDeliveryMode.URGENT
    )
    assert channel.pushed[0].delivery_mode == SyncDeliveryMode.URGENT


# ── receive ───────────────────────────────────────────────────────────────────


async def test_receive_applies_foreign_episodic_deltas_and_skips_own_echoes() -> None:
    inbound = [
        _delta("owner-1", "other-device", SyncDomainKeys.EPISODIC_MEMORY, b"a", 1),
        _delta("owner-1", "device-1", SyncDomainKeys.EPISODIC_MEMORY, b"echo", 2),
        _delta("owner-1", "other-device", SyncDomainKeys.EPISODIC_MEMORY, b"b", 3),
    ]
    channel = _FakeChannel(inbound)
    svc = MemorySyncService(channel, InMemoryEpisodicStore(), "device-1")
    await svc.start_receiving_async("owner-1")
    await asyncio.sleep(0.02)  # let the loop drain the scripted inbound
    await svc.stop_receiving_async()
    channel.close()

    payloads = [d.payload for d in svc.received]
    assert payloads == [b"a", b"b"]  # own echo skipped


async def test_receive_ignores_non_episodic_domains() -> None:
    inbound = [
        _delta("owner-1", "other", SyncDomainKeys.PERSONA, b"p", 1),
        _delta("owner-1", "other", SyncDomainKeys.EPISODIC_MEMORY, b"e", 2),
    ]
    channel = _FakeChannel(inbound)
    svc = MemorySyncService(channel, InMemoryEpisodicStore(), "device-1")
    await svc.start_receiving_async("owner-1")
    await asyncio.sleep(0.02)
    await svc.stop_receiving_async()
    channel.close()
    assert [d.payload for d in svc.received] == [b"e"]


async def test_stop_receiving_before_start_is_safe() -> None:
    svc = MemorySyncService(_FakeChannel(), InMemoryEpisodicStore(), "d")
    await svc.stop_receiving_async()  # must not raise


# ── SyncReconciliation ────────────────────────────────────────────────────────


def test_version_vector_merge_takes_elementwise_max() -> None:
    a = VersionVector({"n1": 5, "n2": 2})
    b = VersionVector({"n2": 9, "n3": 1})
    merged = SyncReconciliation.merge(a, b)
    assert merged.clocks == {"n1": 5, "n2": 9, "n3": 1}


def test_a_dominates_b_true_when_ge_everywhere_and_strictly_greater_once() -> None:
    a = VersionVector({"n1": 5, "n2": 3})
    b = VersionVector({"n1": 5, "n2": 2})
    assert SyncReconciliation.a_dominates_b(a, b) is True


def test_a_dominates_b_false_when_any_component_is_behind() -> None:
    a = VersionVector({"n1": 5, "n2": 1})
    b = VersionVector({"n1": 5, "n2": 2})
    assert SyncReconciliation.a_dominates_b(a, b) is False


def test_a_dominates_b_false_when_equal() -> None:
    a = VersionVector({"n1": 5})
    b = VersionVector({"n1": 5})
    assert SyncReconciliation.a_dominates_b(a, b) is False


def test_last_writer_wins_picks_later_timestamp_ties_favour_a() -> None:
    t1 = datetime(2026, 1, 1, tzinfo=timezone.utc)
    t2 = datetime(2026, 2, 1, tzinfo=timezone.utc)
    assert SyncReconciliation.last_writer_wins((t1, "old"), (t2, "new")) == (t2, "new")
    assert SyncReconciliation.last_writer_wins((t2, "new"), (t1, "old")) == (t2, "new")
    # Equal timestamps → a wins.
    assert SyncReconciliation.last_writer_wins((t1, "A"), (t1, "B")) == (t1, "A")


def test_merge_rejects_none() -> None:
    with pytest.raises(ValueError):
        SyncReconciliation.merge(None, VersionVector({}))
