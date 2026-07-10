# companion_state_sync_engine.py
#
# ICompanionStateSyncEngine + the default CompanionStateSyncEngine.
#
# Orchestration loop. Subscribes to the channel, responds to envelopes,
# and exposes write_local_async + sync_now_async for the host.
#
# Protocol — convergent in <= 2 round-trips per peer pair:
#   1. sync_now_async  → broadcast Announce(localStateVector)
#   2. Peer receives Announce → diff against own vector → reply Request(missing)
#   3. We receive Request → gather entries via store.get_since_async → Push
#   4. Peer receives Push → apply_async for each entry
#   5. Peer broadcasts Announce again if anything applied — converges.
#
# All entries are content-hashed (SHA-256 of payload) at write time so the
# tiebreaker for equal-Version conflicts is deterministic everywhere.
#
# Ported faithfully from CircleAI.Memory.Sync.ICompanionStateSyncEngine and
# CircleAI.Memory.Sync.CompanionStateSyncEngine (C# — the spec).

from __future__ import annotations

import hashlib
from abc import ABC, abstractmethod
from datetime import datetime, timezone
from typing import Callable, Dict, List, Optional

from .companion_state_channel import (
    ICompanionStateChannel,
    IDisposable,
)
from .hybrid_logical_clock import HybridLogicalClock
from .sync_envelope import (
    RequestItem,
    SyncEnvelope,
    SyncEnvelopeKind,
)
from .syncable_entry import SyncableEntry
from .syncable_entry_store import ISyncableEntryStore


class ICompanionStateSyncEngine(ABC):
    """Engine that broadcasts local state vectors, fulfils peer Requests, and
    applies inbound Push entries. Hosts call :meth:`start_async` once at
    startup, then either rely on event-driven sync (handlers respond as
    envelopes arrive) or trigger :meth:`sync_now_async` after notable local
    writes to immediately propagate.

    Implements the async-disposable contract (:meth:`dispose_async`).
    """

    @abstractmethod
    async def start_async(self, *, ct: Optional[object] = None) -> None:
        """Subscribe the engine to channel envelopes."""
        ...

    @abstractmethod
    async def sync_now_async(self, *, ct: Optional[object] = None) -> None:
        """Broadcast the local state vector to all peers immediately."""
        ...

    @abstractmethod
    async def write_local_async(
        self,
        entity_type: str,
        entity_id: str,
        payload: str,
        *,
        is_tombstone: bool = False,
        ct: Optional[object] = None,
    ) -> SyncableEntry:
        """Apply a locally-authored entry: stamp it with a fresh HLC version,
        persist it to the local store, and (if started) broadcast it via Push.
        Returns the resulting entry with its assigned version.
        """
        ...

    @abstractmethod
    async def dispose_async(self) -> None:
        """Release the channel subscription."""
        ...


