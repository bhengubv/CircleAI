# markdown_episodic_memory_store.py
#
# Port of CircleAI.Knowledge MarkdownEpisodicMemoryStore.cs (C# — the EXACT spec).
#
# Markdown-on-disk implementation of CircleAI.Memory.IEpisodicMemoryStore, backed
# by an IKnowledgeStore. Each EpisodicMemoryEntry is persisted as one
# KnowledgeNote with structured frontmatter and a
# "## User\n\n... ## Assistant\n\n..." body. Human-readable + Git-diffable.
#
# Faithful details:
#   • embedding is stored as base64 of the raw float32[] bytes (little-endian) +
#     an ``embedding_dims`` count — struct.pack("<f", x) at every float site.
#   • tags map to ``tag_<key>`` frontmatter entries and the note's Tags list holds
#     the keys.
#   • Search: if no query embedding, order by recency; else cosine over entries
#     whose embedding length matches (cosine here is a bare dot product, as in C#).
#   • empty-Guid entry id -> a fresh uuid4 on ToNote (mirrors C#).

from __future__ import annotations

import base64
import struct
import uuid
from datetime import datetime, timezone
from typing import Dict, List, Optional

from ..memory.episodic_memory import EpisodicMemoryEntry
from .knowledge_note import KnowledgeNote
from .knowledge_store import IKnowledgeStore

_EPISODE_ID_KEY = "episode_id"
_RECORDED_AT_KEY = "recorded_at"
_APP_CONTEXT_KEY = "app_context"
_EMBEDDING_KEY = "embedding"
_EMBEDDING_DIMS_KEY = "embedding_dims"
_TAG_PREFIX = "tag_"

_EMPTY_GUID = uuid.UUID(int=0)


def _f32(x: float) -> float:
    return struct.unpack("<f", struct.pack("<f", x))[0]


