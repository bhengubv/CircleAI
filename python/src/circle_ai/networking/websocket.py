# websocket.py
#
# CircleAI.Networking.WebSocket — full-duplex WebSocket network transport module.
#
# Ported faithfully from the C# spec:
#   WebSocketTransportCommons.cs -> WebSocketLinkState, WebSocketMessageType
#       (enums), WebSocketEndpointDescriptor, WebSocketFrameSummary (records),
#       InMemoryWebSocketSessionRegistry
#   WebSocketTransport.cs -> WebSocketTransport (INetworkTransport over a
#       ClientWebSocket), IWebSocketConnection (the injected ClientWebSocket seam)
#
# The real C# transport wraps ``System.Net.WebSockets.ClientWebSocket``. Here the
# connection is injected behind :class:`IWebSocketConnection` (in-memory, no
# sockets). ``send_async`` sends a binary frame; the pump receives frames until a
# Close frame and enqueues each as a :class:`NetworkPayload`. A working,
# deterministic :class:`InMemoryWebSocketConnection` loops sent frames back so the
# transport round-trips without a real socket.

from __future__ import annotations

import asyncio
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import (
    AsyncIterator,
    Deque,
    Dict,
    List,
    Mapping,
    Optional,
    Sequence,
)

from collections import deque

from ._inbound import InboundChannel
from .interfaces import INetworkTransport
from .network_types import NetworkPayload, TransportKind


class WebSocketLinkState(IntEnum):
    """State of a WebSocket link.

    Ordinals match the C# ``enum WebSocketLinkState { Closed, Connecting, Open,
    CloseSent, CloseReceived, Closed_Error }``.
    """

    CLOSED = 0
    CONNECTING = 1
    OPEN = 2
    CLOSE_SENT = 3
    CLOSE_RECEIVED = 4
    CLOSED_ERROR = 5


class WebSocketMessageType(IntEnum):
    """WebSocket message/frame type.

    Ordinals match the C# ``enum WebSocketMessageType { Text, Binary, Ping,
    Pong, Close }``.
    """

    TEXT = 0
    BINARY = 1
    PING = 2
    PONG = 3
    CLOSE = 4


@dataclass(frozen=True, slots=True)
class WebSocketEndpointDescriptor:
    """Describes a WebSocket endpoint. Faithful port of the C# record.

    ``uri`` is the endpoint string; ``headers`` may be ``None`` (the C#
    ``IReadOnlyDictionary<string,string>?``); ``ping_interval`` is seconds (the
    C# ``TimeSpan``); ``subprotocols`` lists negotiated subprotocols.
    """

    uri: str
    headers: Optional[Mapping[str, str]]
    ping_interval: float  # seconds
    subprotocols: Sequence[str]


@dataclass(frozen=True, slots=True)
class WebSocketFrameSummary:
    """A single-frame telemetry row. Faithful port of the C# record."""

    session_id: str
    type: WebSocketMessageType
    bytes: int
    at_utc: datetime


