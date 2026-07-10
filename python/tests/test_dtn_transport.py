"""test_dtn_transport.py

Verifies the DTN (delay-tolerant-networking) transport module:
DtnPriority ordinals, the DtnBundle / DtnCustodyRecord records,
InMemoryDtnBundleStore (store/custody/expiry/purge/in-flight), and
DtnSyncChannel push/receive/last-sequence with the Wave-1 concurrency
guarantees (synchronous subscribe, unbounded fan-out buffering, no teardown
self-deadlock).

Mirrors CircleAI.Networking.Dtn DtnBundle.cs / DtnTransportCommons.cs /
DtnSyncChannel.cs (C# — the spec).
"""
from __future__ import annotations

import asyncio
import dataclasses
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.networking import (
    DtnBundle,
    DtnCustodyRecord,
    DtnPriority,
    DtnSyncChannel,
    InMemoryDtnBundleStore,
    InMemoryNetworkTransport,
    InMemoryWire,
    MessagePriority,
    TransportKind,
)
from circle_ai.sync import SyncDelta, SyncDeliveryMode


def _now() -> datetime:
    return datetime.now(timezone.utc)


async def _next(it, timeout: float = 1.0):
    return await asyncio.wait_for(it.__anext__(), timeout=timeout)


def _delta(
    *,
    owner="owner-1",
    src="dev-A",
    tgt="dev-B",
    domain="memory.episodic",
    payload=b"delta",
    seq=1,
    mode=SyncDeliveryMode.GUARANTEED,
    ttl=None,
) -> SyncDelta:
    return SyncDelta(
        owner_id=owner,
        source_device_id=src,
        target_device_id=tgt,
        domain_key=domain,
        payload=payload,
        sequence=seq,
        delivery_mode=mode,
        ttl=ttl,
        created_at=_now(),
    )


# ── DtnPriority ──────────────────────────────────────────────────────────────


def test_dtn_priority_ordinals_match_csharp() -> None:
    assert int(DtnPriority.BULK) == 0
    assert int(DtnPriority.NORMAL) == 1
    assert int(DtnPriority.EXPEDITED) == 2
    assert [int(p) for p in DtnPriority] == [0, 1, 2]


# ── DtnBundle / DtnCustodyRecord ─────────────────────────────────────────────


def test_dtn_bundle_is_frozen_record() -> None:
    now = _now()
    b = DtnBundle(
        bundle_id="b1",
        source_node_id="A",
        destination_node_id="B",
        payload=b"x",
        expires_at=now + timedelta(hours=72),
        custody_required=True,
        hop_count=0,
        created_at=now,
    )
    assert b.custody_required is True
    assert b.payload == b"x"
    with pytest.raises(dataclasses.FrozenInstanceError):
        b.hop_count = 1  # type: ignore[misc]


def test_dtn_custody_record_fields() -> None:
    now = _now()
    r = DtnCustodyRecord(bundle_id="b1", custodian_node="node-9", accepted_at_utc=now)
    assert r.bundle_id == "b1"
    assert r.custodian_node == "node-9"
    assert r.accepted_at_utc is now


# ── InMemoryDtnBundleStore ───────────────────────────────────────────────────


def _bundle(bid: str, *, dest: str = "B", expires_in_h: float = 72.0) -> DtnBundle:
    now = _now()
    return DtnBundle(
        bundle_id=bid,
        source_node_id="A",
        destination_node_id=dest,
        payload=b"p",
        expires_at=now + timedelta(hours=expires_in_h),
        custody_required=False,
        hop_count=0,
        created_at=now,
    )


def test_store_get_and_all() -> None:
    store = InMemoryDtnBundleStore()
    store.store(_bundle("b1"))
    store.store(_bundle("b2"))
    assert store.get("b1").bundle_id == "b1"
    assert store.get("missing") is None
    assert {b.bundle_id for b in store.all} == {"b1", "b2"}


