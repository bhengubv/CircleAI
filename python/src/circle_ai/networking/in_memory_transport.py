# in_memory_transport.py
#
# Working, deterministic in-memory realisation of the transport abstraction.
# A real socket / mesh radio is the injected dependency behind INetworkTransport;
# here that dependency is an InMemoryWire that routes NetworkPayloads between
# transports attached to the same wire (loopback "mesh").
#
# This is the transport ABSTRACTION the 10 concrete transports implement — none
# of it touches a real network. Implements:
#   • InMemoryWire            — the routed loopback backplane (the socket seam)
#   • InMemoryNetworkTransport — INetworkTransport over a wire
#   • InMemoryMeshNetwork      — IMeshNetwork over a wire
#
# Concurrency rules honoured (bit Wave 1):
#   • Fan-out delivery snapshots the subscriber set, RELEASES the lock, THEN
#     enqueues — a subscriber's teardown handler can take the same lock without
#     self-deadlock.
#   • A receiver's queue is registered SYNCHRONOUSLY by receive_async() before
#     any await, so a payload sent immediately after start cannot race the
#     subscription and be lost.
#   • Per-subscriber queues are UNBOUNDED, so send never blocks and messages
#     buffered before a consumer drains them are retained.

from __future__ import annotations

import asyncio
import threading
from typing import AsyncIterator, Dict, List, Optional, Sequence, Set

from .interfaces import IMeshNetwork, INetworkTransport
from .network_types import (
    ConnectivityState,
    NetworkContext,
    NetworkPayload,
    TransportKind,
)

# Sentinel pushed onto receiver queues to end iteration on stop/dispose.
_CLOSED = object()


class InMemoryWire:
    """In-memory routed backplane shared by transports of one simulated mesh.

    Routing rule (per attached transport, by that transport's ``node_id``):
      • ``payload.destination_id is None`` or ``""`` -> broadcast to every OTHER
        attached transport.
      • otherwise -> delivered only to the transport whose ``node_id`` matches
        ``destination_id`` (if attached). The sender never receives its own
        payload (loopback echo is suppressed, matching InProcessSyncHub).
    """

    def __init__(self) -> None:
        self._transports: Dict[str, "InMemoryNetworkTransport"] = {}
        self._lock = threading.Lock()

    def _attach(self, transport: "InMemoryNetworkTransport") -> None:
        with self._lock:
            self._transports[transport.node_id] = transport

    def _detach(self, node_id: str) -> None:
        with self._lock:
            self._transports.pop(node_id, None)

    @property
    def attached_node_ids(self) -> List[str]:
        with self._lock:
            return list(self._transports.keys())

    def peer_ids_of(self, node_id: str) -> List[str]:
        """Node IDs of every attached transport other than ``node_id``."""
        with self._lock:
            return [n for n in self._transports if n != node_id]

    def _route(self, payload: NetworkPayload, sender_node_id: str) -> None:
        dest = payload.destination_id
        # Snapshot targets under the lock, then release before delivering.
        with self._lock:
            if dest is None or dest == "":
                targets = [
                    t for nid, t in self._transports.items() if nid != sender_node_id
                ]
            else:
                t = self._transports.get(dest)
                targets = [t] if (t is not None and dest != sender_node_id) else []
        for target in targets:
            target._enqueue(payload)


