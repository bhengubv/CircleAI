# persona_state_sync_bridge.py
#
# Demonstrator: wires the existing IPersonaStore through the sync engine so
# PersonaState updates on one device automatically appear on every paired
# device. This is the FIRST concrete user-visible type to ride the sync
# engine — the same bridge pattern extends to CoreMemory, PersonaDelta,
# MultimodalMemoryEntry in follow-up commits.
#
# Ported faithfully from CircleAI.Memory.Sync.PersonaStateSyncBridge (C# — the
# spec). The C# bridge JSON-serialises its PersonaState record via
# System.Text.Json; here we serialise the Python PersonaState dataclass with a
# round-trippable JSON schema (datetime -> ISO-8601, set -> sorted list). The
# payload is opaque to the sync layer, which only hashes it.

from __future__ import annotations

import json
from datetime import datetime, timezone
from typing import Optional

from ..persona_state import PersonaState
from ..stores import IPersonaStore
from .companion_state_sync_engine import ICompanionStateSyncEngine
from .syncable_entry import SyncableEntry


def _parse_dt(value: object) -> datetime:
    if isinstance(value, datetime):
        return value
    if isinstance(value, str) and value:
        dt = datetime.fromisoformat(value)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    return datetime.now(timezone.utc)


class PersonaStateSyncBridge:
    """Bridges :class:`IPersonaStore` <-> :class:`ICompanionStateSyncEngine`.
    On :meth:`save_async`, the persona is JSON-serialised and pushed.
    """

    #: EntityType used on the wire for PersonaState entries.
    ENTITY_TYPE = "PersonaState"

    def __init__(
        self, store: IPersonaStore, engine: ICompanionStateSyncEngine
    ) -> None:
        if store is None:
            raise ValueError("store required")
        if engine is None:
            raise ValueError("engine required")
        self._store = store
        self._engine = engine

    async def save_async(
        self, persona: PersonaState, *, ct: Optional[object] = None
    ) -> None:
        """Persist ``persona`` locally AND broadcast it via sync."""
        if persona is None:
            raise ValueError("persona required")
        await self._store.save_async(persona, ct=ct)
        payload = self._serialize(persona)
        await self._engine.write_local_async(
            self.ENTITY_TYPE,
            persona.user_id,
            payload,
            is_tombstone=False,
            ct=ct,
        )

    @classmethod
    def try_decode(cls, entry: SyncableEntry) -> Optional[PersonaState]:
        """Decode a :class:`SyncableEntry` back into a :class:`PersonaState`.
        Useful for handlers that subscribe to inbound updates.
        """
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
    def _serialize(persona: PersonaState) -> str:
        obj = {
            "userId": persona.user_id,
            "lastUpdatedUtc": persona.last_updated_utc.isoformat(),
            "verbosity": persona.verbosity,
            "formality": persona.formality,
            "preferredLocale": persona.preferred_locale,
            "topicWeights": dict(persona.topic_weights),
            "disfavouredTopics": sorted(persona.disfavoured_topics),
            "totalInteractions": persona.total_interactions,
            "positiveSignals": persona.positive_signals,
            "negativeSignals": persona.negative_signals,
        }
        return json.dumps(obj, separators=(",", ":"))

    @staticmethod
    def _deserialize(payload: str) -> PersonaState:
        d = json.loads(payload)
        return PersonaState(
            user_id=d.get("userId", "default"),
            last_updated_utc=_parse_dt(d.get("lastUpdatedUtc")),
            verbosity=d.get("verbosity", "balanced"),
            formality=d.get("formality", "neutral"),
            preferred_locale=d.get("preferredLocale"),
            topic_weights=dict(d.get("topicWeights", {})),
            disfavoured_topics=set(d.get("disfavouredTopics", [])),
            total_interactions=d.get("totalInteractions", 0),
            positive_signals=d.get("positiveSignals", 0),
            negative_signals=d.get("negativeSignals", 0),
        )
