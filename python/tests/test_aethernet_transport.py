"""test_aethernet_transport.py

Verifies the AetherNet mesh transport module: AetherPeerKind ordinals, the
peer/hop/packet records, InMemoryAetherNetRegistry, and — over a two-node
InMemoryAetherMeshEngine — AetherNetworkTransport (availability gated by
IAetherContext, routed send/receive, SOS-priority send), AetherPeerDiscovery
(presence beacons), and AetherSyncChannel (DTN delivery + last-sequence),
including the Wave-1 concurrency guarantees.

Mirrors CircleAI.Networking.AetherNet AetherNetTransportCommons.cs /
AetherNetworkTransport.cs / AetherPeerDiscovery.cs / AetherSyncChannel.cs
(C# — the spec).
"""
from __future__ import annotations

import asyncio
import dataclasses
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.aether import AetherInstallLevel, InMemoryAetherContext
from circle_ai.networking import (
    AetherHopTelemetry,
    AetherNetworkTransport,
    AetherPacketSummary,
    AetherPeer,
    AetherPeerDiscovery,
    AetherPeerKind,
    AetherSyncChannel,
    InMemoryAetherMeshEngine,
    InMemoryAetherNetRegistry,
    MessagePriority,
    NetworkPayload,
    PeerInfo,
    PeerRole,
    TransportKind,
)
from circle_ai.sync import SyncDelta, SyncDeliveryMode


def _now() -> datetime:
    return datetime.now(timezone.utc)


async def _next(it, timeout: float = 1.0):
    return await asyncio.wait_for(it.__anext__(), timeout=timeout)


def _ctx(available: bool = True) -> InMemoryAetherContext:
    # APP install + enabled -> is_available True; disabled -> False.
    return InMemoryAetherContext(
        install_level=AetherInstallLevel.APP,
        runtime_version="3.3.0",
        is_enabled=available,
    )


def _peer_info(node_id: str, name: str = "Peer") -> PeerInfo:
    return PeerInfo(
        node_id=node_id,
        display_name=name,
        supported_transports=(TransportKind.AETHER,),
        role=PeerRole.PEER,
        signal_strength_dbm=-50,
        last_seen=_now(),
    )


def _delta(*, owner="owner-1", tgt="node-B", seq=1, payload=b"d") -> SyncDelta:
    return SyncDelta(
        owner_id=owner,
        source_device_id="node-A",
        target_device_id=tgt,
        domain_key="memory.episodic",
        payload=payload,
        sequence=seq,
        delivery_mode=SyncDeliveryMode.GUARANTEED,
        ttl=None,
        created_at=_now(),
    )


# ── AetherPeerKind ───────────────────────────────────────────────────────────


def test_peer_kind_ordinals_match_csharp() -> None:
    assert int(AetherPeerKind.PHONE) == 0
    assert int(AetherPeerKind.TABLET) == 1
    assert int(AetherPeerKind.LAPTOP) == 2
    assert int(AetherPeerKind.DESKTOP) == 3
    assert int(AetherPeerKind.EDGE) == 4
    assert int(AetherPeerKind.VEHICLE) == 5
    assert int(AetherPeerKind.IOT) == 6


# ── records ──────────────────────────────────────────────────────────────────


def test_aether_peer_record_is_frozen_allows_null_name() -> None:
    p = AetherPeer("p1", AetherPeerKind.PHONE, None, ("chat", "voice"))
    assert p.friendly_name is None
    assert list(p.advertised_capabilities) == ["chat", "voice"]
    with pytest.raises(dataclasses.FrozenInstanceError):
        p.peer_id = "x"  # type: ignore[misc]


# ── InMemoryAetherNetRegistry ────────────────────────────────────────────────


def test_registry_register_get_and_ordered_peers() -> None:
    reg = InMemoryAetherNetRegistry()
    reg.register(AetherPeer("p2", AetherPeerKind.LAPTOP, None, ()))
    reg.register(AetherPeer("p1", AetherPeerKind.PHONE, None, ()))
    assert reg.get_peer("p1").kind is AetherPeerKind.PHONE
    assert reg.get_peer("missing") is None
    # peers ordered by peer_id (C#: OrderBy(p => p.PeerId))
    assert [p.peer_id for p in reg.peers] == ["p1", "p2"]


def test_registry_avg_round_trip_empty_is_zero() -> None:
    reg = InMemoryAetherNetRegistry()
    assert reg.avg_round_trip_ms("p1") == 0.0


def test_registry_avg_round_trip_averages() -> None:
    reg = InMemoryAetherNetRegistry()
    reg.record_hop(AetherHopTelemetry("p1", 1, 10.0, _now()))
    reg.record_hop(AetherHopTelemetry("p1", 2, 30.0, _now()))
    reg.record_hop(AetherHopTelemetry("p2", 1, 999.0, _now()))
    assert reg.avg_round_trip_ms("p1") == 20.0


