from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from enum import Enum
from typing import Optional


class SyncDeliveryMode(Enum):
    """How hard the sync channel should try to deliver a delta."""
    BEST_EFFORT = "BestEffort"  # fire-and-forget; may be lost
    GUARANTEED  = "Guaranteed"  # retried until acknowledged or TTL expires
    URGENT      = "Urgent"      # highest priority, interrupts current transfer


@dataclass(frozen=True)
class SchedulingHint:
    """Scheduling advice for the sync channel."""

    preferred_peer_ids: list[str]
    suggested_window_utc: Optional[datetime]
    confidence_score: float  # [0.0, 1.0]


@dataclass(frozen=True)
class SyncDelta:
    """An incremental state change that must reach every device owned by owner_id.

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
    ttl: Optional[float]    # seconds; None = no expiry
    created_at: datetime
    scheduling_hint: Optional[SchedulingHint] = None


class SyncDomainKeys:
    """Canonical domain key strings for SyncDelta.domain_key."""

    EPISODIC_MEMORY = "memory.episodic"
    AFFECT_STATE    = "affect.state"
    PERSONA         = "persona"
    GOALS           = "goals"
    SKILLS          = "skills"
    PREFERENCES     = "preferences"