class InMemoryNetworkTransport(INetworkTransport):
    """`INetworkTransport` backed by an :class:`InMemoryWire`.

    Deterministic and lock-safe. ``start_async`` attaches to the wire and marks
    the transport available; ``stop_async`` detaches, ends all live receivers,
    and marks it unavailable. ``send_async`` routes through the wire.
    """

    def __init__(
        self,
        wire: InMemoryWire,
        node_id: str,
        kind: TransportKind = TransportKind.AETHER,
    ) -> None:
        if wire is None:
            raise ValueError("wire required")
        if node_id is None or node_id.strip() == "":
            raise ValueError("node_id required")
        self._wire = wire
        self._node_id = node_id
        self._kind = kind
        self._available = False
        self._lock = threading.Lock()
        # Live receiver queues (one per active receive_async iterator).
        self._receivers: Set["asyncio.Queue[object]"] = set()

    @property
    def node_id(self) -> str:
        return self._node_id

    @property
    def kind(self) -> TransportKind:
        return self._kind

    @property
    def is_available(self) -> bool:
        return self._available

    async def start_async(self, *, ct: Optional[object] = None) -> None:
        if self._available:
            return
        self._available = True
        self._wire._attach(self)

    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        if not self._available:
            return
        self._available = False
        self._wire._detach(self._node_id)
        # End every live receiver: snapshot, release, then signal.
        with self._lock:
            receivers = list(self._receivers)
            self._receivers.clear()
        for q in receivers:
            q.put_nowait(_CLOSED)

    async def send_async(
        self, payload: NetworkPayload, *, ct: Optional[object] = None
    ) -> None:
        if payload is None:
            raise ValueError("payload required")
        if not self._available:
            raise RuntimeError(
                f"transport {self._node_id!r} is not started"
            )
        # Stamp the source if the payload did not carry one (record is
        # immutable, so build a replacement rather than mutating).
        if payload.source_id is None:
            payload = NetworkPayload(
                id=payload.id,
                source_id=self._node_id,
                destination_id=payload.destination_id,
                data=payload.data,
                priority=payload.priority,
                ttl=payload.ttl,
                content_type=payload.content_type,
                metadata=payload.metadata,
                created_at=payload.created_at,
            )
        self._wire._route(payload, self._node_id)

    def receive_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[NetworkPayload]:
        # Register the queue SYNCHRONOUSLY (before returning the generator and
        # before any await) so a payload sent right after start_async cannot
        # race this subscription. Unbounded queue -> senders never block and
        # early payloads are buffered until drained.
        q: "asyncio.Queue[object]" = asyncio.Queue()
        with self._lock:
            self._receivers.add(q)

        async def _iter() -> AsyncIterator[NetworkPayload]:
            try:
                while True:
                    item = await q.get()
                    if item is _CLOSED:
                        return
                    yield item  # type: ignore[misc]
            finally:
                # Deregister on any exit (break / cancel / stop). Safe to take
                # the lock: delivery already released it before enqueuing.
                with self._lock:
                    self._receivers.discard(q)

        return _iter()

    def _enqueue(self, payload: NetworkPayload) -> None:
        """Fan a wire-delivered payload out to every live receiver.

        Snapshot the receiver set under the lock, release it, then enqueue —
        never hold the lock across the ``put`` (an unbounded put never blocks,
        but the rule keeps the finally-block deregister deadlock-free).
        """
        with self._lock:
            receivers = list(self._receivers)
        for q in receivers:
            q.put_nowait(payload)


class InMemoryMeshNetwork(IMeshNetwork):
    """`IMeshNetwork` view over an :class:`InMemoryWire`.

    Reports the local node id, the live peer set from the wire, and a
    :class:`NetworkContext` health snapshot whose state reflects peer presence.
    """

    def __init__(
        self,
        wire: InMemoryWire,
        local_node_id: str,
        preferred_transport: TransportKind = TransportKind.AETHER,
    ) -> None:
        if wire is None:
            raise ValueError("wire required")
        if local_node_id is None or local_node_id.strip() == "":
            raise ValueError("local_node_id required")
        self._wire = wire
        self._local_node_id = local_node_id
        self._preferred = preferred_transport

    @property
    def local_node_id(self) -> str:
        return self._local_node_id

    async def get_peer_ids_async(
        self, *, ct: Optional[object] = None
    ) -> Sequence[str]:
        return self._wire.peer_ids_of(self._local_node_id)

    async def get_mesh_health_async(
        self, *, ct: Optional[object] = None
    ) -> NetworkContext:
        from datetime import datetime, timezone

        peers = self._wire.peer_ids_of(self._local_node_id)
        state = (
            ConnectivityState.MESH_ONLY if peers else ConnectivityState.OFFLINE
        )
        return NetworkContext(
            state=state,
            preferred_transport=self._preferred,
            available_transports=(self._preferred,) if peers else (),
            signal_strength_dbm=None,
            estimated_bandwidth_bps=None,
            latency_ms=None,
            nearby_peer_count=len(peers),
            snapshot_at=datetime.now(timezone.utc),
        )
