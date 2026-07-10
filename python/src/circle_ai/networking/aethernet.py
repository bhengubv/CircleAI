# aethernet.py
#
# CircleAI.Networking.AetherNet — the Aether-mesh network transport module.
#
# Ported faithfully from the C# spec:
#   AetherNetTransportCommons.cs -> AetherPeerKind (enum), AetherPeer,
#       AetherHopTelemetry, AetherPacketSummary (records),
#       InMemoryAetherNetRegistry
#   AetherNetworkTransport.cs    -> AetherNetworkTransport (INetworkTransport
#       backed by the Aether mesh protocol engine)
#   AetherPeerDiscovery.cs       -> AetherPeerDiscovery (IPeerDiscovery over
#       Aether presence beacons)
#   AetherSyncChannel.cs         -> AetherSyncChannel (ISyncChannel over Aether
#       DTN store-and-forward)
#
# The C# classes take an IAetherContext (presence/availability of the Aether
# runtime) and delegate the wire work to the external aether-protocol engine
# (RoutingService + SignalCipher + DTN). To keep this port working and
# deterministic WITHOUT stubbing, the routing/presence/DTN seam is modelled as
# IAetherMeshEngine and given an in-memory implementation (a routed loopback
# backplane, like InMemoryWire). Availability is still gated by IAetherContext,
# exactly as C#.
#
# Concurrency (Wave-1 rules): the engine's inbound / discovery / DTN hubs buffer
# unbounded, subscribe synchronously before any await, and snapshot+release the
# lock before enqueuing (no lost messages, no teardown self-deadlock).

from __future__ import annotations

import statistics
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import AsyncIterator, Dict, List, Optional, Sequence

from ..aether.context import IAetherContext
from ..sync.sync_channel import ISyncChannel
from ..sync.sync_types import SyncDelta
from .interfaces import INetworkTransport, IPeerDiscovery
from .network_types import NetworkPayload, PeerInfo, TransportKind
from ._inbound import InboundChannel


class AetherPeerKind(IntEnum):
    """Kind of device advertised on the Aether mesh.

    Ordinals match the C# ``enum AetherPeerKind { Phone, Tablet, Laptop,
    Desktop, Edge, Vehicle, Iot }``.
    """

    PHONE = 0
    TABLET = 1
    LAPTOP = 2
    DESKTOP = 3
    EDGE = 4
    VEHICLE = 5
    IOT = 6


@dataclass(frozen=True, slots=True)
class AetherPeer:
    """A peer on the Aether mesh. Faithful port of the C# ``AetherPeer`` record.

    ``friendly_name`` may be ``None`` (the C# ``string?``).
    """

    peer_id: str
    kind: AetherPeerKind
    friendly_name: Optional[str]
    advertised_capabilities: Sequence[str]


@dataclass(frozen=True, slots=True)
class AetherHopTelemetry:
    """Per-hop round-trip telemetry. Faithful port of the C# record."""

    peer_id: str
    hop_count: int
    round_trip_ms: float
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class AetherPacketSummary:
    """A per-packet summary row. Faithful port of the C# record."""

    packet_id: str
    from_peer: str
    to_peer: str
    bytes: int
    packet_kind: str
    at_utc: datetime


