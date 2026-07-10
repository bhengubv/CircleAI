"""test_tcp_transport.py

Verifies the raw-TCP transport module: TcpConnectionState ordinals, the
endpoint / throughput records, TcpKnownPorts constants,
InMemoryTcpConnectionRegistry, the ITcpStream seam + InMemoryTcpStream pair, and
TcpNetworkTransport start/stop/send/receive with the LENGTH-PREFIXED framing
(4-byte little-endian int32 length + data) ported exactly, plus the "Not
connected" send guard and the Wave-1 concurrency guarantees.

Mirrors CircleAI.Networking.Tcp TcpTransportCommons.cs /
TcpNetworkTransport.cs (C# — the spec).
"""
from __future__ import annotations

import asyncio
import dataclasses
import struct
from datetime import datetime, timezone

import pytest

from circle_ai.networking import (
    InMemoryTcpConnectionRegistry,
    InMemoryTcpStream,
    NetworkPayload,
    TcpConnectionState,
    TcpEndpointDescriptor,
    TcpKnownPorts,
    TcpNetworkTransport,
    TcpStreamClosedError,
    TcpThroughputSample,
    TransportKind,
)


def _now() -> datetime:
    return datetime.now(timezone.utc)


async def _next(it, timeout: float = 1.0):
    return await asyncio.wait_for(it.__anext__(), timeout=timeout)


# ── TcpConnectionState ───────────────────────────────────────────────────────


def test_connection_state_ordinals_match_csharp() -> None:
    assert int(TcpConnectionState.DISCONNECTED) == 0
    assert int(TcpConnectionState.CONNECTING) == 1
    assert int(TcpConnectionState.CONNECTED) == 2
    assert int(TcpConnectionState.CLOSING) == 3
    assert int(TcpConnectionState.FAILED) == 4


# ── records + known ports ────────────────────────────────────────────────────


def test_endpoint_descriptor_is_frozen() -> None:
    d = TcpEndpointDescriptor("host", 443, True, True, 5.0)
    assert d.host == "host"
    assert d.no_delay is True
    with pytest.raises(dataclasses.FrozenInstanceError):
        d.port = 1  # type: ignore[misc]


def test_known_ports_match_csharp() -> None:
    assert TcpKnownPorts.HTTP == 80
    assert TcpKnownPorts.HTTPS == 443
    assert TcpKnownPorts.SSH == 22
    assert TcpKnownPorts.SMTP == 25
    assert TcpKnownPorts.IMAP == 143
    assert TcpKnownPorts.IMAP_SSL == 993
    assert TcpKnownPorts.POP3 == 110
    assert TcpKnownPorts.POP3_SSL == 995
    assert TcpKnownPorts.MQTT == 1883
    assert TcpKnownPorts.MQTT_SSL == 8883


# ── InMemoryTcpConnectionRegistry ────────────────────────────────────────────


def test_registry_register_get_state_and_totals() -> None:
    reg = InMemoryTcpConnectionRegistry()
    d = TcpEndpointDescriptor("h", 22, False, False, 1.0)
    reg.register("c1", d)
    assert reg.get("c1") is d
    assert reg.get("missing") is None
    assert reg.state("c1") is TcpConnectionState.DISCONNECTED  # default
    reg.set_state("c1", TcpConnectionState.CONNECTED)
    assert reg.state("c1") is TcpConnectionState.CONNECTED
    reg.record_sample(TcpThroughputSample("c1", 100, 40, _now()))
    reg.record_sample(TcpThroughputSample("c1", 50, 10, _now()))
    reg.record_sample(TcpThroughputSample("c2", 999, 0, _now()))
    assert reg.total_bytes_sent("c1") == 150  # sum(100, 50)


def test_registry_rejects_none() -> None:
    reg = InMemoryTcpConnectionRegistry()
    with pytest.raises(ValueError):
        reg.register("c1", None)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        reg.record_sample(None)  # type: ignore[arg-type]