def test_registry_total_bytes_between() -> None:
    reg = InMemoryAetherNetRegistry()
    reg.record_packet(AetherPacketSummary("k1", "A", "B", 100, "data", _now()))
    reg.record_packet(AetherPacketSummary("k2", "A", "B", 50, "data", _now()))
    reg.record_packet(AetherPacketSummary("k3", "A", "C", 999, "data", _now()))
    assert reg.total_bytes_between("A", "B") == 150
    assert reg.total_bytes_between("A", "C") == 999
    assert reg.total_bytes_between("A", "Z") == 0


def test_registry_recent_packets_newest_first_and_limited() -> None:
    reg = InMemoryAetherNetRegistry()
    base = _now()
    for i in range(5):
        reg.record_packet(
            AetherPacketSummary(f"k{i}", "A", "B", 1, "data", base + timedelta(seconds=i))
        )
    recent = reg.recent_packets(limit=3)
    assert len(recent) == 3
    assert recent[0].packet_id == "k4"


# ── AetherNetworkTransport ───────────────────────────────────────────────────


def test_transport_kind_is_aether() -> None:
    engine = InMemoryAetherMeshEngine("node-A")
    t = AetherNetworkTransport(_ctx(), engine)
    assert t.kind is TransportKind.AETHER


def test_transport_rejects_none_args() -> None:
    engine = InMemoryAetherMeshEngine("node-A")
    with pytest.raises(ValueError):
        AetherNetworkTransport(None, engine)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        AetherNetworkTransport(_ctx(), None)  # type: ignore[arg-type]


def test_transport_availability_gated_by_context() -> None:
    engine = InMemoryAetherMeshEngine("node-A")
    assert AetherNetworkTransport(_ctx(available=True), engine).is_available is True
    assert AetherNetworkTransport(_ctx(available=False), engine).is_available is False


async def test_routed_send_reaches_linked_peer() -> None:
    eng_a = InMemoryAetherMeshEngine("node-A")
    eng_b = InMemoryAetherMeshEngine("node-B")
    eng_a.link(eng_b)

    ta = AetherNetworkTransport(_ctx(), eng_a)
    tb = AetherNetworkTransport(_ctx(), eng_b)
    await ta.start_async()
    await tb.start_async()

    rx_b = tb.receive_async()
    await ta.send_async(
        NetworkPayload.create(b"aether-hello", destination_id="node-B")
    )
    got = await _next(rx_b)
    assert got.data == b"aether-hello"


async def test_broadcast_send_reaches_all_links() -> None:
    eng_a = InMemoryAetherMeshEngine("node-A")
    eng_b = InMemoryAetherMeshEngine("node-B")
    eng_c = InMemoryAetherMeshEngine("node-C")
    eng_a.link(eng_b)
    eng_a.link(eng_c)

    ta = AetherNetworkTransport(_ctx(), eng_a)
    tb = AetherNetworkTransport(_ctx(), eng_b)
    tc = AetherNetworkTransport(_ctx(), eng_c)
    for t in (ta, tb, tc):
        await t.start_async()

    rx_b = tb.receive_async()
    rx_c = tc.receive_async()
    # no destination -> broadcast to all links
    await ta.send_async(NetworkPayload.create(b"all"))
    assert (await _next(rx_b)).data == b"all"
    assert (await _next(rx_c)).data == b"all"


async def test_emergency_priority_still_routes() -> None:
    eng_a = InMemoryAetherMeshEngine("node-A")
    eng_b = InMemoryAetherMeshEngine("node-B")
    eng_a.link(eng_b)
    ta = AetherNetworkTransport(_ctx(), eng_a)
    tb = AetherNetworkTransport(_ctx(), eng_b)
    await ta.start_async()
    await tb.start_async()
    rx_b = tb.receive_async()
    await ta.send_async(
        NetworkPayload.create(
            b"SOS", destination_id="node-B", priority=MessagePriority.EMERGENCY
        )
    )
    got = await _next(rx_b)
    assert got.data == b"SOS"
    assert got.priority is MessagePriority.EMERGENCY


async def test_send_none_raises() -> None:
    engine = InMemoryAetherMeshEngine("node-A")
    t = AetherNetworkTransport(_ctx(), engine)
    await t.start_async()
    with pytest.raises(ValueError):
        await t.send_async(None)  # type: ignore[arg-type]


async def test_message_sent_immediately_after_subscribe_not_lost() -> None:
    eng_a = InMemoryAetherMeshEngine("node-A")
    eng_b = InMemoryAetherMeshEngine("node-B")
    eng_a.link(eng_b)
    ta = AetherNetworkTransport(_ctx(), eng_a)
    tb = AetherNetworkTransport(_ctx(), eng_b)
    await ta.start_async()
    await tb.start_async()
    rx_b = tb.receive_async()  # synchronous subscribe
    await ta.send_async(NetworkPayload.create(b"race", destination_id="node-B"))
    assert (await _next(rx_b)).data == b"race"