class InMemoryAetherNetRegistry:
    """In-memory registry of Aether peers, hop telemetry, and packet summaries.
    Faithful port of the C# ``InMemoryAetherNetRegistry``.
    """

    def __init__(self) -> None:
        self._peers: Dict[str, AetherPeer] = {}
        self._telemetry: List[AetherHopTelemetry] = []
        self._packets: List[AetherPacketSummary] = []
        self._lock = threading.Lock()

    def register(self, p: AetherPeer) -> None:
        if p is None:
            raise ValueError("peer required")
        with self._lock:
            self._peers[p.peer_id] = p

    def get_peer(self, id: str) -> Optional[AetherPeer]:
        with self._lock:
            return self._peers.get(id)

    @property
    def peers(self) -> Sequence[AetherPeer]:
        """Peers ordered by peer id (C#: ``OrderBy(p => p.PeerId)``)."""
        with self._lock:
            return sorted(self._peers.values(), key=lambda p: p.peer_id)

    def record_hop(self, t: AetherHopTelemetry) -> None:
        if t is None:
            raise ValueError("hop telemetry required")
        with self._lock:
            self._telemetry.append(t)

    def record_packet(self, p: AetherPacketSummary) -> None:
        if p is None:
            raise ValueError("packet summary required")
        with self._lock:
            self._packets.append(p)

    def recent_packets(self, limit: int = 100) -> Sequence[AetherPacketSummary]:
        """Most-recent packets first, capped at ``limit``
        (C#: ``OrderByDescending(AtUtc).Take(limit)``).
        """
        with self._lock:
            ordered = sorted(
                self._packets, key=lambda p: p.at_utc, reverse=True
            )
        return ordered[:limit]

    def avg_round_trip_ms(self, peer_id: str) -> float:
        """Mean round-trip (ms) for ``peer_id``; 0.0 when no telemetry
        (C#: ``DefaultIfEmpty(0).Average()``).
        """
        with self._lock:
            rtts = [
                t.round_trip_ms
                for t in self._telemetry
                if t.peer_id == peer_id
            ]
        return statistics.fmean(rtts) if rtts else 0.0

    def total_bytes_between(self, from_peer: str, to_peer: str) -> int:
        """Sum of bytes for packets ``from_peer`` -> ``to_peer``
        (C#: ``Where(...).Sum(p => p.Bytes)``).
        """
        with self._lock:
            return sum(
                p.bytes
                for p in self._packets
                if p.from_peer == from_peer and p.to_peer == to_peer
            )


class IAetherMeshEngine(ABC):
    """The Aether-protocol routing/presence/DTN seam.

    In C# the AetherNet transports delegate the wire work to the external
    aether-protocol engine (RoutingService + SignalCipher + DTN store-and-forward
    + presence beacons). This interface is that seam; the in-memory
    implementation (:class:`InMemoryAetherMeshEngine`) does real deterministic
    routing so the transports are fully working without a stub.
    """

    @abstractmethod
    async def route_async(
        self, payload: NetworkPayload, *, ct: Optional[object] = None
    ) -> None:
        """Route a payload across the mesh (AODV; SOS flood for emergency)."""
        ...

    @abstractmethod
    def inbound(self) -> "InboundChannel[NetworkPayload]":
        """The inbound payload channel this node reads received frames from."""
        ...

    @abstractmethod
    def discovered_peers(self) -> Sequence[PeerInfo]:
        """Peers currently known via Aether presence beacons."""
        ...

    @abstractmethod
    async def announce_async(
        self, local_info: PeerInfo, *, ct: Optional[object] = None
    ) -> None:
        """Broadcast an Aether presence beacon (Hello)."""
        ...

    @abstractmethod
    def discovery_stream(self) -> "InboundChannel[PeerInfo]":
        """The stream of newly-discovered peers (NodeJoined events)."""
        ...

    @abstractmethod
    async def push_bundle_async(
        self, delta: SyncDelta, *, ct: Optional[object] = None
    ) -> None:
        """Hand a delta to the DTN engine as a custody-transfer bundle."""
        ...

    @abstractmethod
    def delivery_stream(self, owner_id: str) -> "InboundChannel[SyncDelta]":
        """The DTN delivery queue filtered by ``owner_id``."""
        ...


