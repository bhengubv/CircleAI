# wifi.py
#
# CircleAI.Networking.WiFi — LAN UDP broadcast/unicast network transport +
# peer-discovery module. No Aether, no cloud, no infrastructure required.
#
# Ported faithfully from the C# spec:
#   WiFiNetworkTransport.cs -> WiFiNetworkTransport (INetworkTransport over LAN
#       UDP), DiscoveryPort/DataPort constants, IUdpSocket (the injected
#       UdpClient seam)
#   WiFiPeerDiscovery.cs -> WiFiPeerDiscovery (IPeerDiscovery via UDP beacons),
#       the CIRCLEAI:BEACON: beacon protocol
#
# The real C# transport uses ``System.Net.Sockets.UdpClient``. Here the datagram
# socket is injected behind :class:`IUdpSocket` bound to a shared
# :class:`InMemoryUdpBus` (in-memory, no sockets); a broadcast fans out to every
# bound socket on the port, a unicast reaches only the socket bound to that IP.
# Send routing (unicast when the destination parses as an IP, else broadcast) and
# the beacon wire format are ported EXACTLY.

from __future__ import annotations

import asyncio
import ipaddress
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import AsyncIterator, Dict, Optional, Set, Tuple

from ._inbound import InboundChannel
from .interfaces import INetworkTransport, IPeerDiscovery
from .network_types import NetworkPayload, PeerInfo, PeerRole, TransportKind

#: The IPv4 limited-broadcast address (``IPAddress.Broadcast``).
BROADCAST_ADDRESS = "255.255.255.255"

#: Sentinel enqueued on close to end a blocked :meth:`InMemoryUdpSocket.receive_async`.
_CLOSED_SENTINEL = object()


@dataclass(frozen=True, slots=True)
class UdpReceiveResult:
    """One received datagram: its bytes + the sender's ``(address, port)`` — the
    seam analogue of ``System.Net.Sockets.UdpReceiveResult``.
    """

    buffer: bytes
    remote_address: str
    remote_port: int


def _is_ip_address(value: str) -> bool:
    """Whether ``value`` parses as an IP address (the C# ``IPAddress.TryParse``)."""
    try:
        ipaddress.ip_address(value)
        return True
    except ValueError:
        return False


class InMemoryUdpBus:
    """In-memory UDP backplane shared by all sockets on one simulated LAN.

    A datagram sent to :data:`BROADCAST_ADDRESS` is delivered to every socket
    bound to the destination port (except, optionally, the sender); a datagram
    sent to a concrete IP reaches only the socket bound to that IP on that port.
    This is the seam a real UDP stack replaces.
    """

    def __init__(self) -> None:
        # (address, port) -> socket ; and port -> set of sockets (for broadcast)
        self._by_endpoint: Dict[Tuple[str, int], "InMemoryUdpSocket"] = {}
        self._by_port: Dict[int, Set["InMemoryUdpSocket"]] = {}
        self._lock = threading.Lock()

    def _bind(self, sock: "InMemoryUdpSocket", address: str, port: int) -> None:
        with self._lock:
            self._by_endpoint[(address, port)] = sock
            self._by_port.setdefault(port, set()).add(sock)

    def _unbind(self, sock: "InMemoryUdpSocket") -> None:
        with self._lock:
            if sock._bound_endpoint is not None:
                self._by_endpoint.pop(sock._bound_endpoint, None)
                port = sock._bound_endpoint[1]
                peers = self._by_port.get(port)
                if peers is not None:
                    peers.discard(sock)
                    if not peers:
                        self._by_port.pop(port, None)

    def _deliver(
        self,
        data: bytes,
        dest_address: str,
        dest_port: int,
        sender: "InMemoryUdpSocket",
    ) -> None:
        """Route ``data`` to the destination. Snapshot targets under the lock,
        release, then enqueue on each (the fan-out no-lock-across-put rule).
        """
        sender_addr = (
            sender._bound_endpoint[0]
            if sender._bound_endpoint is not None
            else sender._source_address
        )
        with self._lock:
            if dest_address == BROADCAST_ADDRESS:
                targets = list(self._by_port.get(dest_port, ()))
            else:
                t = self._by_endpoint.get((dest_address, dest_port))
                targets = [t] if t is not None else []
        for target in targets:
            target._feed(
                UdpReceiveResult(bytes(data), sender_addr, sender._source_port)
            )