class MarkdownEpisodicMemoryStore:
    """Markdown-on-disk :class:`~circle_ai.memory.stores.IEpisodicMemoryStore`
    backed by an :class:`IKnowledgeStore`. Mirrors
    ``CircleAI.Knowledge.MarkdownEpisodicMemoryStore``."""

    def __init__(self, store: IKnowledgeStore) -> None:
        if store is None:
            raise ValueError("store")
        self._store = store

    async def add_async(
        self, entry: EpisodicMemoryEntry, *, ct: Optional[object] = None
    ) -> None:
        if entry is None:
            raise ValueError("entry")
        note = self.to_note(entry)
        await self._store.save_async(note, ct)

    async def search_async(
        self,
        query_embedding: Optional[List[float]],
        top_k: int = 5,
        *,
        ct: Optional[object] = None,
    ) -> List[EpisodicMemoryEntry]:
        snapshot: List[EpisodicMemoryEntry] = []
        async for note in self._store.enumerate_all_async(ct):
            snapshot.append(self.from_note(note))

        if query_embedding is None or len(query_embedding) == 0:
            snapshot.sort(key=lambda e: e.recorded_at_utc, reverse=True)
            return snapshot[:top_k]

        scored = [
            (e, self._cosine_similarity(query_embedding, e.embedding))
            for e in snapshot
            if e.embedding is not None and len(e.embedding) == len(query_embedding)
        ]
        scored.sort(key=lambda x: x[1], reverse=True)
        return [e for (e, _s) in scored[:top_k]]

    async def get_recent_async(
        self, count: int = 10, *, ct: Optional[object] = None
    ) -> List[EpisodicMemoryEntry]:
        snapshot: List[EpisodicMemoryEntry] = []
        async for note in self._store.enumerate_all_async(ct):
            snapshot.append(self.from_note(note))
        snapshot.sort(key=lambda e: e.recorded_at_utc, reverse=True)
        return snapshot[:count]

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        n = 0
        async for _ in self._store.enumerate_all_async(ct):
            n += 1
        return n

    async def prune_older_than_async(
        self, cutoff: datetime, *, ct: Optional[object] = None
    ) -> int:
        doomed: List[uuid.UUID] = []
        async for note in self._store.enumerate_all_async(ct):
            entry = self.from_note(note)
            if entry.recorded_at_utc < cutoff:
                doomed.append(note.id)
        for id in doomed:
            await self._store.delete_async(id, ct)
        return len(doomed)

    # -- EpisodicMemoryEntry <-> KnowledgeNote --------------------------------

    @staticmethod
    def to_note(entry: EpisodicMemoryEntry) -> KnowledgeNote:
        """Map an :class:`EpisodicMemoryEntry` to its :class:`KnowledgeNote`."""
        frontmatter: Dict[str, str] = {
            _EPISODE_ID_KEY: str(entry.id),
            _RECORDED_AT_KEY: entry.recorded_at_utc.isoformat(),
        }
        if entry.app_context is not None and entry.app_context.strip() != "":
            frontmatter[_APP_CONTEXT_KEY] = entry.app_context

        emb = entry.embedding
        if emb is not None and len(emb) > 0:
            raw = b"".join(struct.pack("<f", x) for x in emb)
            frontmatter[_EMBEDDING_KEY] = base64.b64encode(raw).decode("ascii")
            frontmatter[_EMBEDDING_DIMS_KEY] = str(len(emb))

        tags: List[str] = []
        if entry.tags is not None:
            for k, v in entry.tags.items():
                frontmatter[_TAG_PREFIX + k] = v
                tags.append(k)

        body = "## User\n\n" + entry.user_text + "\n\n" + "## Assistant\n\n" + entry.assistant_text

        note_id = uuid.uuid4() if entry.id == _EMPTY_GUID else entry.id
        return KnowledgeNote(
            id=note_id,
            title=_truncate_for_title(entry.user_text),
            body_markdown=body,
            frontmatter=frontmatter,
            tags=tags,
            created_at=entry.recorded_at_utc,
            updated_at=entry.recorded_at_utc,
        )

    @staticmethod
    def from_note(note: KnowledgeNote) -> EpisodicMemoryEntry:
        """Inverse of :meth:`to_note`."""
        episode_id = note.id
        raw = note.frontmatter.get(_EPISODE_ID_KEY)
        if raw is not None:
            try:
                episode_id = uuid.UUID(raw)
            except ValueError:
                episode_id = note.id

        recorded_at = note.created_at
        raw_when = note.frontmatter.get(_RECORDED_AT_KEY)
        if raw_when is not None:
            try:
                recorded_at = datetime.fromisoformat(raw_when)
            except ValueError:
                recorded_at = note.created_at

        app_context = note.frontmatter.get(_APP_CONTEXT_KEY)

        embedding: Optional[List[float]] = None
        b64 = note.frontmatter.get(_EMBEDDING_KEY)
        if b64 is not None and b64.strip() != "":
            try:
                data = base64.b64decode(b64)
                count = len(data) // 4
                embedding = [struct.unpack_from("<f", data, i * 4)[0] for i in range(count)]
            except Exception:
                embedding = None

        user_text, assistant_text = _split_body(note.body_markdown)

        tags_out: Optional[Dict[str, str]] = None
        for k, v in note.frontmatter.items():
            if not k.startswith(_TAG_PREFIX):
                continue
            if tags_out is None:
                tags_out = {}
            tags_out[k[len(_TAG_PREFIX):]] = v

        return EpisodicMemoryEntry(
            id=episode_id,
            recorded_at_utc=recorded_at,
            user_text=user_text,
            assistant_text=assistant_text,
            app_context=app_context,
            embedding=embedding,
            tags=tags_out,
        )

    @staticmethod
    def _cosine_similarity(a: List[float], b: List[float]) -> float:
        dot = 0.0
        for i in range(len(a)):
            dot = _f32(dot + _f32(_f32(a[i]) * _f32(b[i])))
        return dot


def _split_body(body: str):
    if body is None or body == "":
        return "", ""
    normal = body.replace("\r\n", "\n")
    user_marker = "## User\n\n"
    assistant_marker = "\n\n## Assistant\n\n"
    user_idx = normal.find(user_marker)
    assistant_idx = normal.find(assistant_marker)
    if user_idx < 0 or assistant_idx <= user_idx:
        return normal, ""
    user_text = normal[user_idx + len(user_marker):assistant_idx]
    assistant_text = normal[assistant_idx + len(assistant_marker):]
    return user_text, assistant_text


def _truncate_for_title(source: str) -> str:
    if source is None or source.strip() == "":
        return "(untitled)"
    single = source.replace("\n", " ").replace("\r", " ").strip()
    return single if len(single) <= 64 else single[:64]
