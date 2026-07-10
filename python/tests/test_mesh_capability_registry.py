"""test_mesh_capability_registry.py — RT-12 v1 mesh capability discovery.

Covers upsert/replace, idempotent remove, staleness filtering with a clock
override, and the case-insensitive model + min-budget + sorted-descending find.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.aethernet import (
    InMemoryMeshCapabilityRegistry,
    MeshCapabilityAdvertisement,
    NullMeshCapabilityBroadcaster,
)
from circle_ai.device import DeviceTier


def _ad(peer, model="Qwen3-1.7B-MNN", kv=2048, at=None):
    return MeshCapabilityAdvertisement(
        peer_id=peer,
        model_id=model,
        free_kv_tokens=kv,
        tier=DeviceTier.PHONE,
        context_window_tokens=4096,
        advertised_at_utc=at or datetime.now(timezone.utc),
    )


async def test_upsert_replaces_by_peer_id():
    reg = InMemoryMeshCapabilityRegistry()
    await reg.upsert_async(_ad("p1", kv=100))
    await reg.upsert_async(_ad("p1", kv=200))
    entries = reg.list()
    assert len(entries) == 1
    assert entries[0].free_kv_tokens == 200


async def test_upsert_rejects_blank_peer():
    reg = InMemoryMeshCapabilityRegistry()
    with pytest.raises(ValueError):
        await reg.upsert_async(_ad("   "))


async def test_remove_is_idempotent():
    reg = InMemoryMeshCapabilityRegistry()
    await reg.upsert_async(_ad("p1"))
    assert await reg.remove_async("p1") is True
    assert await reg.remove_async("p1") is False


async def test_list_stale_filter():
    now = datetime(2026, 7, 10, 12, 0, 0, tzinfo=timezone.utc)
    reg = InMemoryMeshCapabilityRegistry(now_utc=lambda: now)
    await reg.upsert_async(_ad("fresh", at=now - timedelta(seconds=10)))
    await reg.upsert_async(_ad("stale", at=now - timedelta(seconds=120)))
    all_entries = reg.list()
    assert len(all_entries) == 2
    fresh_only = reg.list(stale_after=timedelta(seconds=60))
    assert [a.peer_id for a in fresh_only] == ["fresh"]


async def test_find_case_insensitive_min_budget_sorted():
    now = datetime(2026, 7, 10, 12, 0, 0, tzinfo=timezone.utc)
    reg = InMemoryMeshCapabilityRegistry(now_utc=lambda: now)
    await reg.upsert_async(_ad("low", model="Qwen3-1.7B-MNN", kv=500, at=now))
    await reg.upsert_async(_ad("high", model="qwen3-1.7b-mnn", kv=3000, at=now))
    await reg.upsert_async(_ad("other", model="Llama-3B", kv=9000, at=now))

    # Case-insensitive model match, min budget 1000, sorted by budget desc.
    got = reg.find("QWEN3-1.7B-MNN", min_free_kv_tokens=1000)
    assert [a.peer_id for a in got] == ["high"]

    got_all = reg.find("qwen3-1.7b-mnn")
    assert [a.peer_id for a in got_all] == ["high", "low"]  # descending budget


async def test_find_respects_staleness():
    now = datetime(2026, 7, 10, 12, 0, 0, tzinfo=timezone.utc)
    reg = InMemoryMeshCapabilityRegistry(now_utc=lambda: now)
    await reg.upsert_async(_ad("old", kv=5000, at=now - timedelta(seconds=300)))
    assert reg.find("Qwen3-1.7B-MNN", stale_after=timedelta(seconds=60)) == []
    assert len(reg.find("Qwen3-1.7B-MNN")) == 1


async def test_find_rejects_blank_model():
    reg = InMemoryMeshCapabilityRegistry()
    with pytest.raises(ValueError):
        reg.find("  ")


async def test_null_broadcaster_is_noop():
    b = NullMeshCapabilityBroadcaster.instance
    # Must not raise; returns without side effects.
    await b.broadcast_async(_ad("p"))
    assert isinstance(NullMeshCapabilityBroadcaster.instance, NullMeshCapabilityBroadcaster)


def test_advertisement_is_frozen():
    ad = _ad("p")
    with pytest.raises(Exception):
        ad.free_kv_tokens = 1  # type: ignore[misc]
