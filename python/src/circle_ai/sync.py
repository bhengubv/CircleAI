# sync.py
#
# Python port of Circle.AI.Networking sync primitives.
#
# Covers:
#   SyncDeliveryMode  — BestEffort | Guaranteed | Urgent
#   SyncDomainKeys    — well-known domain key constants
#   SyncDelta         — incremental state change to replicate across devices
#   ISyncChannel      — cross-device continuity primitive ABC

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone, timedelta
from enum import Enum
from typing import AsyncGenerator, Optional


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# ---------------------------------------------------------------------------
# Enumerations
# ---------------------------------------------------------------------------

class SyncDeliveryMode(Enum):
    """How hard the sync channel should try to deliver a delta."""
    BestEffort = "BestEffort"   # fire-and-forget; may be lost
    Guaranteed = "Guaranteed"   # retried until acknowledged or TTL expires
    Urgent     = "Urgent"       # highest priority, interrupts current transfer


# ---------------------------------------------------------------------------
# Well-known domain keys
# ---------------------------------------------------------------------------

class SyncDomainKeys:
    """Canonical domain key strings for SyncDelta.domain_key."""

    MEMORY_EPISODIC = "memory.episodic"
    AFFECT_STATE    = "affect.state"
    PERSONA         = "persona"
    GOALS           = "goals"
    FEEDBACK        = "feedback"


# ---------------------------------------------------------------------------
# SyncDelta
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class SyncDelta:
    """An incremental state change that must reach every device owned by *owner_id*.

    This is the primitive that makes Circle AI cross-device continuous —
    HER + JARVIS memory following the person.
    """

    owner_id: str           # identity whose state this belongs to
    source_device_id: str   # origin device
    target_device_id: str   # "" = broadcast to all owned devices
    domain_key: str         # "memory.episodic" | "affect.state" | "persona" | custom
    payload: bytes          # serialised state fragment
    sequence: int           # monotonic per owner+domain
    delivery_mode: SyncDeliveryMode
    ttl: Optional[timedelta]
    created_at: datetime


# ---------------------------------------------------------------------------
# ISyncChannel ABC
# ---------------------------------------------------------------------------

class ISyncChannel(ABC):
    """The cross-device continuity primitive.

    Pushes memory/state deltas across whatever transport is available:
    gRPC over 5G, BLE mesh via a neighbour, DTN bundle arriving 6 hours later.
    App code is identical in every case.
    """

    @abstractmethod
    async def push_delta_async(
        self, delta: SyncDelta, *, ct: Optional[object] = None
    ) -> None:
        """Push a delta.

        Channel selects transport and handles retries.  Returns when accepted
        (not necessarily delivered for DTN/LocalStore).
        """
        ...

    @abstractmethod
    async def receive_deltas_async(
        self, owner_id: str, *, ct: Optional[object] = None
    ) -> AsyncGenerator[SyncDelta, None]:
        """Async-iterate over deltas arriving for *owner_id*."""
        ...

    @abstractmethod
    async def get_last_sequence_async(
        self,
        owner_id: str,
        domain_key: str,
        *,
        ct: Optional[object] = None,
    ) -> int:
        """Return the last-seen sequence number for *owner_id* + *domain_key*."""
        ...
