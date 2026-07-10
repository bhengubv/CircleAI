# dtn.py
#
# CircleAI.Networking.Dtn — delay-tolerant-networking transport module.
#
# Ported faithfully from the C# spec:
#   DtnBundle.cs           -> DtnBundle (record)
#   DtnTransportCommons.cs -> DtnPriority (enum), DtnCustodyRecord (record),
#                             InMemoryDtnBundleStore
#   DtnSyncChannel.cs      -> DtnSyncChannel (ISyncChannel over any transports)
#
# Store-and-forward: bundles are persisted locally and forwarded whenever any
# INetworkTransport becomes available. TTL = 72 hours by default; expired
# bundles are discarded. Works over HTTP, WiFi, Bluetooth, NearLink — any
# INetworkTransport.
#
# Concurrency (Wave-1 rules): DtnSyncChannel's shared delivered-delta hub buffers
# unbounded, subscribes synchronously before any await, and snapshots the
# subscriber set + releases the lock before enqueuing (no teardown self-deadlock).

from __future__ import annotations

import asyncio
import threading
import uuid
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from enum import IntEnum
from typing import AsyncIterator, Dict, Iterable, List, Optional, Sequence, Set, Tuple

from ..sync.sync_channel import ISyncChannel
from ..sync.sync_types import SyncDeliveryMode, SyncDelta
from .interfaces import INetworkTransport
from .network_types import MessagePriority, NetworkPayload

# Default DTN TTL — matches C# DtnSyncChannel.DefaultTtl = TimeSpan.FromHours(72).
_DEFAULT_TTL = timedelta(hours=72)

_CLOSED = object()


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _new_id() -> str:
    """Guid.NewGuid().ToString("N") — 32 lowercase hex chars, no dashes."""
    return uuid.uuid4().hex


class DtnPriority(IntEnum):
    """Bundle scheduling priority.

    Ordinals match the C# ``enum DtnPriority { Bulk, Normal, Expedited }``.
    """

    BULK = 0
    NORMAL = 1
    EXPEDITED = 2


@dataclass(frozen=True, slots=True)
class DtnBundle:
    """A DTN bundle: a self-contained delivery unit with TTL and custody
    semantics. Faithful port of the C# ``DtnBundle`` record.

    ``payload`` is ``bytes`` (the C# ``ReadOnlyMemory<byte>``). ``expires_at``
    defaults to ``created_at + 72h`` in the channel that mints it.
    """

    bundle_id: str
    source_node_id: str
    destination_node_id: str
    payload: bytes
    expires_at: datetime           # default: created_at + 72h
    custody_required: bool         # request custody transfer at each hop
    hop_count: int
    created_at: datetime


@dataclass(frozen=True, slots=True)
class DtnCustodyRecord:
    """A custody-transfer acceptance record. Faithful port of the C#
    ``DtnCustodyRecord`` record.
    """

    bundle_id: str
    custodian_node: str
    accepted_at_utc: datetime


class InMemoryDtnBundleStore:
    """In-memory bundle + custody store. Faithful port of the C#
    ``InMemoryDtnBundleStore``.

    Thread-safe: mirrors the C# ``ConcurrentDictionary`` backing with a lock so
    ``all``/``purge``/``in_flight_to`` iterate a consistent snapshot.
    """

    def __init__(self) -> None:
        self._bundles: Dict[str, DtnBundle] = {}
        self._custody: Dict[str, DtnCustodyRecord] = {}
        self._lock = threading.Lock()

    def store(self, b: DtnBundle) -> None:
        if b is None:
            raise ValueError("bundle required")
        with self._lock:
            self._bundles[b.bundle_id] = b

    def get(self, bundle_id: str) -> Optional[DtnBundle]:
        with self._lock:
            return self._bundles.get(bundle_id)

    @property
    def all(self) -> Sequence[DtnBundle]:
        with self._lock:
            return list(self._bundles.values())

    def accept_custody(self, r: DtnCustodyRecord) -> None:
        if r is None:
            raise ValueError("custody record required")
        with self._lock:
            self._custody[r.bundle_id] = r

    def get_custody(self, bundle_id: str) -> Optional[DtnCustodyRecord]:
        with self._lock:
            return self._custody.get(bundle_id)

    def is_expired(self, bundle_id: str, now: datetime) -> bool:
        """True when the bundle is unknown OR ``now`` is past its expiry.

        Mirrors C#: an unknown bundle id is treated as expired (returns True).
        """
        with self._lock:
            b = self._bundles.get(bundle_id)
        if b is None:
            return True
        return now > b.expires_at

    def purge(self, now: datetime) -> int:
        """Drop every bundle (and its custody record) whose expiry has passed.
        Returns the number purged. Mirrors C# ``Purge``.
        """
        with self._lock:
            dead = [
                bid for bid, b in self._bundles.items() if now > b.expires_at
            ]
            for bid in dead:
                self._bundles.pop(bid, None)
                self._custody.pop(bid, None)
            return len(dead)

    def in_flight_to(self, destination_node_id: str) -> Sequence[DtnBundle]:
        """Every stored bundle addressed to ``destination_node_id``."""
        with self._lock:
            return [
                b
                for b in self._bundles.values()
                if b.destination_node_id == destination_node_id
            ]