class IUdpSocket(ABC):
    """The injected datagram-socket seam (replaces ``UdpClient``).

    A socket may be bound to a receive port (to accept datagrams) and can send
    to an ``(address, port)`` destination.
    """

    @abstractmethod
    async def send_async(
        self,
        data: bytes,
        address: str,
        port: int,
        *,
        ct: Optional[object] = None,
    ) -> None:
        """Send one datagram to ``(address, port)`` (the C# ``SendAsync``)."""
        ...

    @abstractmethod
    async def receive_async(
        self, *, ct: Optional[object] = None
    ) -> UdpReceiveResult:
        """Receive the next datagram (the C# ``ReceiveAsync``)."""
        ...

    @abstractmethod
    def close(self) -> None:
        """Close the socket (the C# ``UdpClient.Close``)."""
        ...


class InMemoryUdpSocket(IUdpSocket):
    """A working, deterministic :class:`IUdpSocket` bound to an
    :class:`InMemoryUdpBus`.

    Construct with ``bind_port`` to receive datagrams sent to
    ``(bind_address, bind_port)`` or broadcast on that port. ``source_address``/
    ``source_port`` identify this socket as the datagram sender.
    """

    def __init__(
        self,
        bus: InMemoryUdpBus,
        *,
        bind_address: str = "0.0.0.0",
        bind_port: Optional[int] = None,
        source_address: str = "127.0.0.1",
        source_port: int = 0,
    ) -> None:
        if bus is None:
            raise ValueError("bus required")
        self._bus = bus
        self._source_address = source_address
        self._source_port = source_port
        self._bound_endpoint: Optional[Tuple[str, int]] = None
        self._closed = False
        self._queue: "asyncio.Queue[object]" = asyncio.Queue()
        if bind_port is not None:
            self._bound_endpoint = (bind_address, bind_port)
            self._bus._bind(self, bind_address, bind_port)

    def _feed(self, result: UdpReceiveResult) -> None:
        if not self._closed:
            self._queue.put_nowait(result)

    async def send_async(
        self,
        data: bytes,
        address: str,
        port: int,
        *,
        ct: Optional[object] = None,
    ) -> None:
        if self._closed:
            raise RuntimeError("socket is closed")
        self._bus._deliver(bytes(data), address, port, self)

    async def receive_async(
        self, *, ct: Optional[object] = None
    ) -> UdpReceiveResult:
        result = await self._queue.get()
        if result is _CLOSED_SENTINEL:
            raise RuntimeError("socket is closed")
        return result  # type: ignore[return-value]

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        self._bus._unbind(self)
        self._queue.put_nowait(_CLOSED_SENTINEL)


class WiFiNetworkTransport(INetworkTransport):
    """`INetworkTransport` using LAN UDP broadcast / unicast. Faithful port of
    the C# ``WiFiNetworkTransport``.

    ``send_async`` unicasts to ``(destination_id, DATA_PORT)`` when
    ``destination_id`` parses as an IP address, otherwise broadcasts to
    ``(255.255.255.255, DATA_PORT)`` — exactly as C#. The receive pump reads
    datagrams from the injected receiver socket and enqueues each as a
    :class:`NetworkPayload`. ``is_available`` is True once started (the C#
    ``_receiver is not null``).

    The sender/receiver sockets are injected (the C# ``UdpClient``s); pass an
    :class:`InMemoryUdpSocket` pair bound to a shared :class:`InMemoryUdpBus`, or
    let :meth:`in_memory` build them.
    """

    #: UDP port used for discovery beacons (the C# ``DiscoveryPort``).
    DISCOVERY_PORT = 47890
    #: UDP port used for data datagrams (the C# ``DataPort``).
    DATA_PORT = 47891

    def __init__(
        self,
        sender: IUdpSocket,
        receiver: IUdpSocket,
    ) -> None:
        if sender is None:
            raise ValueError("sender required")
        if receiver is None:
            raise ValueError("receiver required")
        self._sender = sender
        self._receiver = receiver
        self._started = False
        self._inbound: "InboundChannel[NetworkPayload]" = InboundChannel()
        self._pump_task: "Optional[object]" = None

    @staticmethod
    def in_memory(
        bus: InMemoryUdpBus, *, source_address: str = "127.0.0.1"
    ) -> "WiFiNetworkTransport":
        """Build a transport whose sender + receiver are :class:`InMemoryUdpSocket`
        instances on ``bus``. The receiver binds ``(source_address, DATA_PORT)``
        so unicast to ``source_address`` and broadcast both reach it.
        """
        sender = InMemoryUdpSocket(
            bus, source_address=source_address, source_port=WiFiNetworkTransport.DATA_PORT
        )
        receiver = InMemoryUdpSocket(
            bus,
            bind_address=source_address,
            bind_port=WiFiNetworkTransport.DATA_PORT,
            source_address=source_address,
        )
        return WiFiNetworkTransport(sender, receiver)

    @property
    def kind(self) -> TransportKind:
        return TransportKind.WIFI

    @property
    def is_available(self) -> bool:
        return self._started

    async def start_async(self, *, ct: Optional[object] = None) -> None:
        self._started = True
        self._pump_task = asyncio.ensure_future(self._pump_async(ct))

    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        self._started = False
        self._receiver.close()
        self._sender.close()
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
        data = bytes(payload.data)
        dest = payload.destination_id
        if dest and _is_ip_address(dest):
            await self._sender.send_async(data, dest, self.DATA_PORT, ct=ct)
        else:
            await self._sender.send_async(
                data, BROADCAST_ADDRESS, self.DATA_PORT, ct=ct
            )

    def receive_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[NetworkPayload]:
        return self._inbound.read_all()

    async def _pump_async(self, ct: Optional[object]) -> None:
        while self._started:
            try:
                result = await self._receiver.receive_async(ct=ct)
            except Exception:
                break
            self._inbound.write(NetworkPayload.create(result.buffer))
        self._inbound.try_complete()


