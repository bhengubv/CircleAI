# syncable_entry.py
#
# The wire format. Every piece of companion state that crosses devices is
# wrapped in one of these. Payload is opaque JSON (or any string); type
# adapters serialise their own records into the Payload field and back.
#
# ContentHash is SHA-256 of the Payload — used as the tiebreaker when two
# peers happen to write the same Version (impossibly rare with HLC, but
# the system must still converge deterministically).
#
# Ported faithfully from CircleAI.Memory.Sync.SyncableEntry (C# — the spec).

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime


@dataclass(frozen=True, slots=True)
class SyncableEntry:
    """A single syncable item — the smallest unit the engine moves between peers.

    :param entity_type: Logical type — e.g. "PersonaState", "CoreMemory",
        "DailyMemorySummary".
    :param entity_id: Identifier within the type — e.g. a user ID, a GUID-N
        format string.
    :param version: HLC-produced monotonic version stamp.
    :param is_tombstone: True when this entry represents a deletion. Payload is
        empty in that case.
    :param content_hash: SHA-256 hex of ``payload`` — content tiebreaker when
        versions collide.
    :param payload: Opaque payload — type-specific JSON or any string the
        adapter chose.
    :param source_node_id: Identifier of the node that authored this version
        (for debugging + provenance).
    :param authored_at: UTC wall-clock when authored — for human-facing
        display, not for ordering.
    """

    entity_type: str
    entity_id: str
    version: int
    is_tombstone: bool
    content_hash: str
    payload: str
    source_node_id: str
    authored_at: datetime
