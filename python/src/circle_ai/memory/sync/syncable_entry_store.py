# syncable_entry_store.py
#
# ISyncableEntryStore — the seat the sync engine reads from and writes to —
# plus the in-memory implementation.
#
# Ported faithfully from CircleAI.Memory.Sync.ISyncableEntryStore and
# CircleAI.Memory.Sync.InMemorySyncableEntryStore (C# — the spec).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from typing import Dict, List, Optional, Sequence, Tuple

from .sync_envelope import StateVectorEntry
from .syncable_entry import SyncableEntry


class ISyncableEntryStore(ABC):
    """The seat the sync engine reads from and writes to. Implementations track
    the local view of all known syncable entries plus their version stamps.

    Apply rules — implementations MUST enforce these for convergence:
      • Higher Version wins
      • On tie (same Version), higher ContentHash (string compare) wins
      • Tombstones replace any non-tombstone of equal-or-lower Version
    """

    @abstractmethod
    async def apply_async(
        self, entry: SyncableEntry, *, ct: Optional[object] = None
    ) -> bool:
        """Apply an incoming entry.

        Returns True when local state was actually updated (incoming was
        strictly newer / preferred). Returns False when the local entry was
        already at or beyond the incoming version.
        """
        ...

    @abstractmethod
    async def get_async(
        self, entity_type: str, entity_id: str, *, ct: Optional[object] = None
    ) -> Optional[SyncableEntry]:
        """Return the current entry for the given (type, id), or None when not
        known locally. Tombstones ARE returned — callers needing "is it
        deleted?" should check :attr:`SyncableEntry.is_tombstone`.
        """
        ...

    @abstractmethod
    async def get_since_async(
        self, entity_type: str, since_version: int, *, ct: Optional[object] = None
    ) -> Sequence[SyncableEntry]:
        """Return every entry of the given type whose Version is strictly
        greater than ``since_version``, ordered ascending by Version.
        """
        ...

    @abstractmethod
    async def get_state_vector_async(
        self, *, ct: Optional[object] = None
    ) -> Sequence[StateVectorEntry]:
        """Return the highest known Version per entity type — the local node's
        state vector. Types with no entries are omitted.
        """
        ...


class InMemorySyncableEntryStore(ISyncableEntryStore):
    """In-memory :class:`ISyncableEntryStore`."""

    def __init__(self) -> None:
        # Keyed by (type, id) so writes are O(1).
        self._entries: Dict[Tuple[str, str], SyncableEntry] = {}
        self._lock = threading.Lock()
        self._max_version_by_type: Dict[str, int] = {}

    async def apply_async(
        self, entry: SyncableEntry, *, ct: Optional[object] = None
    ) -> bool:
        if entry is None:
            raise ValueError("entry required")
        key = (entry.entity_type, entry.entity_id)

        with self._lock:
            existing = self._entries.get(key)
            if existing is None:
                self._entries[key] = entry
                applied = True
            elif self._should_apply(existing, entry):
                self._entries[key] = entry
                applied = True
            else:
                applied = False

            if applied:
                current = self._max_version_by_type.get(entry.entity_type, 0)
                if entry.version > current:
                    self._max_version_by_type[entry.entity_type] = entry.version
        return applied

    async def get_async(
        self, entity_type: str, entity_id: str, *, ct: Optional[object] = None
    ) -> Optional[SyncableEntry]:
        with self._lock:
            return self._entries.get((entity_type, entity_id))

    async def get_since_async(
        self, entity_type: str, since_version: int, *, ct: Optional[object] = None
    ) -> Sequence[SyncableEntry]:
        with self._lock:
            result: List[SyncableEntry] = [
                e
                for e in self._entries.values()
                if e.entity_type == entity_type and e.version > since_version
            ]
        result.sort(key=lambda e: e.version)
        return result

    async def get_state_vector_async(
        self, *, ct: Optional[object] = None
    ) -> Sequence[StateVectorEntry]:
        with self._lock:
            vector = [
                StateVectorEntry(entity_type=k, max_known_version=v)
                for k, v in self._max_version_by_type.items()
            ]
        vector.sort(key=lambda e: e.entity_type)
        return vector

    @staticmethod
    def _should_apply(existing: SyncableEntry, incoming: SyncableEntry) -> bool:
        """Apply rule: higher Version wins; on tie, higher ContentHash (string
        compare) wins; tombstone replaces a non-tombstone of equal version.
        """
        if incoming.version > existing.version:
            return True
        if incoming.version < existing.version:
            return False
        # Equal versions — tombstone-of-non-tombstone wins.
        if incoming.is_tombstone and not existing.is_tombstone:
            return True
        if not incoming.is_tombstone and existing.is_tombstone:
            return False
        # Same tombstone state, same version — content hash tiebreaker.
        # Python str comparison is ordinal (by code point), matching C#
        # string.CompareOrdinal for these ASCII hex hashes.
        return incoming.content_hash > existing.content_hash
