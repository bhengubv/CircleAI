"""test_websocket_transport.py

Verifies the WebSocket transport module: WebSocketLinkState /
WebSocketMessageType ordinals, the endpoint / frame-summary records,
InMemoryWebSocketSessionRegistry (state default, byte totals, per-type frame
counts), the IWebSocketConnection seam + InMemoryWebSocketConnection, and
WebSocketTransport start/stop/send/receive (is_available gated on OPEN, the pump
that stops on a Close frame, and the Wave-1 concurrency guarantees).

Mirrors CircleAI.Networking.WebSocket WebSocketTransportCommons.cs /
WebSocketTransport.cs (C# — the spec).
"""
from __future__ import annotations

import asyncio
import dataclasses
from datetime import datetime, timezone

import pytest

from circle_ai.networking import (
    InMemoryWebSocketConnection,
    InMemoryWebSocketSessionRegistry,
    IWebSocketConnection,
    NetworkPayload,
    TransportKind,
    WebSocketEndpointDescriptor,
    WebSocketFrameSummary,
    WebSocketLinkState,
    WebSocketMessageType,
    WebSocketTransport,
)


def _now() -> datetime:
    return datetime.now(timezone.utc)


async def _next(it, timeout: float = 1.0):
    return await asyncio.wait_for(it.__anext__(), timeout=timeout)


# ── enums ────────────────────────────────────────────────────────────────────


def test_link_state_ordinals_match_csharp() -> None:
    assert int(WebSocketLinkState.CLOSED) == 0
    assert int(WebSocketLinkState.CONNECTING) == 1
    assert int(WebSocketLinkState.OPEN) == 2
    assert int(WebSocketLinkState.CLOSE_SENT) == 3
    assert int(WebSocketLinkState.CLOSE_RECEIVED) == 4
    assert int(WebSocketLinkState.CLOSED_ERROR) == 5


def test_message_type_ordinals_match_csharp() -> None:
    assert int(WebSocketMessageType.TEXT) == 0
    assert int(WebSocketMessageType.BINARY) == 1
    assert int(WebSocketMessageType.PING) == 2
    assert int(WebSocketMessageType.PONG) == 3
    assert int(WebSocketMessageType.CLOSE) == 4


# ── records ──────────────────────────────────────────────────────────────────


def test_endpoint_descriptor_is_frozen() -> None:
    d = WebSocketEndpointDescriptor(
        "wss://x/ws", {"Authorization": "Bearer"}, 20.0, ("chat",)
    )
    assert d.uri == "wss://x/ws"
    assert d.headers["Authorization"] == "Bearer"
    assert list(d.subprotocols) == ["chat"]
    with pytest.raises(dataclasses.FrozenInstanceError):
        d.uri = "x"  # type: ignore[misc]


def test_endpoint_descriptor_allows_none_headers() -> None:
    d = WebSocketEndpointDescriptor("wss://x", None, 0.0, ())
    assert d.headers is None


def test_frame_summary_record() -> None:
    f = WebSocketFrameSummary("s1", WebSocketMessageType.BINARY, 128, _now())
    assert f.bytes == 128
    with pytest.raises(dataclasses.FrozenInstanceError):
        f.bytes = 1  # type: ignore[misc]


# ── InMemoryWebSocketSessionRegistry ─────────────────────────────────────────


def test_registry_register_get_and_state_default() -> None:
    reg = InMemoryWebSocketSessionRegistry()
    d = WebSocketEndpointDescriptor("wss://x", None, 0.0, ())
    reg.register("s1", d)
    assert reg.get("s1") is d
    assert reg.get("missing") is None
    assert reg.state("s1") is WebSocketLinkState.CLOSED  # default
    reg.set_state("s1", WebSocketLinkState.OPEN)
    assert reg.state("s1") is WebSocketLinkState.OPEN


def test_registry_rejects_none() -> None:
    reg = InMemoryWebSocketSessionRegistry()
    with pytest.raises(ValueError):
        reg.register("s1", None)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        reg.record_frame(None)  # type: ignore[arg-type]