class InMemoryWebSocketSessionRegistry:
    """In-memory registry of WebSocket endpoints, link states, and frame
    telemetry. Faithful port of the C# ``InMemoryWebSocketSessionRegistry``.
    """

    def __init__(self) -> None:
        self._endpoints: Dict[str, WebSocketEndpointDescriptor] = {}
        self._states: Dict[str, WebSocketLinkState] = {}
        self._frames: List[WebSocketFrameSummary] = []
        self._lock = threading.Lock()

    def register(self, session_id: str, d: WebSocketEndpointDescriptor) -> None:
        if d is None:
            raise ValueError("descriptor required")
        with self._lock:
            self._endpoints[session_id] = d

    def get(self, session_id: str) -> Optional[WebSocketEndpointDescriptor]:
        with self._lock:
            return self._endpoints.get(session_id)

    def set_state(self, session_id: str, s: WebSocketLinkState) -> None:
        with self._lock:
            self._states[session_id] = s

    def state(self, session_id: str) -> WebSocketLinkState:
        with self._lock:
            return self._states.get(session_id, WebSocketLinkState.CLOSED)

    def record_frame(self, f: WebSocketFrameSummary) -> None:
        if f is None:
            raise ValueError("frame required")
        with self._lock:
            self._frames.append(f)

    def total_bytes(self, session_id: str) -> int:
        """Sum of frame ``bytes`` for ``session_id``
        (C#: ``Where(...).Sum(f => (long)f.Bytes)``).
        """
        with self._lock:
            return sum(
                f.bytes for f in self._frames if f.session_id == session_id
            )

    def frame_count(
        self, session_id: str, type: WebSocketMessageType
    ) -> int:
        """Count of frames of ``type`` for ``session_id``
        (C#: ``Count(f => f.SessionId == sessionId && f.Type == type)``).
        """
        with self._lock:
            return sum(
                1
                for f in self._frames
                if f.session_id == session_id and f.type == type
            )


@dataclass(frozen=True, slots=True)
class WebSocketReceiveResult:
    """One received frame: its bytes + whether it is a Close frame — the seam
    analogue of ``WebSocketReceiveResult`` (only the fields the pump needs).
    """

    data: bytes
    message_type: WebSocketMessageType


class IWebSocketConnection(ABC):
    """The injected WebSocket connection seam (replaces ``ClientWebSocket``).

    ``connect_async`` opens the link; ``send_async`` sends one binary frame;
    ``receive_async`` returns the next frame (or a Close frame when the link is
    closing / closed); ``close_async`` initiates a normal closure.
    """

    @property
    @abstractmethod
    def state(self) -> WebSocketLinkState:
        """Current link state (the C# ``ClientWebSocket.State``)."""
        ...

    @abstractmethod
    async def connect_async(self, *, ct: Optional[object] = None) -> None:
        """Open the connection (the C# ``ConnectAsync``)."""
        ...

    @abstractmethod
    async def send_async(
        self, data: bytes, *, ct: Optional[object] = None
    ) -> None:
        """Send one binary frame (the C# ``SendAsync(..., Binary, true, ct)``)."""
        ...

    @abstractmethod
    async def receive_async(
        self, *, ct: Optional[object] = None
    ) -> WebSocketReceiveResult:
        """Receive the next frame (the C# ``ReceiveAsync``)."""
        ...

    @abstractmethod
    async def close_async(self, *, ct: Optional[object] = None) -> None:
        """Initiate a normal closure (the C# ``CloseAsync``)."""
        ...

    def dispose(self) -> None:
        """Release the connection (the C# ``Dispose``). Default no-op."""
        ...


class WebSocketTransport(INetworkTransport):
    """Full-duplex `INetworkTransport` backed by an injected WebSocket
    connection. Faithful port of the C# ``WebSocketTransport``.

    ``is_available`` is True only when the link state is
    :attr:`WebSocketLinkState.OPEN`. ``start_async`` connects and starts the
    pump; ``send_async`` sends a binary frame; the pump receives frames until a
    Close frame, enqueuing each as a :class:`NetworkPayload`. ``receive_async``
    streams that inbound channel.
    """

    def __init__(self, connection: IWebSocketConnection) -> None:
        if connection is None:
            raise ValueError("connection required")
        self._conn = connection
        self._inbound: "InboundChannel[NetworkPayload]" = InboundChannel()
        self._pump_task: Optional["asyncio.Task[None]"] = None

    @property
    def kind(self) -> TransportKind:
        return TransportKind.WEB_SOCKET

    @property
    def is_available(self) -> bool:
        return self._conn.state == WebSocketLinkState.OPEN

    async def start_async(self, *, ct: Optional[object] = None) -> None:
        await self._conn.connect_async(ct=ct)
        self._pump_task = asyncio.ensure_future(self._pump_async(ct))

    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        await self._conn.close_async(ct=ct)
        self._inbound.try_complete()
        task = self._pump_task
        if task is not None and not task.done():
            try:
                await asyncio.wait_for(asyncio.shield(task), timeout=1.0)
            except (asyncio.TimeoutError, asyncio.CancelledError):
                pass

    async def send_async(
        self, payload: NetworkPayload, *, ct: Optional[object] = None
    ) -> None:
        if payload is None:
            raise ValueError("payload required")
        await self._conn.send_async(bytes(payload.data), ct=ct)

    def receive_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[NetworkPayload]:
        return self._inbound.read_all()

    async def _pump_async(self, ct: Optional[object]) -> None:
        """Receive frames until a Close frame (or error), then complete the
        inbound channel — faithful to the C# ``PumpAsync``.
        """
        while True:
            try:
                result = await self._conn.receive_async(ct=ct)
                if result.message_type == WebSocketMessageType.CLOSE:
                    break
                self._inbound.write(NetworkPayload.create(bytes(result.data)))
            except Exception:
                break
        self._inbound.try_complete()

    def dispose(self) -> None:
        """Dispose the underlying connection (the C# ``DisposeAsync``)."""
        self._conn.dispose()


