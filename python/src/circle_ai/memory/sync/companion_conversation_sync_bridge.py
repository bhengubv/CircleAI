# companion_conversation_sync_bridge.py
#
# (Phase A2 — HER/Jarvis parity) Bridges live conversation state to the
# sync engine so a session that starts on the phone can be picked up on
# the laptop mid-stream.
#
# Each "conversation delta" is a strongly-typed snapshot of the active
# turn: session id, last user utterance, last assistant text-so-far, the
# timestamp, and whether the turn has completed. The receiving device's
# session handler can resume from the partial assistant text without
# losing context.
#
# Ported faithfully from CircleAI.Memory.Sync.CompanionConversationSyncBridge
# (C# — the spec).

from __future__ import annotations

import json
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Optional

from .companion_state_sync_engine import ICompanionStateSyncEngine
from .syncable_entry import SyncableEntry


def _parse_dt(value: object) -> datetime:
    if isinstance(value, str) and value:
        dt = datetime.fromisoformat(value)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    return datetime.now(timezone.utc)


@dataclass(frozen=True, slots=True)
class ConversationStateDelta:
    """(Phase A2) Wire-format payload of an in-flight conversation turn. The
    EntityId is the SessionId so multiple sessions converge independently.

    :param session_id: Stable identifier the originating device uses for this
        conversation.
    :param user_text: The latest user utterance for this turn (may be partial
        transcript).
    :param assistant_text: Assistant reply so far — empty until the model
        starts emitting tokens.
    :param is_turn_complete: True once the turn finished; false during
        streaming.
    :param started_at_utc: When the originating device started the turn.
    :param updated_at_utc: When this delta was authored.
    """

    session_id: str
    user_text: str
    assistant_text: str
    is_turn_complete: bool
    started_at_utc: datetime
    updated_at_utc: datetime


class CompanionConversationSyncBridge:
    """(Phase A2) Bridges live :class:`ConversationStateDelta` snapshots to the
    existing :class:`ICompanionStateSyncEngine` wire so any peer device
    subscribing to the "ConversationState" entity type can mirror or hand off
    the conversation.
    """

    #: EntityType used on the wire for conversation-state entries.
    ENTITY_TYPE = "ConversationState"

    def __init__(self, engine: ICompanionStateSyncEngine) -> None:
        if engine is None:
            raise ValueError("engine required")
        self._engine = engine

    async def publish_async(
        self, delta: ConversationStateDelta, *, ct: Optional[object] = None
    ) -> SyncableEntry:
        """Broadcast a conversation-state snapshot to peer devices. The
        receiving device's bridge subscribes via
        :class:`ICompanionStateChannel` and routes the delta into its own
        session-equivalent runtime.
        """
        if delta is None:
            raise ValueError("delta required")
        if delta.session_id is None or delta.session_id.strip() == "":
            raise ValueError("session_id required")
        payload = self._serialize(delta)
        return await self._engine.write_local_async(
            self.ENTITY_TYPE, delta.session_id, payload, is_tombstone=False, ct=ct
        )

    async def terminate_async(
        self, session_id: str, *, ct: Optional[object] = None
    ) -> SyncableEntry:
        """Mark the session as ended so peers can clean up shadow state. Uses
        the sync-layer tombstone primitive — peers receive an empty payload.
        """
        if session_id is None or session_id.strip() == "":
            raise ValueError("session_id required")
        return await self._engine.write_local_async(
            self.ENTITY_TYPE, session_id, "", is_tombstone=True, ct=ct
        )

    @classmethod
    def try_decode(cls, entry: SyncableEntry) -> Optional[ConversationStateDelta]:
        """Decode a sync-layer entry back to a typed delta."""
        if entry is None:
            raise ValueError("entry required")
        if entry.is_tombstone:
            return None
        if entry.entity_type != cls.ENTITY_TYPE:
            return None
        try:
            return cls._deserialize(entry.payload)
        except (ValueError, json.JSONDecodeError):
            return None

    # ── serialisation ────────────────────────────────────────────────────

    @staticmethod
    def _serialize(delta: ConversationStateDelta) -> str:
        obj = {
            "sessionId": delta.session_id,
            "userText": delta.user_text,
            "assistantText": delta.assistant_text,
            "isTurnComplete": delta.is_turn_complete,
            "startedAtUtc": delta.started_at_utc.isoformat(),
            "updatedAtUtc": delta.updated_at_utc.isoformat(),
        }
        return json.dumps(obj, separators=(",", ":"))

    @staticmethod
    def _deserialize(payload: str) -> ConversationStateDelta:
        d = json.loads(payload)
        return ConversationStateDelta(
            session_id=d.get("sessionId", ""),
            user_text=d.get("userText", ""),
            assistant_text=d.get("assistantText", ""),
            is_turn_complete=bool(d.get("isTurnComplete", False)),
            started_at_utc=_parse_dt(d.get("startedAtUtc")),
            updated_at_utc=_parse_dt(d.get("updatedAtUtc")),
        )
