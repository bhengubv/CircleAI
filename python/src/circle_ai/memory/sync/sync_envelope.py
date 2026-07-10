# sync_envelope.py
#
# Three envelope kinds drive the convergence protocol:
#
#   Announce  — "I am node N. For each entity type, my highest version is V."
#   Request   — "I see you have version > mine for type T since version X.
#                Send me everything you have for T newer than X."
#   Push      — "Here are entries you asked for (or that I want you to apply)."
#
# The protocol is deliberately simple. Two peers exchange Announce; whoever
# is behind sends a Request; the other replies with a Push; the receiver
# upserts. Repeating Announce always converges.
#
# Ported faithfully from CircleAI.Memory.Sync.SyncEnvelope (C# — the spec).

from __future__ import annotations

from dataclasses import dataclass
from enum import IntEnum
from typing import Optional, Sequence

from .syncable_entry import SyncableEntry


class SyncEnvelopeKind(IntEnum):
    """Kind of sync envelope."""

    ANNOUNCE = 0
    """Broadcast of the sender's per-entity-type high-watermark versions."""
    REQUEST = 1
    """Reply to an Announce asking for entries newer than a known version."""
    PUSH = 2
    """Unsolicited or replied delivery of syncable entries."""


@dataclass(frozen=True, slots=True)
class StateVectorEntry:
    """Per-entity-type high-watermark — used in Announce/Request payloads."""

    entity_type: str
    max_known_version: int


@dataclass(frozen=True, slots=True)
class RequestItem:
    """Reply-side request item — "send me entries of ``entity_type`` strictly
    newer than ``since_version``".
    """

    entity_type: str
    since_version: int


@dataclass(frozen=True, slots=True)
class SyncEnvelope:
    """A sync envelope — the message unit that crosses the channel."""

    kind: SyncEnvelopeKind
    from_node_id: str
    state_vector: Optional[Sequence[StateVectorEntry]]
    requests: Optional[Sequence[RequestItem]]
    entries: Optional[Sequence[SyncableEntry]]