# ── InMemoryTcpStream framing ────────────────────────────────────────────────


async def test_stream_pair_reads_written_bytes() -> None:
    a, b = InMemoryTcpStream.pair()
    await a.write_async(b"hello")
    assert await b.read_exactly_async(5) == b"hello"


async def test_stream_read_exactly_spans_multiple_writes() -> None:
    a, b = InMemoryTcpStream.pair()
    await a.write_async(b"ab")
    await a.write_async(b"cd")
    assert await b.read_exactly_async(4) == b"abcd"


async def test_stream_close_raises_on_short_read() -> None:
    a, b = InMemoryTcpStream.pair()
    await a.write_async(b"xy")
    b.close()
    with pytest.raises(TcpStreamClosedError):
        await b.read_exactly_async(4)


# ── TcpNetworkTransport ──────────────────────────────────────────────────────


def test_transport_kind_is_tcp() -> None:
    a, _b = InMemoryTcpStream.pair()
    t = TcpNetworkTransport(a)
    assert t.kind is TransportKind.TCP


async def test_send_before_stream_raises_not_connected() -> None:
    # Listener-only shape: no stream present -> send raises "Not connected."
    t = TcpNetworkTransport(stream=None, listen_port=9000)
    await t.start_async()
    assert t.is_available is False
    with pytest.raises(RuntimeError):
        await t.send_async(NetworkPayload.create(b"x"))


async def test_send_writes_length_prefixed_frame() -> None:
    a, b = InMemoryTcpStream.pair()
    t = TcpNetworkTransport(a)
    await t.start_async()
    await t.send_async(NetworkPayload.create(b"hello"))
    # Peer 'b' should see 4-byte little-endian length (5) then the data.
    length_bytes = await b.read_exactly_async(4)
    assert length_bytes == struct.pack("<i", 5)
    assert await b.read_exactly_async(5) == b"hello"
    await t.stop_async()


async def test_client_to_peer_roundtrip_via_pump() -> None:
    a, b = InMemoryTcpStream.pair()
    client = TcpNetworkTransport(a)
    server = TcpNetworkTransport(b)
    await client.start_async()
    await server.start_async()
    srx = server.receive_async()
    await client.send_async(NetworkPayload.create(b"tcp-payload"))
    got = await _next(srx)
    assert got.data == b"tcp-payload"
    await client.stop_async()
    await server.stop_async()


async def test_is_available_reflects_connection() -> None:
    a, b = InMemoryTcpStream.pair()
    t = TcpNetworkTransport(a)
    await t.start_async()
    assert t.is_available is True
    await t.stop_async()
    assert t.is_available is False


async def test_multiple_frames_preserve_order() -> None:
    a, b = InMemoryTcpStream.pair()
    client = TcpNetworkTransport(a)
    server = TcpNetworkTransport(b)
    await client.start_async()
    await server.start_async()
    srx = server.receive_async()
    for i in range(10):
        await client.send_async(NetworkPayload.create(str(i).encode()))
    received = [(await _next(srx)).data for _ in range(10)]
    assert received == [str(i).encode() for i in range(10)]
    await client.stop_async()
    await server.stop_async()


async def test_stop_completes_receive_without_deadlock() -> None:
    a, b = InMemoryTcpStream.pair()
    server = TcpNetworkTransport(b)
    await server.start_async()
    srx = server.receive_async()
    await server.stop_async()
    collected = [item async for item in srx]
    assert collected == []


async def test_empty_payload_roundtrips() -> None:
    a, b = InMemoryTcpStream.pair()
    client = TcpNetworkTransport(a)
    server = TcpNetworkTransport(b)
    await client.start_async()
    await server.start_async()
    srx = server.receive_async()
    await client.send_async(NetworkPayload.create(b""))
    got = await _next(srx)
    assert got.data == b""
    await client.stop_async()
    await server.stop_async()