class WiFiPeerDiscovery(IPeerDiscovery):
    """Discovers nearby Circle AI devices on the same LAN via UDP broadcast
    beacons. Faithful port of the C# ``WiFiPeerDiscovery``.

    A beacon is the UTF-8 string ``CIRCLEAI:BEACON:{nodeId}``. :meth:`discover_async`
    listens on :attr:`WiFiNetworkTransport.DISCOVERY_PORT`, filters datagrams
    starting with the magic prefix, and yields a :class:`PeerInfo` for each
    (node id = the suffix, display name = ``WiFi/{senderAddress}``, role Peer).
    :meth:`announce_async` broadcasts this device's beacon.

    The listen/announce sockets are injected; pass :class:`InMemoryUdpSocket`
    instances on a shared :class:`InMemoryUdpBus`, or use :meth:`in_memory`.
    """

    #: Beacon prefix (the C# ``BeaconMagic``).
    BEACON_MAGIC = "CIRCLEAI:BEACON:"

    def __init__(
        self,
        listen_socket: IUdpSocket,
        announce_socket: IUdpSocket,
    ) -> None:
        if listen_socket is None:
            raise ValueError("listen_socket required")
        if announce_socket is None:
            raise ValueError("announce_socket required")
        self._listen = listen_socket
        self._announce = announce_socket

    @staticmethod
    def in_memory(
        bus: InMemoryUdpBus, *, source_address: str = "127.0.0.1"
    ) -> "WiFiPeerDiscovery":
        """Build a discovery whose listen socket binds the discovery port and
        whose announce socket sends from ``source_address``.
        """
        listen = InMemoryUdpSocket(
            bus,
            bind_address=source_address,
            bind_port=WiFiNetworkTransport.DISCOVERY_PORT,
            source_address=source_address,
        )
        announce = InMemoryUdpSocket(
            bus,
            source_address=source_address,
            source_port=WiFiNetworkTransport.DISCOVERY_PORT,
        )
        return WiFiPeerDiscovery(listen, announce)

    async def discover_async(  # type: ignore[override]
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[PeerInfo]:
        while True:
            try:
                result = await self._listen.receive_async(ct=ct)
            except Exception:
                return
            msg = result.buffer.decode("utf-8", errors="replace")
            if msg.startswith(self.BEACON_MAGIC):
                node_id = msg[len(self.BEACON_MAGIC):]
                yield PeerInfo(
                    node_id=node_id,
                    display_name=f"WiFi/{result.remote_address}",
                    supported_transports=(TransportKind.WIFI,),
                    role=PeerRole.PEER,
                    signal_strength_dbm=None,
                    last_seen=datetime.now(timezone.utc),
                )

    async def announce_async(
        self, local_info: PeerInfo, *, ct: Optional[object] = None
    ) -> None:
        if local_info is None:
            raise ValueError("local_info required")
        beacon = f"{self.BEACON_MAGIC}{local_info.node_id}".encode("utf-8")
        await self._announce.send_async(
            beacon,
            BROADCAST_ADDRESS,
            WiFiNetworkTransport.DISCOVERY_PORT,
            ct=ct,
        )
