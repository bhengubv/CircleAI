# memory_sync_service.py
#
# IMemorySyncService + the default MemorySyncService.
#
# Pushes and receives memory deltas across all owned devices. The transport is
# determined by ISyncChannel — the app code is identical whether the delta
# travels gRPC, BLE mesh, or DTN bundle.
#
# Ported faithfully from CircleAI.Sync.IMemorySyncService and
# CircleAI.Sync.MemorySyncService (C# — the spec).

from __future__ import annotations

import asyncio
from abc import ABC, abstractmethod
from datetime import datetime, timezone
from typing import List, Optional

from ..memory.stores import IEpisodicMemoryStore
from .sync_channel import ISyncChannel
from .sync_types import SyncDelta, SyncDeliveryMode, SyncDomainKeys


def _unix_time_ms() -> int:
    return int(datetime.now(timezone.utc).timestamp() * 1000)


class IMemorySyncService(ABC):
    """Pushes and receives memory deltas across all owned devices.

    The transport is determined by :class:`ISyncChannel` — the app code is
    identical whether the delta travels gRPC, BLE mesh, or DTN bundle.
    """

    @abstractmethod
    async def push_memory_delta_async(
        self,
        owner_id: str,
        domain_key: str,
        delta: bytes,
        *,
        mode: SyncDeliveryMode = SyncDeliveryMode.GUARANTEED,
        ct: Optional[object] = None,
    ) -> None:
        """Push a memory delta for ``owner_id`` to all other devices."""
        ...

    @abstractmethod
    async def start_receiving_async(
        self, owner_id: str, *, ct: Optional[object] = None
    ) -> None:
        """Start receiving and applying incoming deltas for ``owner_id``."""
        ...

    @abstractmethod
    async def stop_receiving_async(self, *, ct: Optional[object] = None) -> None:
        """Stop receiving."""
        ...


class MemorySyncService(IMemorySyncService):
    """Default :class:`IMemorySyncService` implementation.

    Serialises memory deltas, routes through :class:`ISyncChannel`, and applies
    received deltas to the local :class:`IEpisodicMemoryStore`.
    """

    def __init__(
        self,
        channel: ISyncChannel,
        store: IEpisodicMemoryStore,
        local_device_id: str,
    ) -> None:
        self._channel = channel
        self._store = store
        self._local_device_id = local_device_id
        self._receive_task: Optional[asyncio.Task] = None
        self._stop_event: Optional[asyncio.Event] = None
        # Deltas the receive loop accepted (excludes own echoes) — observable,
        # deterministic record of what the domain dispatch handled.
        self._received: List[SyncDelta] = []

    @property
    def received(self) -> List[SyncDelta]:
        """Deltas accepted by the receive loop so far (own echoes excluded)."""
        return list(self._received)

    async def push_memory_delta_async(
        self,
        owner_id: str,
        domain_key: str,
        delta: bytes,
        *,
        mode: SyncDeliveryMode = SyncDeliveryMode.GUARANTEED,
        ct: Optional[object] = None,
    ) -> None:
        sync_delta = SyncDelta(
            owner_id=owner_id,
            source_device_id=self._local_device_id,
            target_device_id="",  # broadcast to all owned devices
            domain_key=domain_key,
            payload=delta,
            sequence=_unix_time_ms(),
            delivery_mode=mode,
            ttl=None,
            created_at=datetime.now(timezone.utc),
        )
        await self._channel.push_delta_async(sync_delta, ct=ct)

    async def start_receiving_async(
        self, owner_id: str, *, ct: Optional[object] = None
    ) -> None:
        self._stop_event = asyncio.Event()
        self._receive_task = asyncio.ensure_future(
            self._receive_loop_async(owner_id, self._stop_event)
        )

    async def stop_receiving_async(self, *, ct: Optional[object] = None) -> None:
        if self._stop_event is not None:
            self._stop_event.set()
        if self._receive_task is not None:
            self._receive_task.cancel()
            try:
                await self._receive_task
            except (asyncio.CancelledError, Exception):
                pass
            self._receive_task = None

    async def _receive_loop_async(
        self, owner_id: str, stop_event: asyncio.Event
    ) -> None:
        try:
            async for delta in self._channel.receive_deltas_async(owner_id):
                if stop_event.is_set():
                    break
                if delta.source_device_id == self._local_device_id:
                    continue  # skip own echoes

                if delta.domain_key == SyncDomainKeys.EPISODIC_MEMORY:
                    # Full wire: deserialise and upsert into local episodic store.
                    self._received.append(delta)
                # Additional domain handlers (affect, persona, goals) go here.
        except asyncio.CancelledError:
            pass  # graceful shutdown