def test_store_rejects_none() -> None:
    store = InMemoryDtnBundleStore()
    with pytest.raises(ValueError):
        store.store(None)  # type: ignore[arg-type]


def test_custody_accept_and_get() -> None:
    store = InMemoryDtnBundleStore()
    rec = DtnCustodyRecord("b1", "node-1", _now())
    store.accept_custody(rec)
    assert store.get_custody("b1") is rec
    assert store.get_custody("b2") is None


def test_is_expired_unknown_bundle_is_expired() -> None:
    store = InMemoryDtnBundleStore()
    # C#: unknown bundle id -> treated as expired (True).
    assert store.is_expired("nope", _now()) is True


def test_is_expired_respects_expiry() -> None:
    store = InMemoryDtnBundleStore()
    store.store(_bundle("b1", expires_in_h=72.0))
    now = _now()
    assert store.is_expired("b1", now) is False
    assert store.is_expired("b1", now + timedelta(hours=73)) is True


def test_purge_drops_expired_and_returns_count() -> None:
    store = InMemoryDtnBundleStore()
    store.store(_bundle("live", expires_in_h=72.0))
    store.store(_bundle("dead1", expires_in_h=-1.0))
    store.store(_bundle("dead2", expires_in_h=-5.0))
    # add custody for a dead bundle to confirm it is also purged
    store.accept_custody(DtnCustodyRecord("dead1", "n", _now()))
    purged = store.purge(_now())
    assert purged == 2
    assert {b.bundle_id for b in store.all} == {"live"}
    assert store.get_custody("dead1") is None


def test_in_flight_to_filters_by_destination() -> None:
    store = InMemoryDtnBundleStore()
    store.store(_bundle("b1", dest="B"))
    store.store(_bundle("b2", dest="C"))
    store.store(_bundle("b3", dest="B"))
    ids = {b.bundle_id for b in store.in_flight_to("B")}
    assert ids == {"b1", "b3"}


# ── DtnSyncChannel ───────────────────────────────────────────────────────────


async def test_push_delta_sends_over_first_available_transport() -> None:
    wire = InMemoryWire()
    # 'B' is the destination transport that should receive the payload.
    tb = InMemoryNetworkTransport(wire, "dev-B", TransportKind.HTTP)
    ta = InMemoryNetworkTransport(wire, "dev-A", TransportKind.HTTP)
    await tb.start_async()
    await ta.start_async()

    # Channel over the sending transport (ta is available).
    ch = DtnSyncChannel([ta])
    rx_b = tb.receive_async()
    await ch.push_delta_async(_delta(payload=b"hello-dtn", mode=SyncDeliveryMode.GUARANTEED))

    got = await _next(rx_b)
    assert got.data == b"hello-dtn"
    assert got.content_type == "application/dtn-bundle"
    assert got.destination_id == "dev-B"
    # Guaranteed -> normal priority (only Urgent maps to Urgent).
    assert got.priority is MessagePriority.NORMAL


async def test_push_delta_urgent_maps_to_urgent_priority() -> None:
    wire = InMemoryWire()
    tb = InMemoryNetworkTransport(wire, "dev-B")
    ta = InMemoryNetworkTransport(wire, "dev-A")
    await tb.start_async()
    await ta.start_async()
    ch = DtnSyncChannel([ta])
    rx_b = tb.receive_async()
    await ch.push_delta_async(_delta(mode=SyncDeliveryMode.URGENT))
    got = await _next(rx_b)
    assert got.priority is MessagePriority.URGENT


async def test_push_delta_with_no_available_transport_queues_bundle() -> None:
    wire = InMemoryWire()
    ta = InMemoryNetworkTransport(wire, "dev-A")
    # Not started -> not available. Bundle must be queued, not sent, no raise.
    ch = DtnSyncChannel([ta])
    await ch.push_delta_async(_delta(payload=b"queued"))
    queued = ch.queued_bundles
    assert len(queued) == 1
    assert queued[0].payload == b"queued"
    assert queued[0].custody_required is True  # Guaranteed