class CompanionStateSyncEngine(ICompanionStateSyncEngine):
    """Default :class:`ICompanionStateSyncEngine`."""

    def __init__(
        self,
        channel: ICompanionStateChannel,
        store: ISyncableEntryStore,
        clock: HybridLogicalClock,
        wall_clock: Optional[Callable[[], datetime]] = None,
    ) -> None:
        if channel is None:
            raise ValueError("channel required")
        if store is None:
            raise ValueError("store required")
        if clock is None:
            raise ValueError("clock required")
        self._channel = channel
        self._store = store
        self._clock = clock
        self._wall_clock = wall_clock or (lambda: datetime.now(timezone.utc))
        self._subscription: Optional[IDisposable] = None
        self._disposed = False

    async def start_async(self, *, ct: Optional[object] = None) -> None:
        self._throw_if_disposed()
        if self._subscription is None:
            self._subscription = self._channel.subscribe(self._handle_envelope_async)

    async def sync_now_async(self, *, ct: Optional[object] = None) -> None:
        self._throw_if_disposed()
        vector = await self._store.get_state_vector_async(ct=ct)
        await self._channel.send_async(
            SyncEnvelope(
                kind=SyncEnvelopeKind.ANNOUNCE,
                from_node_id=self._channel.local_node_id,
                state_vector=vector,
                requests=None,
                entries=None,
            ),
            ct=ct,
        )

    async def write_local_async(
        self,
        entity_type: str,
        entity_id: str,
        payload: str,
        *,
        is_tombstone: bool = False,
        ct: Optional[object] = None,
    ) -> SyncableEntry:
        self._throw_if_disposed()
        if entity_type is None or entity_type.strip() == "":
            raise ValueError("entity_type required")
        if entity_id is None or entity_id.strip() == "":
            raise ValueError("entity_id required")

        payload = payload if payload is not None else ""
        entry = SyncableEntry(
            entity_type=entity_type,
            entity_id=entity_id,
            version=self._clock.tick(),
            is_tombstone=is_tombstone,
            content_hash=self._compute_hash(payload),
            payload=payload,
            source_node_id=self._channel.local_node_id,
            authored_at=self._wall_clock(),
        )

        await self._store.apply_async(entry, ct=ct)

        if self._subscription is not None:
            await self._channel.send_async(
                SyncEnvelope(
                    kind=SyncEnvelopeKind.PUSH,
                    from_node_id=self._channel.local_node_id,
                    state_vector=None,
                    requests=None,
                    entries=[entry],
                ),
                ct=ct,
            )
        return entry

    # ── Inbound envelope handling ────────────────────────────────────────

    async def _handle_envelope_async(
        self, envelope: SyncEnvelope, ct: Optional[object]
    ) -> None:
        if envelope.kind == SyncEnvelopeKind.ANNOUNCE:
            await self._handle_announce_async(envelope, ct)
        elif envelope.kind == SyncEnvelopeKind.REQUEST:
            await self._handle_request_async(envelope, ct)
        elif envelope.kind == SyncEnvelopeKind.PUSH:
            await self._handle_push_async(envelope, ct)

    async def _handle_announce_async(
        self, envelope: SyncEnvelope, ct: Optional[object]
    ) -> None:
        if envelope.state_vector is None:
            return
        local = await self._store.get_state_vector_async(ct=ct)
        local_map: Dict[str, int] = {
            v.entity_type: v.max_known_version for v in local
        }

        requests: List[RequestItem] = []
        for peer in envelope.state_vector:
            our_max = local_map.get(peer.entity_type, 0)
            if peer.max_known_version > our_max:
                requests.append(
                    RequestItem(entity_type=peer.entity_type, since_version=our_max)
                )
        if len(requests) == 0:
            return

        await self._channel.send_async(
            SyncEnvelope(
                kind=SyncEnvelopeKind.REQUEST,
                from_node_id=self._channel.local_node_id,
                state_vector=None,
                requests=requests,
                entries=None,
            ),
            ct=ct,
        )

    async def _handle_request_async(
        self, envelope: SyncEnvelope, ct: Optional[object]
    ) -> None:
        if envelope.requests is None or len(envelope.requests) == 0:
            return
        collected: List[SyncableEntry] = []
        for req in envelope.requests:
            newer = await self._store.get_since_async(
                req.entity_type, req.since_version, ct=ct
            )
            collected.extend(newer)
        if len(collected) == 0:
            return

        await self._channel.send_async(
            SyncEnvelope(
                kind=SyncEnvelopeKind.PUSH,
                from_node_id=self._channel.local_node_id,
                state_vector=None,
                requests=None,
                entries=collected,
            ),
            ct=ct,
        )

    async def _handle_push_async(
        self, envelope: SyncEnvelope, ct: Optional[object]
    ) -> None:
        if envelope.entries is None:
            return
        any_applied = False
        for e in envelope.entries:
            self._clock.observe(e.version)
            applied = await self._store.apply_async(e, ct=ct)
            any_applied = any_applied or applied
        # If anything applied, re-announce so other peers can converge too.
        if any_applied:
            await self.sync_now_async(ct=ct)

    # ── async disposable ─────────────────────────────────────────────────

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        if self._subscription is not None:
            self._subscription.dispose()
        self._subscription = None

    async def __aenter__(self) -> "CompanionStateSyncEngine":
        return self

    async def __aexit__(self, *exc_info: object) -> None:
        await self.dispose_async()

    def _throw_if_disposed(self) -> None:
        if self._disposed:
            raise RuntimeError("CompanionStateSyncEngine is disposed")

    # ── Helpers ──────────────────────────────────────────────────────────

    @staticmethod
    def _compute_hash(payload: str) -> str:
        return hashlib.sha256(payload.encode("utf-8")).hexdigest()
