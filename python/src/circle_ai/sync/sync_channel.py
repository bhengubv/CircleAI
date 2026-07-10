# sync_channel.py
#
# ISyncChannel — the cross-device continuity primitive (transport seam).
#
# Ported faithfully from CircleAI.Networking.ISyncChannel (C# — the spec).
# The concrete transport (gRPC over 5G, BLE mesh, DTN bundle) is injected;
# app code is identical in every case. In-memory implementations live in tests
# / hosts — this module defines only the contract, matching the C# interface.

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import AsyncIterator, Optional

from .sync_types import SyncDelta


class ISyncChannel(ABC):
    """The cross-device continuity primitive.

    Pushes memory/state deltas across whatever transport is available:
    gRPC over 5G, BLE mesh via a neighbour, DTN bundle arriving 6 hours later.
    App code is identical in every case. This is the primitive that makes
    Circle AI HER + JARVIS: memory follows the person, not the device.
    """

    @abstractmethod
    async def push_delta_async(
        self, delta: SyncDelta, *, ct: Optional[object] = None
    ) -> None:
        """Push a delta. Channel selects transport and handles retries.
        Returns when accepted (not necessarily delivered for DTN/LocalStore).
        """
        ...

    @abstractmethod
    def receive_deltas_async(
        self, owner_id: str, *, ct: Optional[object] = None
    ) -> AsyncIterator[SyncDelta]:
        """Async-iterate over deltas arriving for ``owner_id``.

        Returns an async iterator (matching the C# ``IAsyncEnumerable``); the
        implementation is an ``async def`` generator or any object exposing
        ``__aiter__``/``__anext__``.
        """
        ...

    @abstractmethod
    async def get_last_sequence_async(
        self, owner_id: str, domain_key: str, *, ct: Optional[object] = None
    ) -> int:
        """Return the last-seen sequence number for ``owner_id`` + ``domain_key``."""
        ...
