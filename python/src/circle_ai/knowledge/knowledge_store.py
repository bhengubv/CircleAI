# knowledge_store.py
#
# Port of CircleAI.Knowledge IKnowledgeStore.cs + FileSystemKnowledgeStore.cs
# (C# — the EXACT spec).
#
#   • IKnowledgeStore — get / save / delete + async-streaming search-by-tag /
#     enumerate-all.
#   • FileSystemKnowledgeStore — one .md file per note under a configured root,
#     named "{id-no-dashes}.md". Writes are atomic (write-to-tmp + rename);
#     thread-safe via a per-Guid lock.
#   • InMemoryKnowledgeStore — deterministic in-memory store (no disk), same
#     contract; save refreshes UpdatedAt.
#
# The C# type derives from CircleAIComponentBase (operational wrapping only); the
# storage semantics are ported directly. IAsyncEnumerable -> async generator.
# Guid.ToString("N") -> uuid.hex (32 hex chars, no dashes).

from __future__ import annotations

import os
import threading
import uuid
from dataclasses import replace
from datetime import datetime, timezone
from typing import AsyncIterator, Dict, Optional

from .knowledge_note import KnowledgeNote


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class IKnowledgeStore:
    """Persistent store for :class:`KnowledgeNote` documents. Mirrors
    ``CircleAI.Knowledge.IKnowledgeStore``."""

    async def get_async(
        self, id: uuid.UUID, ct: Optional[object] = None
    ) -> Optional[KnowledgeNote]:
        raise NotImplementedError  # pragma: no cover - interface marker

    async def save_async(
        self, note: KnowledgeNote, ct: Optional[object] = None
    ) -> KnowledgeNote:
        raise NotImplementedError  # pragma: no cover - interface marker

    async def delete_async(
        self, id: uuid.UUID, ct: Optional[object] = None
    ) -> None:
        raise NotImplementedError  # pragma: no cover - interface marker

    def search_by_tag_async(
        self, tag: str, ct: Optional[object] = None
    ) -> AsyncIterator[KnowledgeNote]:
        raise NotImplementedError  # pragma: no cover - interface marker

    def enumerate_all_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[KnowledgeNote]:
        raise NotImplementedError  # pragma: no cover - interface marker


class FileSystemKnowledgeStore(IKnowledgeStore):
    """File-system :class:`IKnowledgeStore`. Mirrors
    ``CircleAI.Knowledge.FileSystemKnowledgeStore``."""

    def __init__(self, root_directory: str) -> None:
        if root_directory is None or root_directory.strip() == "":
            raise ValueError("rootDirectory required")
        self._root = root_directory
        self._locks: Dict[uuid.UUID, threading.Lock] = {}
        self._locks_guard = threading.Lock()
        os.makedirs(self._root, exist_ok=True)

    def _lock_for(self, id: uuid.UUID) -> threading.Lock:
        with self._locks_guard:
            lk = self._locks.get(id)
            if lk is None:
                lk = threading.Lock()
                self._locks[id] = lk
            return lk

    def _note_path(self, id: uuid.UUID) -> str:
        return os.path.join(self._root, id.hex + ".md")

    async def get_async(
        self, id: uuid.UUID, ct: Optional[object] = None
    ) -> Optional[KnowledgeNote]:
        path = self._note_path(id)
        if not os.path.isfile(path):
            return None
        with self._lock_for(id):
            with open(path, "r", encoding="utf-8") as fh:
                return KnowledgeNote.parse_file(fh.read())

    async def save_async(
        self, note: KnowledgeNote, ct: Optional[object] = None
    ) -> KnowledgeNote:
        if note is None:
            raise ValueError("note")
        refreshed = replace(note, updated_at=_utc_now())
        target = self._note_path(refreshed.id)
        tmp = target + "." + uuid.uuid4().hex + ".tmp"
        with self._lock_for(refreshed.id):
            try:
                with open(tmp, "w", encoding="utf-8") as fh:
                    fh.write(refreshed.to_file_text())
                os.replace(tmp, target)  # atomic move-with-overwrite
                return refreshed
            except Exception:
                try:
                    if os.path.exists(tmp):
                        os.remove(tmp)
                except OSError:
                    pass
                raise

    async def delete_async(
        self, id: uuid.UUID, ct: Optional[object] = None
    ) -> None:
        path = self._note_path(id)
        with self._lock_for(id):
            if os.path.isfile(path):
                os.remove(path)

    async def search_by_tag_async(
        self, tag: str, ct: Optional[object] = None
    ) -> AsyncIterator[KnowledgeNote]:
        if tag is None or tag.strip() == "":
            raise ValueError("tag required")
        async for note in self.enumerate_all_async(ct):
            if any(t.lower() == tag.lower() for t in note.tags):
                yield note

    async def enumerate_all_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[KnowledgeNote]:
        if not os.path.isdir(self._root):
            return
        for fn in sorted(os.listdir(self._root)):
            if not fn.endswith(".md"):
                continue
            path = os.path.join(self._root, fn)
            if not os.path.isfile(path):
                continue
            try:
                with open(path, "r", encoding="utf-8") as fh:
                    note = KnowledgeNote.parse_file(fh.read())
            except Exception:
                # Skip notes not in our format (e.g. a stray README.md).
                continue
            yield note


class InMemoryKnowledgeStore(IKnowledgeStore):
    """Deterministic in-memory :class:`IKnowledgeStore` (no disk). Same contract
    as :class:`FileSystemKnowledgeStore`; ``save`` refreshes ``updated_at``."""

    def __init__(self) -> None:
        self._notes: Dict[uuid.UUID, KnowledgeNote] = {}
        self._lock = threading.Lock()

    async def get_async(
        self, id: uuid.UUID, ct: Optional[object] = None
    ) -> Optional[KnowledgeNote]:
        with self._lock:
            return self._notes.get(id)

    async def save_async(
        self, note: KnowledgeNote, ct: Optional[object] = None
    ) -> KnowledgeNote:
        if note is None:
            raise ValueError("note")
        refreshed = replace(note, updated_at=_utc_now())
        with self._lock:
            self._notes[refreshed.id] = refreshed
        return refreshed

    async def delete_async(
        self, id: uuid.UUID, ct: Optional[object] = None
    ) -> None:
        with self._lock:
            self._notes.pop(id, None)

    async def search_by_tag_async(
        self, tag: str, ct: Optional[object] = None
    ) -> AsyncIterator[KnowledgeNote]:
        if tag is None or tag.strip() == "":
            raise ValueError("tag required")
        async for note in self.enumerate_all_async(ct):
            if any(t.lower() == tag.lower() for t in note.tags):
                yield note

    async def enumerate_all_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[KnowledgeNote]:
        with self._lock:
            snapshot = list(self._notes.values())
        for note in snapshot:
            yield note