class InMemoryWebSocketConnection(IWebSocketConnection):
    """A working, deterministic :class:`IWebSocketConnection`.

    ``send_async`` enqueues each sent frame into an internal inbound queue when
    ``loopback`` is set, so a :class:`WebSocketTransport` over this connection
    round-trips deterministically without a real socket. :meth:`deliver` injects
    an inbound frame from a simulated remote; :meth:`deliver_close` injects a
    Close frame (ending the pump). ``close_async`` transitions to
    :attr:`WebSocketLinkState.CLOSED` and unblocks any pending receive with a
    Close frame.
    """

    def __init__(self, *, loopback: bool = True) -> None:
        self._loopback = loopback
        self._state = WebSocketLinkState.CLOSED
        self._queue: Deque[WebSocketReceiveResult] = deque()
        self._lock = threading.Lock()
        self._available = asyncio.Event()

    @property
    def state(self) -> WebSocketLinkState:
        return self._state

    async def connect_async(self, *, ct: Optional[object] = None) -> None:
        self._state = WebSocketLinkState.OPEN

    async def send_async(
        self, data: bytes, *, ct: Optional[object] = None
    ) -> None:
        if self._state != WebSocketLinkState.OPEN:
            raise RuntimeError("WebSocket is not open")
        if self._loopback:
            self._enqueue(
                WebSocketReceiveResult(bytes(data), WebSocketMessageType.BINARY)
            )

    async def receive_async(
        self, *, ct: Optional[object] = None
    ) -> WebSocketReceiveResult:
        while True:
            with self._lock:
                if self._queue:
                    return self._queue.popleft()
                if self._state in (
                    WebSocketLinkState.CLOSED,
                    WebSocketLinkState.CLOSE_RECEIVED,
                    WebSocketLinkState.CLOSED_ERROR,
                ):
                    return WebSocketReceiveResult(
                        b"", WebSocketMessageType.CLOSE
                    )
                self._available.clear()
            await self._available.wait()

    async def close_async(self, *, ct: Optional[object] = None) -> None:
        self._state = WebSocketLinkState.CLOSED
        self._available.set()

    def _enqueue(self, result: WebSocketReceiveResult) -> None:
        with self._lock:
            self._queue.append(result)
        self._available.set()

    def deliver(self, data: bytes) -> None:
        """Inject an inbound binary frame from a simulated remote peer."""
        self._enqueue(
            WebSocketReceiveResult(bytes(data), WebSocketMessageType.BINARY)
        )

    def deliver_close(self) -> None:
        """Inject a Close frame (ends the transport pump)."""
        self._enqueue(WebSocketReceiveResult(b"", WebSocketMessageType.CLOSE))

    def dispose(self) -> None:
        self._state = WebSocketLinkState.CLOSED
        self._available.set()
