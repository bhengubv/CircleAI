"""test_wifi_transport.py

Verifies the WiFi (LAN UDP) transport + peer-discovery module: the
DiscoveryPort/DataPort constants, the InMemoryUdpBus routing (broadcast fan-out,
unicast by IP), the IUdpSocket seam + InMemoryUdpSocket, WiFiNetworkTransport
send routing (IP destination -> unicast, else broadcast) and receive pump, and
WiFiPeerDiscovery's CIRCLEAI:BEACON: announce/discover protocol.

Mirrors CircleAI.Networking.WiFi WiFiNetworkTransport.cs /
WiFiPeerDiscovery.cs (C# — the spec).
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timezone

import pytest

from circle_ai.networking import (
    BROADCAST_ADDRESS,
    InMemoryUdpBus,
    InMemoryUdpSocket,
    NetworkPayload,
    PeerInfo,
    TransportKind,
    WiFiNetworkTransport,
    WiFiPeerDiscovery,
)
from circle_ai.networking.network_types import PeerRole


def _now() -> datetime:
    return datetime.now(timezone.utc)


async def _next(it, timeout: float = 1.0):
    return await asyncio.wait_for(it.__anext__(), timeout=timeout)


# ── constants ────────────────────────────────────────────────────────────────


def test_ports_match_csharp() -> None:
    assert WiFiNetworkTransport.DISCOVERY_PORT == 47890
    assert WiFiNetworkTransport.DATA_PORT == 47891
    assert BROADCAST_ADDRESS == "255.255.255.255"


def test_beacon_magic_matches_csharp() -> None:
    assert WiFiPeerDiscovery.BEACON_MAGIC == "CIRCLEAI:BEACON:"


# ── InMemoryUdpBus routing ───────────────────────────────────────────────────


async def test_bus_broadcast_reaches_all_bound_on_port() -> None:
    bus = InMemoryUdpBus()
    sender = InMemoryUdpSocket(bus, source_address="10.0.0.1", source_port=5000)
    r1 = InMemoryUdpSocket(bus, bind_address="10.0.0.2", bind_port=5000)
    r2 = InMemoryUdpSocket(bus, bind_address="10.0.0.3", bind_port=5000)
    await sender.send_async(b"bcast", BROADCAST_ADDRESS, 5000)
    got1 = await asyncio.wait_for(r1.receive_async(), 1.0)
    got2 = await asyncio.wait_for(r2.receive_async(), 1.0)
    assert got1.buffer == b"bcast"
    assert got2.buffer == b"bcast"
    assert got1.remote_address == "10.0.0.1"


async def test_bus_unicast_reaches_only_target_ip() -> None:
    bus = InMemoryUdpBus()
    sender = InMemoryUdpSocket(bus, source_address="10.0.0.1", source_port=5000)
    r1 = InMemoryUdpSocket(bus, bind_address="10.0.0.2", bind_port=5000)
    r2 = InMemoryUdpSocket(bus, bind_address="10.0.0.3", bind_port=5000)
    await sender.send_async(b"uni", "10.0.0.2", 5000)
    got1 = await asyncio.wait_for(r1.receive_async(), 1.0)
    assert got1.buffer == b"uni"
    # r2 must not have received it.
    with pytest.raises(asyncio.TimeoutError):
        await asyncio.wait_for(r2.receive_async(), 0.1)


async def test_socket_close_ends_blocked_receive() -> None:
    bus = InMemoryUdpBus()
    sock = InMemoryUdpSocket(bus, bind_address="10.0.0.2", bind_port=5000)

    async def _recv():
        with pytest.raises(RuntimeError):
            await sock.receive_async()

    task = asyncio.ensure_future(_recv())
    await asyncio.sleep(0)  # let the receive start blocking
    sock.close()
    await asyncio.wait_for(task, 1.0)


# ── WiFiNetworkTransport ─────────────────────────────────────────────────────


def test_transport_kind_is_wifi() -> None:
    bus = InMemoryUdpBus()
    t = WiFiNetworkTransport.in_memory(bus)
    assert t.kind is TransportKind.WIFI


def test_transport_rejects_none_sockets() -> None:
    bus = InMemoryUdpBus()
    sock = InMemoryUdpSocket(bus)
    with pytest.raises(ValueError):
        WiFiNetworkTransport(None, sock)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        WiFiNetworkTransport(sock, None)  # type: ignore[arg-type]


async def test_is_available_after_start() -> None:
    bus = InMemoryUdpBus()
    t = WiFiNetworkTransport.in_memory(bus)
    assert t.is_available is False
    await t.start_async()
    assert t.is_available is True
    await t.stop_async()
    assert t.is_available is False


async def test_broadcast_send_reaches_peer() -> None:
    bus = InMemoryUdpBus()
    a = WiFiNetworkTransport.in_memory(bus, source_address="10.0.0.1")
    b = WiFiNetworkTransport.in_memory(bus, source_address="10.0.0.2")
    await a.start_async()
    await b.start_async()
    brx = b.receive_async()
    # No destination -> broadcast to DATA_PORT, which peer b is bound to.
    await a.send_async(NetworkPayload.create(b"hello-lan"))
    assert (await _next(brx)).data == b"hello-lan"
    await a.stop_async()
    await b.stop_async()


async def test_unicast_send_by_ip_reaches_only_that_peer() -> None:
    bus = InMemoryUdpBus()
    a = WiFiNetworkTransport.in_memory(bus, source_address="10.0.0.1")
    b = WiFiNetworkTransport.in_memory(bus, source_address="10.0.0.2")
    c = WiFiNetworkTransport.in_memory(bus, source_address="10.0.0.3")
    await a.start_async()
    await b.start_async()
    await c.start_async()
    brx = b.receive_async()
    crx = c.receive_async()
    # Destination is an IP -> unicast to 10.0.0.2 only.
    await a.send_async(
        NetworkPayload.create(b"just-b", destination_id="10.0.0.2")
    )
    assert (await _next(brx)).data == b"just-b"
    with pytest.raises(asyncio.TimeoutError):
        await _next(crx, timeout=0.1)
    await a.stop_async()
    await b.stop_async()
    await c.stop_async()


async def test_non_ip_destination_falls_back_to_broadcast() -> None:
    bus = InMemoryUdpBus()
    a = WiFiNetworkTransport.in_memory(bus, source_address="10.0.0.1")
    b = WiFiNetworkTransport.in_memory(bus, source_address="10.0.0.2")
    await a.start_async()
    await b.start_async()
    brx = b.receive_async()
    # A non-IP destination id -> broadcast (C#: IPAddress.TryParse fails).
    await a.send_async(
        NetworkPayload.create(b"named", destination_id="some-node-name")
    )
    assert (await _next(brx)).data == b"named"
    await a.stop_async()
    await b.stop_async()


async def test_stop_completes_receive_without_deadlock() -> None:
    bus = InMemoryUdpBus()
    t = WiFiNetworkTransport.in_memory(bus, source_address="10.0.0.5")
    await t.start_async()
    rx = t.receive_async()
    await t.stop_async()
    collected = [item async for item in rx]
    assert collected == []


# ── WiFiPeerDiscovery ────────────────────────────────────────────────────────


async def test_discover_yields_peer_from_beacon() -> None:
    bus = InMemoryUdpBus()
    disc = WiFiPeerDiscovery.in_memory(bus, source_address="10.0.0.9")
    announcer = WiFiPeerDiscovery.in_memory(bus, source_address="10.0.0.8")
    stream = disc.discover_async()
    await announcer.announce_async(
        PeerInfo(
            "node-42", None, (TransportKind.WIFI,), PeerRole.PEER, None, _now()
        )
    )
    peer = await _next(stream)
    assert peer.node_id == "node-42"
    assert peer.display_name == "WiFi/10.0.0.8"
    assert list(peer.supported_transports) == [TransportKind.WIFI]
    assert peer.role is PeerRole.PEER


async def test_discover_ignores_non_beacon_datagrams() -> None:
    bus = InMemoryUdpBus()
    disc = WiFiPeerDiscovery.in_memory(bus, source_address="10.0.0.9")
    # A raw sender that broadcasts noise on the discovery port.
    noise = InMemoryUdpSocket(
        bus,
        source_address="10.0.0.7",
        source_port=WiFiNetworkTransport.DISCOVERY_PORT,
    )
    announcer = WiFiPeerDiscovery.in_memory(bus, source_address="10.0.0.8")
    stream = disc.discover_async()
    await noise.send_async(
        b"NOT-A-BEACON", BROADCAST_ADDRESS, WiFiNetworkTransport.DISCOVERY_PORT
    )
    await announcer.announce_async(
        PeerInfo(
            "real-node", None, (TransportKind.WIFI,), PeerRole.PEER, None, _now()
        )
    )
    # The noise datagram is filtered; the first yielded peer is the real one.
    peer = await _next(stream)
    assert peer.node_id == "real-node"


async def test_announce_rejects_none() -> None:
    bus = InMemoryUdpBus()
    disc = WiFiPeerDiscovery.in_memory(bus)
    with pytest.raises(ValueError):
        await disc.announce_async(None)  # type: ignore[arg-type]