async def test_push_delta_custody_required_only_for_guaranteed() -> None:
    ch = DtnSyncChannel([])  # no transports -> everything queues
    await ch.push_delta_async(_delta(seq=1, mode=SyncDeliveryMode.BEST_EFFORT))
    await ch.push_delta_async(_delta(seq=2, mode=SyncDeliveryMode.GUARANTEED))
    flags = {b.payload: b.custody_required for b in ch.queued_bundles}
    # both have payload b"delta"; check the store holds two with mixed custody
    custody_values = sorted(b.custody_required for b in ch.queued_bundles)
    assert custody_values == [False, True]
    _ = flags


async def test_push_delta_default_ttl_is_72_hours() -> None:
    ch = DtnSyncChannel([])
    before = _now()
    await ch.push_delta_async(_delta(ttl=None))
    b = ch.queued_bundles[0]
    # expires_at ~ created_at + 72h
    delta_h = (b.expires_at - b.created_at).total_seconds() / 3600.0
    assert abs(delta_h - 72.0) < 0.001
    assert b.expires_at >= before + timedelta(hours=71)


async def test_push_delta_honours_explicit_ttl() -> None:
    ch = DtnSyncChannel([])
    await ch.push_delta_async(_delta(ttl=120.0))  # 120 seconds
    b = ch.queued_bundles[0]
    ttl_s = (b.expires_at - b.created_at).total_seconds()
    assert abs(ttl_s - 120.0) < 0.001


async def test_receive_deltas_delivers_injected_delta() -> None:
    ch = DtnSyncChannel([])
    rx = ch.receive_deltas_async("owner-1")
    ch.deliver(_delta(owner="owner-1", payload=b"arrived", seq=7))
    got = await _next(rx)
    assert got.payload == b"arrived"
    assert got.sequence == 7


async def test_receive_message_immediately_after_subscribe_is_not_lost() -> None:
    # Wave-1 guarantee: subscribe synchronously, deliver on the next line.
    ch = DtnSyncChannel([])
    rx = ch.receive_deltas_async("owner-1")
    ch.deliver(_delta(owner="owner-1", payload=b"race"))
    assert (await _next(rx)).payload == b"race"


async def test_receive_unbounded_buffering_retains_predrain_deltas() -> None:
    ch = DtnSyncChannel([])
    rx = ch.receive_deltas_async("owner-1")
    for i in range(20):
        ch.deliver(_delta(owner="owner-1", payload=str(i).encode(), seq=i + 1))
    received = [(await _next(rx)).payload for _ in range(20)]
    assert received == [str(i).encode() for i in range(20)]


async def test_receive_fan_out_to_multiple_iterators() -> None:
    ch = DtnSyncChannel([])
    rx1 = ch.receive_deltas_async("owner-1")
    rx2 = ch.receive_deltas_async("owner-1")
    ch.deliver(_delta(owner="owner-1", payload=b"dup"))
    assert (await _next(rx1)).payload == b"dup"
    assert (await _next(rx2)).payload == b"dup"


async def test_close_ends_live_receivers_without_deadlock() -> None:
    ch = DtnSyncChannel([])
    rx = ch.receive_deltas_async("owner-1")
    ch.close()
    collected = [d async for d in rx]
    assert collected == []


async def test_get_last_sequence_defaults_to_zero() -> None:
    ch = DtnSyncChannel([])
    assert await ch.get_last_sequence_async("owner-x", "memory.episodic") == 0


async def test_deliver_advances_last_sequence_monotonically() -> None:
    ch = DtnSyncChannel([])
    ch.deliver(_delta(owner="o", domain="d", seq=5))
    assert await ch.get_last_sequence_async("o", "d") == 5
    # A lower sequence must not regress the tracked value.
    ch.deliver(_delta(owner="o", domain="d", seq=3))
    assert await ch.get_last_sequence_async("o", "d") == 5
    ch.deliver(_delta(owner="o", domain="d", seq=9))
    assert await ch.get_last_sequence_async("o", "d") == 9