class AetherNetworkTransport(INetworkTransport):
    """`INetworkTransport` backed by the Aether mesh protocol engine.

    Faithful port of the C# ``AetherNetworkTransport``. Uses BLE + WiFi Direct +
    NearLink + NFC + LoRa + HTTP Relay as physical transports; Signal Protocol
    (X3DH + Double Ratchet) provides E2E encryption; AODV routing + DTN 72h
    store-and-forward for offline delivery; SOS flood for emergency messages.

    Availability is gated by the injected :class:`IAetherContext`
    (``is_available``). The routing itself is delegated to the injected
    :class:`IAetherMeshEngine`.
    """

    def __init__(
        self, context: IAetherContext, engine: "IAetherMeshEngine"
    ) -> None:
        if context is None:
            raise ValueError("context required")
        if engine is None:
            raise ValueError("engine required")
        self._context = context
        self._engine = engine

    @property
    def kind(self) -> TransportKind:
        return TransportKind.AETHER

    @property
    def is_available(self) -> bool:
        return self._context.is_available

    async def start_async(self, *, ct: Optional[object] = None) -> None:
        # Aether runtime is managed out-of-band; nothing to open here (C#:
        # Task.CompletedTask).
        return None

    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        # End the inbound receive loop (C#: _inbound.Writer.TryComplete()).
        self._engine.inbound().try_complete()

    async def send_async(
        self, payload: NetworkPayload, *, ct: Optional[object] = None
    ) -> None:
        if payload is None:
            raise ValueError("payload required")
        # Routing (incl. SOS flood for emergency priority) handled by the engine.
        await self._engine.route_async(payload, ct=ct)

    def receive_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[NetworkPayload]:
        return self._engine.inbound().read_all()


class AetherPeerDiscovery(IPeerDiscovery):
    """`IPeerDiscovery` using Aether presence beacons (Hello/HelloAck). No
    infrastructure — discovery works over BLE/WiFi Direct/NearLink.

    Faithful port of the C# ``AetherPeerDiscovery``; presence work is delegated
    to the injected :class:`IAetherMeshEngine`.
    """

    def __init__(
        self, context: IAetherContext, engine: "IAetherMeshEngine"
    ) -> None:
        if context is None:
            raise ValueError("context required")
        if engine is None:
            raise ValueError("engine required")
        self._context = context
        self._engine = engine

    def discover_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[PeerInfo]:
        return self._engine.discovery_stream().read_all()

    async def announce_async(
        self, local_info: PeerInfo, *, ct: Optional[object] = None
    ) -> None:
        if local_info is None:
            raise ValueError("local_info required")
        await self._engine.announce_async(local_info, ct=ct)


class AetherSyncChannel(ISyncChannel):
    """`ISyncChannel` backed by Aether DTN store-and-forward.

    Faithful port of the C# ``AetherSyncChannel``. Memory deltas are delivered
    even when source and destination devices are never simultaneously online — a
    DTN bundle relays through intermediate nodes. TTL = 72 hours by default
    (matches aether-protocol DTN spec). Last-sequence bookkeeping is tracked
    locally exactly as C#; the bundle work is delegated to the engine.
    """

    def __init__(
        self, context: IAetherContext, engine: "IAetherMeshEngine"
    ) -> None:
        if context is None:
            raise ValueError("context required")
        if engine is None:
            raise ValueError("engine required")
        self._context = context
        self._engine = engine
        self._sequences: Dict[tuple, int] = {}
        self._lock = threading.Lock()

    async def push_delta_async(
        self, delta: SyncDelta, *, ct: Optional[object] = None
    ) -> None:
        if delta is None:
            raise ValueError("delta required")
        # Serialise + hand to the DTN engine for custody-transfer delivery.
        await self._engine.push_bundle_async(delta, ct=ct)
        with self._lock:
            key = (delta.owner_id, delta.domain_key)
            if delta.sequence > self._sequences.get(key, 0):
                self._sequences[key] = delta.sequence

    def receive_deltas_async(
        self, owner_id: str, *, ct: Optional[object] = None
    ) -> AsyncIterator[SyncDelta]:
        return self._engine.delivery_stream(owner_id).read_all()

    async def get_last_sequence_async(
        self, owner_id: str, domain_key: str, *, ct: Optional[object] = None
    ) -> int:
        with self._lock:
            return self._sequences.get((owner_id, domain_key), 0)


