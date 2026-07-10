"""test_network_transport.py

Verifies the in-memory transport abstraction end-to-end:
InMemoryWire routing, InMemoryNetworkTransport send/receive (with the Wave-1
concurrency guarantees — synchronous subscribe, unbounded fan-out buffering, no
teardown self-deadlock), InMemoryMeshNetwork topology/health,
TransportMessageChannel typed round-trips, and InMemoryConnectivityMonitor
watch fan-out.

Mirrors CircleAI.Networking INetworkTransport / IMeshNetwork / IMessageChannel /
IConnectivityMonitor (C# — the spec).
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timezone

import pytest

from circle_ai.networking import (
    ConnectivityState,
    InMemoryConnectivityMonitor,
    InMemoryMeshNetwork,
    InMemoryNetworkTransport,
    InMemoryWire,
    JsonMessageSerializer,
    MessagePriority,
    NetworkContext,
    NetworkPayload,
    TransportKind,
    TransportMessageChannel,
)


async def _next(it, timeout: float = 1.0):
    return await asyncio.wait_for(it.__anext__(), timeout=timeout)


# ── InMemoryNetworkTransport basics ──────────────────────────────────────────


async def test_transport_start_stop_availability() -> None:
    t = InMemoryNetworkTransport(InMemoryWire(), "A", TransportKind.WIFI)
    assert t.kind is TransportKind.WIFI
    assert t.is_available is False
    await t.start_async()
    assert t.is_available is True
    await t.stop_async()
    assert t.is_available is False


async def test_send_before_start_raises() -> None:
    t = InMemoryNetworkTransport(InMemoryWire(), "A")
    with pytest.raises(RuntimeError):
        await t.send_async(NetworkPayload.create(b"x"))


def test_transport_rejects_blank_node_id() -> None:
    with pytest.raises(ValueError):
        InMemoryNetworkTransport(InMemoryWire(), "  ")


async def test_directed_send_reaches_only_destination() -> None:
    wire = InMemoryWire()
    a = InMemoryNetworkTransport(wire, "A")
    b = InMemoryNetworkTransport(wire, "B")
    c = InMemoryNetworkTransport(wire, "C")
    for t in (a, b, c):
        await t.start_async()

    rx_b = b.receive_async()
    rx_c = c.receive_async()
    await a.send_async(NetworkPayload.create(b"for-B", destination_id="B"))

    got = await _next(rx_b)
    assert got.data == b"for-B"
    assert got.source_id == "A"  # source stamped by the sending transport
    # C must NOT receive a directed B message.
    with pytest.raises(asyncio.TimeoutError):
        await _next(rx_c, timeout=0.15)


async def test_broadcast_reaches_all_other_peers_not_self() -> None:
    wire = InMemoryWire()
    a = InMemoryNetworkTransport(wire, "A")
    b = InMemoryNetworkTransport(wire, "B")
    c = InMemoryNetworkTransport(wire, "C")
    for t in (a, b, c):
        await t.start_async()

    rx_a = a.receive_async()
    rx_b = b.receive_async()
    rx_c = c.receive_async()
    # No destination -> broadcast.
    await a.send_async(NetworkPayload.create(b"hello-all"))

    assert (await _next(rx_b)).data == b"hello-all"
    assert (await _next(rx_c)).data == b"hello-all"
    # Sender does not receive its own broadcast (loopback echo suppressed).
    with pytest.raises(asyncio.TimeoutError):
        await _next(rx_a, timeout=0.15)


# ── Wave-1 concurrency guarantees ────────────────────────────────────────────


async def test_message_sent_immediately_after_subscribe_is_not_lost() -> None:
    """Subscribe synchronously, publish on the very next line — the payload
    must be buffered, never raced away (the Wave-1 lost-message bug)."""
    wire = InMemoryWire()
    a = InMemoryNetworkTransport(wire, "A")
    b = InMemoryNetworkTransport(wire, "B")
    await a.start_async()
    await b.start_async()

    rx = b.receive_async()  # registers B's queue synchronously
    await a.send_async(NetworkPayload.create(b"race", destination_id="B"))
    assert (await _next(rx)).data == b"race"


async def test_unbounded_buffering_retains_messages_sent_before_drain() -> None:
    wire = InMemoryWire()
    a = InMemoryNetworkTransport(wire, "A")
    b = InMemoryNetworkTransport(wire, "B")
    await a.start_async()
    await b.start_async()

    rx = b.receive_async()
    # Fire many payloads before reading a single one — none may block or drop.
    for i in range(25):
        await a.send_async(
            NetworkPayload.create(str(i).encode(), destination_id="B")
        )
    received = [(await _next(rx)).data for _ in range(25)]
    assert received == [str(i).encode() for i in range(25)]


async def test_fan_out_to_multiple_receivers_on_same_transport() -> None:
    wire = InMemoryWire()
    a = InMemoryNetworkTransport(wire, "A")
    b = InMemoryNetworkTransport(wire, "B")
    await a.start_async()
    await b.start_async()

    rx1 = b.receive_async()
    rx2 = b.receive_async()
    await a.send_async(NetworkPayload.create(b"dup", destination_id="B"))
    assert (await _next(rx1)).data == b"dup"
    assert (await _next(rx2)).data == b"dup"


async def test_stop_ends_live_receivers_without_deadlock() -> None:
    wire = InMemoryWire()
    a = InMemoryNetworkTransport(wire, "A")
    b = InMemoryNetworkTransport(wire, "B")
    await a.start_async()
    await b.start_async()

    rx = b.receive_async()
    # Stopping must terminate the iterator (the finally-block deregister takes
    # the same lock delivery uses — must not self-deadlock).
    await b.stop_async()
    collected = [item async for item in rx]
    assert collected == []


async def test_receiver_deregisters_on_break() -> None:
    wire = InMemoryWire()
    a = InMemoryNetworkTransport(wire, "A")
    b = InMemoryNetworkTransport(wire, "B")
    await a.start_async()
    await b.start_async()

    rx = b.receive_async()
    await a.send_async(NetworkPayload.create(b"one", destination_id="B"))
    async for _ in rx:
        break  # exits generator -> finally deregisters the queue
    # A second directed send has no live receiver; nothing raises.
    await a.send_async(NetworkPayload.create(b"two", destination_id="B"))


# ── InMemoryMeshNetwork ──────────────────────────────────────────────────────


async def test_mesh_peer_ids_reflect_attached_transports() -> None:
    wire = InMemoryWire()
    a = InMemoryNetworkTransport(wire, "A")
    b = InMemoryNetworkTransport(wire, "B")
    mesh = InMemoryMeshNetwork(wire, "A")
    await a.start_async()
    await b.start_async()

    assert mesh.local_node_id == "A"
    assert sorted(await mesh.get_peer_ids_async()) == ["B"]

    c = InMemoryNetworkTransport(wire, "C")
    await c.start_async()
    assert sorted(await mesh.get_peer_ids_async()) == ["B", "C"]


async def test_mesh_health_reflects_peer_presence() -> None:
    wire = InMemoryWire()
    a = InMemoryNetworkTransport(wire, "A")
    mesh = InMemoryMeshNetwork(wire, "A")
    await a.start_async()

    # No peers -> offline health.
    h0 = await mesh.get_mesh_health_async()
    assert h0.state is ConnectivityState.OFFLINE
    assert h0.nearby_peer_count == 0

    b = InMemoryNetworkTransport(wire, "B")
    await b.start_async()
    h1 = await mesh.get_mesh_health_async()
    assert h1.state is ConnectivityState.MESH_ONLY
    assert h1.nearby_peer_count == 1
    assert TransportKind.AETHER in h1.available_transports


# ── TransportMessageChannel ──────────────────────────────────────────────────


async def test_channel_dict_round_trip() -> None:
    wire = InMemoryWire()
    ta = InMemoryNetworkTransport(wire, "A")
    tb = InMemoryNetworkTransport(wire, "B")
    await ta.start_async()
    await tb.start_async()
    ca = TransportMessageChannel(ta)
    cb = TransportMessageChannel(tb)

    rx = cb.receive_async(dict)
    await ca.send_async("B", {"k": 1, "v": "hi"})
    assert await _next(rx) == {"k": 1, "v": "hi"}


async def test_channel_str_round_trip() -> None:
    wire = InMemoryWire()
    ta = InMemoryNetworkTransport(wire, "A")
    tb = InMemoryNetworkTransport(wire, "B")
    await ta.start_async()
    await tb.start_async()
    ca = TransportMessageChannel(ta)
    cb = TransportMessageChannel(tb)

    rx = cb.receive_async(str)
    await ca.send_async("B", "plain-text")
    assert await _next(rx) == "plain-text"


async def test_channel_uses_json_content_type_and_priority() -> None:
    wire = InMemoryWire()
    ta = InMemoryNetworkTransport(wire, "A")
    tb = InMemoryNetworkTransport(wire, "B")
    await ta.start_async()
    await tb.start_async()
    ca = TransportMessageChannel(ta, priority=MessagePriority.HIGH)

    # Read at the raw transport layer to inspect the produced payload.
    rx_raw = tb.receive_async()
    await ca.send_async("B", {"x": 1})
    payload = await _next(rx_raw)
    assert payload.content_type == "application/json"
    assert payload.priority is MessagePriority.HIGH
    assert payload.destination_id == "B"


async def test_channel_send_message_immediately_after_subscribe() -> None:
    # Same race guarantee, but exercised through the channel layer (whose
    # receive_async opens the underlying transport iterator synchronously).
    wire = InMemoryWire()
    ta = InMemoryNetworkTransport(wire, "A")
    tb = InMemoryNetworkTransport(wire, "B")
    await ta.start_async()
    await tb.start_async()
    ca = TransportMessageChannel(ta)
    cb = TransportMessageChannel(tb)

    rx = cb.receive_async(str)
    await ca.send_async("B", "no-race")
    assert await _next(rx) == "no-race"


def test_json_serializer_bytes_passthrough() -> None:
    s = JsonMessageSerializer()
    assert s.serialize(b"raw") == b"raw"
    assert s.deserialize(b"raw", bytes) == b"raw"


def test_json_serializer_stable_sorted_keys() -> None:
    s = JsonMessageSerializer()
    # Key order in the source dict must not change the wire bytes.
    a = s.serialize({"b": 1, "a": 2})
    b = s.serialize({"a": 2, "b": 1})
    assert a == b


# ── InMemoryConnectivityMonitor ──────────────────────────────────────────────


def _online_ctx() -> NetworkContext:
    return NetworkContext(
        state=ConnectivityState.ONLINE,
        preferred_transport=TransportKind.GRPC,
        available_transports=[TransportKind.GRPC],
        signal_strength_dbm=-40,
        estimated_bandwidth_bps=1_000_000,
        latency_ms=20,
        nearby_peer_count=3,
        snapshot_at=datetime.now(timezone.utc),
    )


async def test_monitor_defaults_to_offline() -> None:
    m = InMemoryConnectivityMonitor()
    assert m.current_state is ConnectivityState.OFFLINE
    assert m.get_snapshot().state is ConnectivityState.OFFLINE


async def test_monitor_watch_yields_current_then_updates() -> None:
    m = InMemoryConnectivityMonitor()
    w = m.watch_async()
    # First yield is the snapshot at subscribe time.
    first = await _next(w)
    assert first.state is ConnectivityState.OFFLINE

    ctx = _online_ctx()
    m.set_context(ctx)
    nxt = await _next(w)
    assert nxt.state is ConnectivityState.ONLINE
    assert nxt.nearby_peer_count == 3
    assert m.current_state is ConnectivityState.ONLINE


async def test_monitor_fan_out_to_multiple_watchers() -> None:
    m = InMemoryConnectivityMonitor()
    w1 = m.watch_async()
    w2 = m.watch_async()
    # drain the seeded snapshot from each
    await _next(w1)
    await _next(w2)
    assert m.watcher_count == 2

    m.set_context(_online_ctx())
    assert (await _next(w1)).state is ConnectivityState.ONLINE
    assert (await _next(w2)).state is ConnectivityState.ONLINE


async def test_monitor_close_ends_watchers() -> None:
    m = InMemoryConnectivityMonitor()
    w = m.watch_async()
    await _next(w)  # seeded snapshot
    m.close()
    assert [x async for x in w] == []
    assert m.watcher_count == 0


async def test_monitor_watcher_deregisters_on_close() -> None:
    m = InMemoryConnectivityMonitor()
    w = m.watch_async()
    await _next(w)  # consume the seeded snapshot
    assert m.watcher_count == 1
    # Closing the async generator runs its finally-block, which deregisters the
    # watcher (a bare `break` defers that to GC, so close explicitly).
    await w.aclose()
    assert m.watcher_count == 0
