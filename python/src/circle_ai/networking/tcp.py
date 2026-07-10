# tcp.py
#
# CircleAI.Networking.Tcp — raw-TCP network transport module.
#
# Ported faithfully from the C# spec:
#   TcpTransportCommons.cs -> TcpConnectionState (enum), TcpEndpointDescriptor,
#       TcpThroughputSample (records), TcpKnownPorts, InMemoryTcpConnectionRegistry
#   TcpNetworkTransport.cs -> TcpNetworkTransport (INetworkTransport over raw
#       TCP), ITcpStream (the injected NetworkStream seam)
#
# The real C# transport uses TcpClient / TcpListener / NetworkStream. Here the
# byte stream is injected behind :class:`ITcpStream` (in-memory, no sockets); the
# LENGTH-PREFIXED framing is ported EXACTLY:
#   send   -> write a 4-byte LITTLE-ENDIAN int32 length, then the data
#   receive-> ReadExactly(4) -> little-endian length -> ReadExactly(length)
# matching BitConverter.GetBytes(int) / BitConverter.ToInt32 on the little-endian
# runtime. A working, deterministic :class:`InMemoryTcpStream` pairs two streams
# so a client transport round-trips to a peer without a real socket.

from __future__ import annotations

import asyncio
import struct
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from collections import deque
from typing import AsyncIterator, Deque, Dict, List, Optional

from ._inbound import InboundChannel
from .interfaces import INetworkTransport
from .network_types import NetworkPayload, TransportKind


class TcpConnectionState(IntEnum):
    """State of a TCP connection.

    Ordinals match the C# ``enum TcpConnectionState { Disconnected, Connecting,
    Connected, Closing, Failed }``.
    """

    DISCONNECTED = 0
    CONNECTING = 1
    CONNECTED = 2
    CLOSING = 3
    FAILED = 4


@dataclass(frozen=True, slots=True)
class TcpEndpointDescriptor:
    """Describes a TCP endpoint. Faithful port of the C# record.

    ``connect_timeout`` is seconds (the C# ``TimeSpan``).
    """

    host: str
    port: int
    no_delay: bool
    keep_alive: bool
    connect_timeout: float  # seconds


@dataclass(frozen=True, slots=True)
class TcpThroughputSample:
    """A bytes-sent/received measurement for one endpoint. Faithful port of the
    C# record.
    """

    endpoint_id: str
    bytes_sent: int
    bytes_received: int
    at_utc: datetime


class TcpKnownPorts:
    """Well-known TCP port constants. Faithful port of the C# static
    ``TcpKnownPorts``.
    """

    HTTP = 80
    HTTPS = 443
    SSH = 22
    SMTP = 25
    IMAP = 143
    IMAP_SSL = 993
    POP3 = 110
    POP3_SSL = 995
    MQTT = 1883
    MQTT_SSL = 8883


class InMemoryTcpConnectionRegistry:
    """In-memory registry of TCP endpoints, connection states, and throughput.
    Faithful port of the C# ``InMemoryTcpConnectionRegistry``.
    """

    def __init__(self) -> None:
        self._endpoints: Dict[str, TcpEndpointDescriptor] = {}
        self._states: Dict[str, TcpConnectionState] = {}
        self._throughput: List[TcpThroughputSample] = []
        self._lock = threading.Lock()

    def register(self, id: str, d: TcpEndpointDescriptor) -> None:
        if d is None:
            raise ValueError("descriptor required")
        with self._lock:
            self._endpoints[id] = d

    def get(self, id: str) -> Optional[TcpEndpointDescriptor]:
        with self._lock:
            return self._endpoints.get(id)

    def set_state(self, id: str, s: TcpConnectionState) -> None:
        with self._lock:
            self._states[id] = s

    def state(self, id: str) -> TcpConnectionState:
        with self._lock:
            return self._states.get(id, TcpConnectionState.DISCONNECTED)

    def record_sample(self, s: TcpThroughputSample) -> None:
        if s is None:
            raise ValueError("throughput sample required")
        with self._lock:
            self._throughput.append(s)

    def total_bytes_sent(self, id: str) -> int:
        """Sum of ``bytes_sent`` over samples for ``id``
        (C#: ``Where(...).Sum(t => t.BytesSent)``).
        """
        with self._lock:
            return sum(
                t.bytes_sent for t in self._throughput if t.endpoint_id == id
            )


class TcpStreamClosedError(RuntimeError):
    """Raised by :meth:`ITcpStream.read_exactly` when the stream is closed
    before the requested bytes arrive — the analogue of .NET's
    ``EndOfStreamException`` from ``ReadExactlyAsync``. The transport's pump
    loop catches this (like the C# ``catch { break; }``) and completes the
    inbound channel.
    """


class ITcpStream(ABC):
    """The injected byte-stream seam (replaces ``NetworkStream``).

    Frames are length-prefixed by the transport; this seam only moves raw bytes.
    Implementations provide ordered, reliable delivery to a paired peer.
    """

    @abstractmethod
    async def write_async(
        self, data: bytes, *, ct: Optional[object] = None
    ) -> None:
        """Write ``data`` to the stream (the C# ``NetworkStream.WriteAsync``)."""
        ...

    @abstractmethod
    async def read_exactly_async(
        self, count: int, *, ct: Optional[object] = None
    ) -> bytes:
        """Read EXACTLY ``count`` bytes, or raise :class:`TcpStreamClosedError`
        if the stream closes first (the C# ``ReadExactlyAsync``).
        """
        ...

    @property
    @abstractmethod
    def is_connected(self) -> bool:
        """Whether the underlying connection is open."""
        ...

    @abstractmethod
    def close(self) -> None:
        """Close the stream (the C# ``NetworkStream.Close`` / ``TcpClient.Close``)."""
        ...