class InMemoryAetherMeshEngine(IAetherMeshEngine):
    """A working, deterministic :class:`IAetherMeshEngine`.

    Models a routed loopback Aether mesh in memory (no sockets, no Signal
    crypto): a single node's inbound / discovery / per-owner DTN queues, plus a
    :meth:`link` seam to wire engines into a multi-node mesh so a payload routed
    from one is delivered to its neighbours. Every seam is real and
    deterministic — nothing is stubbed.
    """

    def __init__(self, node_id: str) -> None:
        if node_id is None or node_id.strip() == "":
            raise ValueError("node_id required")
        self._node_id = node_id
        self._inbound: "InboundChannel[NetworkPayload]" = InboundChannel()
        self._discovery: "InboundChannel[PeerInfo]" = InboundChannel()
        self._delivery: Dict[str, "InboundChannel[SyncDelta]"] = {}
        self._peers: Dict[str, PeerInfo] = {}
        self._links: List["InMemoryAetherMeshEngine"] = []
        self._lock = threading.Lock()

    @property
    def node_id(self) -> str:
        return self._node_id

    def link(self, other: "InMemoryAetherMeshEngine") -> None:
        """Wire ``other`` in as a directly-reachable mesh neighbour (routing +
        DTN delivery + presence flow to it). Bidirectional.
        """
        if other is None or other is self:
            raise ValueError("a distinct engine is required")
        with self._lock:
            if other not in self._links:
                self._links.append(other)
        other._add_reverse_link(self)

    def _add_reverse_link(self, other: "InMemoryAetherMeshEngine") -> None:
        with self._lock:
            if other not in self._links:
                self._links.append(other)

    def _delivery_channel(self, owner_id: str) -> "InboundChannel[SyncDelta]":
        with self._lock:
            ch = self._delivery.get(owner_id)
            if ch is None:
                ch = InboundChannel()
                self._delivery[owner_id] = ch
            return ch

    # ── IAetherMeshEngine ────────────────────────────────────────────────────

    async def route_async(
        self, payload: NetworkPayload, *, ct: Optional[object] = None
    ) -> None:
        dest = payload.destination_id
        with self._lock:
            links = list(self._links)
        for peer in links:
            # Directed -> only the matching node; broadcast/empty -> all links.
            if not dest or dest == peer.node_id:
                peer.inbound().write(payload)

    def inbound(self) -> "InboundChannel[NetworkPayload]":
        return self._inbound

    def discovered_peers(self) -> Sequence[PeerInfo]:
        with self._lock:
            return list(self._peers.values())

    async def announce_async(
        self, local_info: PeerInfo, *, ct: Optional[object] = None
    ) -> None:
        # A Hello beacon: every linked neighbour learns of this peer.
        with self._lock:
            links = list(self._links)
        for peer in links:
            peer._receive_beacon(local_info)

    def _receive_beacon(self, info: PeerInfo) -> None:
        with self._lock:
            self._peers[info.node_id] = info
        self._discovery.write(info)

    def discovery_stream(self) -> "InboundChannel[PeerInfo]":
        return self._discovery

    async def push_bundle_async(
        self, delta: SyncDelta, *, ct: Optional[object] = None
    ) -> None:
        # DTN custody transfer: deliver to linked neighbours' owner queues.
        with self._lock:
            links = list(self._links)
        target = delta.target_device_id
        for peer in links:
            if not target or target == peer.node_id:
                peer._deliver_bundle(delta)

    def _deliver_bundle(self, delta: SyncDelta) -> None:
        self._delivery_channel(delta.owner_id).write(delta)

    def delivery_stream(self, owner_id: str) -> "InboundChannel[SyncDelta]":
        return self._delivery_channel(owner_id)