def test_registry_total_bytes_and_frame_count() -> None:
    reg = InMemoryWebSocketSessionRegistry()
    reg.record_frame(WebSocketFrameSummary("s1", WebSocketMessageType.BINARY, 10, _now()))
    reg.record_frame(WebSocketFrameSummary("s1", WebSocketMessageType.BINARY, 20, _now()))
    reg.record_frame(WebSocketFrameSummary("s1", WebSocketMessageType.PING, 5, _now()))
    reg.record_frame(WebSocketFrameSummary("s2", WebSocketMessageType.BINARY, 99, _now()))
    assert reg.total_bytes("s1") == 35  # 10 + 20 + 5
    assert reg.frame_count("s1", WebSocketMessageType.BINARY) == 2
    assert reg.frame_count("s1", WebSocketMessageType.PING) == 1
    assert reg.frame_count("s1", WebSocketMessageType.CLOSE) == 0


# ── WebSocketTransport ───────────────────────────────────────────────────────


def test_transport_kind_is_websocket() -> None:
    t = WebSocketTransport(InMemoryWebSocketConnection())
    assert t.kind is TransportKind.WEB_SOCKET


def test_transport_rejects_none_connection() -> None:
    with pytest.raises(ValueError):
        WebSocketTransport(None)  # type: ignore[arg-type]


async def test_is_available_only_when_open() -> None:
    conn = InMemoryWebSocketConnection()
    t = WebSocketTransport(conn)
    assert t.is_available is False  # CLOSED before connect
    await t.start_async()
    assert t.is_available is True  # OPEN after connect
    await t.stop_async()
    assert t.is_available is False  # CLOSED after stop


async def test_send_loops_back_binary_frame() -> None:
    conn = InMemoryWebSocketConnection(loopback=True)
    t = WebSocketTransport(conn)
    await t.start_async()
    rx = t.receive_async()
    await t.send_async(NetworkPayload.create(b"ws-frame"))
    assert (await _next(rx)).data == b"ws-frame"
    await t.stop_async()


async def test_deliver_injects_inbound_frame() -> None:
    conn = InMemoryWebSocketConnection(loopback=False)
    t = WebSocketTransport(conn)
    await t.start_async()
    rx = t.receive_async()
    conn.deliver(b"from-server")
    assert (await _next(rx)).data == b"from-server"
    await t.stop_async()


async def test_close_frame_ends_receive_stream() -> None:
    conn = InMemoryWebSocketConnection(loopback=False)
    t = WebSocketTransport(conn)
    await t.start_async()
    rx = t.receive_async()
    conn.deliver(b"one")
    assert (await _next(rx)).data == b"one"
    # A Close frame stops the pump, which completes the inbound channel.
    conn.deliver_close()
    remaining = [item async for item in rx]
    assert remaining == []


async def test_message_immediately_after_subscribe_not_lost() -> None:
    conn = InMemoryWebSocketConnection(loopback=True)
    t = WebSocketTransport(conn)
    await t.start_async()
    rx = t.receive_async()
    await t.send_async(NetworkPayload.create(b"race"))
    assert (await _next(rx)).data == b"race"
    await t.stop_async()


async def test_unbounded_buffering_retains_predrain_frames() -> None:
    conn = InMemoryWebSocketConnection(loopback=True)
    t = WebSocketTransport(conn)
    await t.start_async()
    rx = t.receive_async()
    for i in range(12):
        await t.send_async(NetworkPayload.create(str(i).encode()))
    received = [(await _next(rx)).data for _ in range(12)]
    assert received == [str(i).encode() for i in range(12)]
    await t.stop_async()


async def test_stop_completes_receive_without_deadlock() -> None:
    conn = InMemoryWebSocketConnection(loopback=True)
    t = WebSocketTransport(conn)
    await t.start_async()
    rx = t.receive_async()
    await t.stop_async()
    collected = [item async for item in rx]
    assert collected == []