class TcpNetworkTransport(INetworkTransport):
    """`INetworkTransport` over raw TCP. Faithful port of the C#
    ``TcpNetworkTransport``.

    Acts as a client when an :class:`ITcpStream` is injected (or produced by an
    injected connector). Frames are length-prefixed: :meth:`send_async` writes a
    4-byte little-endian int32 length then the data; the pump loop reads the
    length then exactly that many bytes and enqueues a :class:`NetworkPayload`.
    ``receive_async`` streams the resulting inbound channel.

    The C# constructor also supports a listen-only mode (``listenPort`` set,
    ``remoteEndpoint`` null) which starts a ``TcpListener`` but wires no stream;
    in that mode ``is_available`` is False and ``send_async`` raises until a
    stream is present. This port mirrors that: pass ``stream=None`` with
    ``listen_port`` set for the listener-only shape.
    """

    def __init__(
        self,
        stream: Optional[ITcpStream] = None,
        *,
        listen_port: Optional[int] = None,
    ) -> None:
        self._stream = stream
        self._listen_port = listen_port
        self._listening = False
        self._inbound: "InboundChannel[NetworkPayload]" = InboundChannel()
        self._pump_task: Optional["asyncio.Task[None]"] = None

    @property
    def kind(self) -> TransportKind:
        return TransportKind.TCP

    @property
    def is_available(self) -> bool:
        # C#: `_client?.Connected ?? false` — only a live client stream counts.
        return self._stream is not None and self._stream.is_connected

    async def start_async(self, *, ct: Optional[object] = None) -> None:
        if self._stream is not None:
            # Client mode: subscribe (the InboundChannel registers on write)
            # and start the pump. The pump reads from an already-open stream.
            self._pump_task = asyncio.ensure_future(self._pump_async(ct))
        elif self._listen_port is not None:
            # Listener-only mode: no stream yet (the C# starts a TcpListener but
            # does not accept in StartAsync). Mark listening; nothing to pump.
            self._listening = True

    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        if self._stream is not None:
            self._stream.close()
        self._listening = False
        self._inbound.try_complete()
        # Let a running pump observe the closed stream and finish.
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
        if self._stream is None:
            raise RuntimeError("Not connected.")
        data = payload.data
        # 4-byte little-endian int32 length prefix (BitConverter.GetBytes(int)).
        length = struct.pack("<i", len(data))
        await self._stream.write_async(length, ct=ct)
        await self._stream.write_async(bytes(data), ct=ct)

    def receive_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[NetworkPayload]:
        return self._inbound.read_all()

    async def _pump_async(self, ct: Optional[object]) -> None:
        """Read length-prefixed frames until the stream closes, then complete
        the inbound channel — faithful to the C# ``PumpAsync``.
        """
        stream = self._stream
        while stream is not None:
            try:
                len_buf = await stream.read_exactly_async(4, ct=ct)
                (length,) = struct.unpack("<i", len_buf)
                data = await stream.read_exactly_async(length, ct=ct)
                self._inbound.write(NetworkPayload.create(data))
            except Exception:
                break
        self._inbound.try_complete()


class InMemoryTcpStream(ITcpStream):
    """A working, deterministic :class:`ITcpStream`.

    Bytes written to this stream become readable on its :attr:`peer` stream (and
    vice-versa) — an in-memory reliable pipe. :meth:`pair` builds a connected
    two-stream pair. Reads block (async) until enough bytes are buffered or the
    stream is closed, matching ``ReadExactlyAsync`` / ``EndOfStreamException``.
    """

    def __init__(self) -> None:
        self._buffer: Deque[int] = deque()
        self._peer: Optional["InMemoryTcpStream"] = None
        self._connected = True
        self._lock = threading.Lock()
        self._data_available = asyncio.Event()

    @staticmethod
    def pair() -> "tuple[InMemoryTcpStream, InMemoryTcpStream]":
        """Return a connected (a, b) pair: a's writes are readable on b."""
        a = InMemoryTcpStream()
        b = InMemoryTcpStream()
        a._peer = b
        b._peer = a
        return a, b

    @property
    def peer(self) -> Optional["InMemoryTcpStream"]:
        return self._peer

    @property
    def is_connected(self) -> bool:
        return self._connected

    def _feed(self, data: bytes) -> None:
        """Append inbound bytes (called by the peer's write). Wakes any reader."""
        with self._lock:
            self._buffer.extend(data)
        self._data_available.set()

    async def write_async(
        self, data: bytes, *, ct: Optional[object] = None
    ) -> None:
        if not self._connected:
            raise TcpStreamClosedError("stream is closed")
        peer = self._peer
        if peer is None:
            raise RuntimeError("stream has no peer")
        peer._feed(bytes(data))

    async def read_exactly_async(
        self, count: int, *, ct: Optional[object] = None
    ) -> bytes:
        if count == 0:
            return b""
        out = bytearray()
        while len(out) < count:
            with self._lock:
                while self._buffer and len(out) < count:
                    out.append(self._buffer.popleft())
                buffer_empty = not self._buffer
                if buffer_empty:
                    self._data_available.clear()
            if len(out) >= count:
                break
            if not self._connected and buffer_empty:
                raise TcpStreamClosedError(
                    f"stream closed with {len(out)}/{count} bytes read"
                )
            await self._data_available.wait()
        return bytes(out)

    def close(self) -> None:
        self._connected = False
        # Wake a blocked reader so it can observe the close and raise.
        self._data_available.set()