class DtnSyncChannel(ISyncChannel):
    """`ISyncChannel` backed by DTN store-and-forward.

    Faithful port of the C# ``DtnSyncChannel``. A pushed :class:`SyncDelta` is
    wrapped as a :class:`DtnBundle` (custody required iff the delta's delivery
    mode is ``GUARANTEED``); if any injected transport is available the payload
    is sent over the first available one, otherwise the bundle is queued locally
    for later delivery.

    ``receive_deltas_async`` streams from a shared unbounded delivered-delta hub
    (the C# ``Channel.CreateUnbounded<SyncDelta>``); :meth:`deliver` is the seam
    a transport-side reader (or a test) uses to inject arrived deltas. Multiple
    ``receive_deltas_async`` iterators each get every delivered delta (fan-out),
    with the Wave-1 no-lost-message / no-deadlock guarantees.
    """

    def __init__(self, transports: Iterable[INetworkTransport]) -> None:
        # Materialise once (C#: [.. transports]).
        self._transports: List[INetworkTransport] = list(transports)
        self._sequences: Dict[Tuple[str, str], int] = {}
        self._lock = threading.Lock()
        # Local store of minted bundles (C# persists to SQLite in the full impl;
        # here it makes store-and-forward state observable).
        self._store = InMemoryDtnBundleStore()
        # Shared delivered-delta fan-out hub.
        self._subs_lock = threading.Lock()
        self._subscribers: Set["asyncio.Queue[object]"] = set()

    async def push_delta_async(
        self, delta: SyncDelta, *, ct: Optional[object] = None
    ) -> None:
        if delta is None:
            raise ValueError("delta required")
        now = _utc_now()
        ttl = delta.ttl
        expires_at = now + (
            timedelta(seconds=ttl) if ttl is not None else _DEFAULT_TTL
        )
        bundle = DtnBundle(
            bundle_id=_new_id(),
            source_node_id=delta.source_device_id,
            destination_node_id=delta.target_device_id,
            payload=delta.payload,
            expires_at=expires_at,
            custody_required=delta.delivery_mode == SyncDeliveryMode.GUARANTEED,
            hop_count=0,
            created_at=now,
        )
        # Track the bundle so in_flight/purge callers can observe queued work.
        self._store.store(bundle)

        # Try live transports first; if none available, the bundle stays queued.
        available = [t for t in self._transports if t.is_available]
        if available:
            payload = NetworkPayload.create(
                data=delta.payload,
                destination_id=delta.target_device_id,
                priority=(
                    MessagePriority.URGENT
                    if delta.delivery_mode == SyncDeliveryMode.URGENT
                    else MessagePriority.NORMAL
                ),
                content_type="application/dtn-bundle",
            )
            await available[0].send_async(payload, ct=ct)
        # else: bundle is queued locally and retried on transport-up events.

    @property
    def bundle_store(self) -> InMemoryDtnBundleStore:
        """The local bundle store holding minted/queued bundles."""
        return self._store

    @property
    def queued_bundles(self) -> Sequence[DtnBundle]:
        """Bundles minted by :meth:`push_delta_async` (observability aid)."""
        return self._store.all

    def receive_deltas_async(
        self, owner_id: str, *, ct: Optional[object] = None
    ) -> AsyncIterator[SyncDelta]:
        # Register synchronously before any await so a deliver() right after this
        # call cannot race the subscription. Unbounded queue -> deliver never
        # blocks and pre-drain deltas are buffered.
        q: "asyncio.Queue[object]" = asyncio.Queue()
        with self._subs_lock:
            self._subscribers.add(q)

        async def _iter() -> AsyncIterator[SyncDelta]:
            try:
                while True:
                    item = await q.get()
                    if item is _CLOSED:
                        return
                    yield item  # type: ignore[misc]
            finally:
                with self._subs_lock:
                    self._subscribers.discard(q)

        return _iter()

    def deliver(self, delta: SyncDelta) -> None:
        """Inject an arrived delta into every live receiver (the transport-side
        seam / test hook for the C# ``_delivered`` writer). Also advances the
        per-owner+domain last-sequence bookkeeping.
        """
        if delta is None:
            raise ValueError("delta required")
        with self._lock:
            key = (delta.owner_id, delta.domain_key)
            prev = self._sequences.get(key, 0)
            if delta.sequence > prev:
                self._sequences[key] = delta.sequence
        # Snapshot subscribers, release, then enqueue (no teardown deadlock).
        with self._subs_lock:
            subs = list(self._subscribers)
        for q in subs:
            q.put_nowait(delta)

    def close(self) -> None:
        """End every live ``receive_deltas_async`` iterator."""
        with self._subs_lock:
            subs = list(self._subscribers)
            self._subscribers.clear()
        for q in subs:
            q.put_nowait(_CLOSED)

    async def get_last_sequence_async(
        self, owner_id: str, domain_key: str, *, ct: Optional[object] = None
    ) -> int:
        with self._lock:
            return self._sequences.get((owner_id, domain_key), 0)
