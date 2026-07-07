# memory/in_memory_episodic_store.py
#
# Concrete in-memory IEpisodicMemoryStore for the memory-brain. Ported from
# CircleAI.Memory (InMemoryEpisodicStore) — the C# reference — and mirrors the
# TypeScript pilot (memory/stores.ts) and the Go port (memory_stores.go).
#
# All data is lost when the process exits; a persistent (SQLite-vec) backend is a
# later slice. The algorithms — cosine similarity == dot product on L2-normalised
# vectors, recency fallback, FIFO capacity eviction, prune — are identical to the
# reference.
#
# Async posture: the tree marks IEpisodicMemoryStore's methods ``*_async``
# (asyncio_mode = "auto"). The work here is pure in-memory CPU, so each coroutine
# completes synchronously — no lock is needed (asyncio is single-threaded and the
# body never awaits mid-operation), which is the same reasoning the TS pilot uses
# for dropping the C# ReaderWriterLockSlim.

from __future__ import annotations

from datetime import datetime
from typing import Optional

from .episodic_memory import EpisodicMemoryEntry


class InMemoryEpisodicStore:
    """In-memory :class:`IEpisodicMemoryStore`.

    Capacity is capped (FIFO eviction) to prevent unbounded growth on
    long-running processes. Satisfies the ``IEpisodicMemoryStore`` Protocol in
    :mod:`circle_ai.memory.stores` structurally.
    """

    def __init__(self, max_entries: int = 1000) -> None:
        """
        :param max_entries: Cap on stored entries; when exceeded the oldest are
            evicted (FIFO). Default 1000. Must be positive.
        """
        if max_entries <= 0:
            raise ValueError("max_entries must be positive")
        self._max_entries = max_entries
        self._entries: list[EpisodicMemoryEntry] = []

    async def add_async(
        self, entry: EpisodicMemoryEntry, *, ct: Optional[object] = None
    ) -> None:
        """Append a new entry; evict the oldest (FIFO) when over capacity."""
        if entry is None:
            raise ValueError("entry required")
        self._entries.append(entry)
        while len(self._entries) > self._max_entries:
            self._entries.pop(0)

    async def search_async(
        self,
        query_embedding: Optional[list[float]],
        top_k: int = 5,
        *,
        ct: Optional[object] = None,
    ) -> list[EpisodicMemoryEntry]:
        """Return top-k entries by cosine similarity; fall back to recency.

        With no embedding (``None`` or empty) returns the most recent entries.
        Otherwise ranks by cosine similarity — but only against entries whose
        embedding has the same dimension as the query. Both vectors are assumed
        L2-normalised, so cosine similarity == dot product.
        """
        snapshot = list(self._entries)

        if query_embedding is None or len(query_embedding) == 0:
            # No embedding — return most recent.
            return sorted(
                snapshot, key=lambda e: e.recorded_at_utc, reverse=True
            )[:top_k]

        dim = len(query_embedding)
        scored = [
            (e, _cosine_similarity(query_embedding, e.embedding))
            for e in snapshot
            if e.embedding is not None and len(e.embedding) == dim
        ]
        scored.sort(key=lambda x: x[1], reverse=True)
        return [e for e, _ in scored[:top_k]]

    async def get_recent_async(
        self, count: int = 10, *, ct: Optional[object] = None
    ) -> list[EpisodicMemoryEntry]:
        """Return the *count* most recent entries, newest-first."""
        return sorted(
            self._entries, key=lambda e: e.recorded_at_utc, reverse=True
        )[:count]

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        """Total number of entries currently stored."""
        return len(self._entries)

    async def prune_older_than_async(
        self, cutoff: datetime, *, ct: Optional[object] = None
    ) -> int:
        """Remove all entries older than *cutoff*; return the count removed."""
        before = len(self._entries)
        self._entries = [e for e in self._entries if e.recorded_at_utc >= cutoff]
        return before - len(self._entries)


def _cosine_similarity(a: list[float], b: list[float]) -> float:
    """Cosine similarity of two equal-length, L2-normalised vectors (== dot)."""
    dot = 0.0
    for i in range(len(a)):
        dot += a[i] * b[i]
    return dot
