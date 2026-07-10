# interfaces.py
#
# The transport-abstraction contracts the 10 concrete transports implement.
#
# Ported faithfully from CircleAI.Networking (C# — the spec):
#   INetworkTransport.cs    -> INetworkTransport
#   IMeshNetwork.cs         -> IMeshNetwork
#   IMessageChannel.cs      -> IMessageChannel
#   IConnectivityMonitor.cs -> IConnectivityMonitor
#   ITransportSelector.cs   -> ITransportSelector
#   IPeerDiscovery.cs       -> IPeerDiscovery
#
# C# ``IAsyncEnumerable<T>`` maps to an ``AsyncIterator[T]``-returning method
# (an ``async def`` generator, or any object exposing ``__aiter__``). C#
# ``CancellationToken ct = default`` maps to a keyword-only ``ct`` argument
# defaulting to ``None`` — the in-memory implementations honour it where a real
# transport would observe cancellation.

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import AsyncIterator, List, Optional, Sequence, Type, TypeVar

from .network_types import (
    NetworkContext,
    NetworkPayload,
    PeerInfo,
    TransportKind,
)

T = TypeVar("T")


class INetworkTransport(ABC):
    """Unified send/receive abstraction for a single transport kind.

    Faithful port of the C# ``INetworkTransport`` interface.
    """

    @property
    @abstractmethod
    def kind(self) -> TransportKind:
        """Which :class:`TransportKind` this transport speaks."""
        ...

    @property
    @abstractmethod
    def is_available(self) -> bool:
        """Whether the underlying link is currently usable."""
        ...

    @abstractmethod
    async def start_async(self, *, ct: Optional[object] = None) -> None:
        """Bring the transport up (open sockets, join mesh, etc.)."""
        ...

    @abstractmethod
    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        """Tear the transport down and release resources."""
        ...

    @abstractmethod
    async def send_async(
        self, payload: NetworkPayload, *, ct: Optional[object] = None
    ) -> None:
        """Transmit a single payload over this transport."""
        ...

    @abstractmethod
    def receive_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[NetworkPayload]:
        """Async-iterate over inbound payloads (the C# ``IAsyncEnumerable``)."""
        ...


class IMeshNetwork(ABC):
    """Mesh-specific: topology, node identity, mesh health.

    Faithful port of the C# ``IMeshNetwork`` interface.
    """

    @property
    @abstractmethod
    def local_node_id(self) -> str:
        """Stable identifier of THIS node on the mesh."""
        ...

    @abstractmethod
    async def get_peer_ids_async(
        self, *, ct: Optional[object] = None
    ) -> Sequence[str]:
        """Node IDs of currently-reachable peers."""
        ...

    @abstractmethod
    async def get_mesh_health_async(
        self, *, ct: Optional[object] = None
    ) -> NetworkContext:
        """A :class:`NetworkContext` snapshot describing mesh health."""
        ...


class IMessageChannel(ABC):
    """Typed message delivery over any transport.

    Faithful port of the generic C# ``IMessageChannel`` interface. Python has
    no runtime generic dispatch, so :meth:`receive_async` takes an optional
    ``message_type`` used to deserialise inbound payloads.
    """

    @abstractmethod
    async def send_async(
        self, destination_id: str, message: T, *, ct: Optional[object] = None
    ) -> None:
        """Serialise and send ``message`` to ``destination_id``."""
        ...

    @abstractmethod
    def receive_async(
        self,
        message_type: Optional[Type[T]] = None,
        *,
        ct: Optional[object] = None,
    ) -> AsyncIterator[T]:
        """Async-iterate over inbound messages, deserialised to ``message_type``
        (the C# ``ReceiveAsync<T>``). ``None`` yields the raw deserialised
        object.
        """
        ...


class IConnectivityMonitor(ABC):
    """Observes connectivity state and emits changes.

    Faithful port of the C# ``IConnectivityMonitor`` interface.
    """

    @property
    @abstractmethod
    def current_state(self):  # -> ConnectivityState
        """The current coarse :class:`ConnectivityState`."""
        ...

    @abstractmethod
    def get_snapshot(self) -> NetworkContext:
        """A synchronous point-in-time :class:`NetworkContext`."""
        ...

    @abstractmethod
    def watch_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[NetworkContext]:
        """Async-iterate over connectivity snapshots as they change."""
        ...


class ITransportSelector(ABC):
    """Selects the best transport for a payload+context.

    Default cascade: gRPC -> WebSocket -> HTTP -> MQTT -> TCP ->
    WiFi -> Bluetooth -> NearLink -> Aether -> DTN -> LocalStore.

    Faithful port of the C# ``ITransportSelector`` interface.
    """

    @abstractmethod
    def select_best(
        self, payload: NetworkPayload, context: NetworkContext
    ) -> TransportKind:
        """The single best transport to use right now."""
        ...

    @abstractmethod
    def get_cascade(
        self, payload: NetworkPayload, context: NetworkContext
    ) -> List[TransportKind]:
        """The ordered fallback list, best-first."""
        ...


class IPeerDiscovery(ABC):
    """Finds nearby devices via mDNS, BLE beacons, NearLink scan, Aether
    presence, etc.

    Faithful port of the C# ``IPeerDiscovery`` interface.
    """

    @abstractmethod
    def discover_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[PeerInfo]:
        """Async-iterate over discovered peers as they are found (the C#
        ``IAsyncEnumerable<PeerInfo>``).
        """
        ...

    @abstractmethod
    async def announce_async(
        self, local_info: PeerInfo, *, ct: Optional[object] = None
    ) -> None:
        """Broadcast this device's presence to the local neighbourhood."""
        ...