async def test_stop_completes_receive_loop() -> None:
    engine = InMemoryAetherMeshEngine("node-A")
    t = AetherNetworkTransport(_ctx(), engine)
    await t.start_async()
    rx = t.receive_async()
    await t.stop_async()
    assert [item async for item in rx] == []


# ── AetherPeerDiscovery ──────────────────────────────────────────────────────


async def test_announce_makes_peer_discoverable_on_neighbour() -> None:
    eng_a = InMemoryAetherMeshEngine("node-A")
    eng_b = InMemoryAetherMeshEngine("node-B")
    eng_a.link(eng_b)

    disc_a = AetherPeerDiscovery(_ctx(), eng_a)
    disc_b = AetherPeerDiscovery(_ctx(), eng_b)

    # B is listening for discoveries; A announces itself.
    rx_b = disc_b.discover_async()
    await disc_a.announce_async(_peer_info("node-A", "Alice"))
    found = await _next(rx_b)
    assert found.node_id == "node-A"
    assert found.display_name == "Alice"


async def test_discovery_rejects_none_announce() -> None:
    engine = InMemoryAetherMeshEngine("node-A")
    disc = AetherPeerDiscovery(_ctx(), engine)
    with pytest.raises(ValueError):
        await disc.announce_async(None)  # type: ignore[arg-type]


async def test_discovery_stream_fan_out() -> None:
    eng_a = InMemoryAetherMeshEngine("node-A")
    eng_b = InMemoryAetherMeshEngine("node-B")
    eng_a.link(eng_b)
    disc_a = AetherPeerDiscovery(_ctx(), eng_a)
    disc_b = AetherPeerDiscovery(_ctx(), eng_b)
    rx1 = disc_b.discover_async()
    rx2 = disc_b.discover_async()
    await disc_a.announce_async(_peer_info("node-A"))
    assert (await _next(rx1)).node_id == "node-A"
    assert (await _next(rx2)).node_id == "node-A"


# ── AetherSyncChannel ────────────────────────────────────────────────────────


async def test_push_delta_delivers_to_target_owner_queue() -> None:
    eng_a = InMemoryAetherMeshEngine("node-A")
    eng_b = InMemoryAetherMeshEngine("node-B")
    eng_a.link(eng_b)

    ch_a = AetherSyncChannel(_ctx(), eng_a)
    ch_b = AetherSyncChannel(_ctx(), eng_b)

    rx_b = ch_b.receive_deltas_async("owner-1")
    await ch_a.push_delta_async(_delta(owner="owner-1", tgt="node-B", payload=b"memory"))
    got = await _next(rx_b)
    assert got.payload == b"memory"
    assert got.owner_id == "owner-1"


async def test_push_delta_tracks_last_sequence_locally() -> None:
    engine = InMemoryAetherMeshEngine("node-A")
    ch = AetherSyncChannel(_ctx(), engine)
    assert await ch.get_last_sequence_async("owner-1", "memory.episodic") == 0
    await ch.push_delta_async(_delta(owner="owner-1", seq=5))
    assert await ch.get_last_sequence_async("owner-1", "memory.episodic") == 5
    # lower sequence does not regress
    await ch.push_delta_async(_delta(owner="owner-1", seq=3))
    assert await ch.get_last_sequence_async("owner-1", "memory.episodic") == 5


async def test_push_delta_none_raises() -> None:
    engine = InMemoryAetherMeshEngine("node-A")
    ch = AetherSyncChannel(_ctx(), engine)
    with pytest.raises(ValueError):
        await ch.push_delta_async(None)  # type: ignore[arg-type]


async def test_delivery_stream_unbounded_buffering() -> None:
    eng_a = InMemoryAetherMeshEngine("node-A")
    eng_b = InMemoryAetherMeshEngine("node-B")
    eng_a.link(eng_b)
    ch_a = AetherSyncChannel(_ctx(), eng_a)
    ch_b = AetherSyncChannel(_ctx(), eng_b)
    rx_b = ch_b.receive_deltas_async("owner-1")
    for i in range(12):
        await ch_a.push_delta_async(
            _delta(owner="owner-1", tgt="node-B", seq=i + 1, payload=str(i).encode())
        )
    received = [(await _next(rx_b)).payload for _ in range(12)]
    assert received == [str(i).encode() for i in range(12)]


def test_sync_channel_rejects_none_args() -> None:
    engine = InMemoryAetherMeshEngine("node-A")
    with pytest.raises(ValueError):
        AetherSyncChannel(None, engine)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        AetherSyncChannel(_ctx(), None)  # type: ignore[arg-type]


# ── engine guardrails ────────────────────────────────────────────────────────


def test_engine_rejects_blank_node_id() -> None:
    with pytest.raises(ValueError):
        InMemoryAetherMeshEngine("  ")


def test_engine_link_rejects_self_and_none() -> None:
    eng = InMemoryAetherMeshEngine("node-A")
    with pytest.raises(ValueError):
        eng.link(eng)
    with pytest.raises(ValueError):
        eng.link(None)  # type: ignore[arg-type]
