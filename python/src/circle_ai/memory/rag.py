# memory/rag.py
#
# Retrieval-augmented context assembly. Ported from CircleAI.Memory (C#):
#   • ITextEmbedder (CircleAI.Embeddings) — the semantic-ranking seam
#   • RagContextBuilder — retrieves the most relevant episodes and formats them
#     as a compact context block for injection into the B! system prompt
#   • RagPipelineBuilder — fluent factory with sensible defaults
# Mirrors the verified TypeScript reference (memory/rag.ts).
#
# RAG is strictly best-effort: any retrieval / embedding failure degrades to an
# empty string and must never block inference. In-memory port — the C#
# WithSqliteStore convenience is intentionally omitted (no SQLite backend here);
# use with_store / with_in_memory_store instead.

from __future__ import annotations

from typing import Optional, Protocol, runtime_checkable

from .episodic_memory import EpisodicMemoryEntry
from .in_memory_episodic_store import InMemoryEpisodicStore
from .stores import IEpisodicMemoryStore


# ─────────────────────────────────────────────────────────────────────────────
# ITextEmbedder — CircleAI.Embeddings.ITextEmbedder
# ─────────────────────────────────────────────────────────────────────────────


@runtime_checkable
class ITextEmbedder(Protocol):
    """Produces an embedding vector for a text."""

    async def generate_async(
        self, text: str, *, ct: Optional[object] = None
    ) -> list[float]:
        """Return the embedding vector for *text*."""
        ...


# ─────────────────────────────────────────────────────────────────────────────
# RagContextBuilder — CircleAI.Memory.RagContextBuilder
# ─────────────────────────────────────────────────────────────────────────────


class RagContextBuilder:
    """Retrieves the most semantically relevant episodes from an
    :class:`IEpisodicMemoryStore` and formats them as a compact context block
    for injection into the B! system prompt.
    """

    def __init__(
        self,
        store: IEpisodicMemoryStore,
        embedder: Optional[ITextEmbedder] = None,
        top_k: int = 5,
        max_chars_per_entry: int = 300,
    ) -> None:
        """
        :param store: The episodic store to query.
        :param embedder: Optional embedder. When provided, uses semantic
            similarity to rank results; when ``None``, falls back to recency.
        :param top_k: Maximum number of episodes to include. Default 5 (floored
            at 1).
        :param max_chars_per_entry: Maximum characters taken from each episode's
            texts. Default 300 (floored at 50).
        """
        if store is None:
            raise ValueError("store required")
        self._store = store
        self._embedder = embedder
        self._top_k = max(1, top_k)
        self._max_chars_per_entry = max(50, max_chars_per_entry)

    async def build_context_async(
        self, query: str, *, ct: Optional[object] = None
    ) -> str:
        """Build a context block for the given *query* text.

        Returns an empty string when the store is empty or all retrievals fail
        (RAG is best-effort and must never block inference).
        """
        if query is None or len(query.strip()) == 0:
            return ""

        try:
            query_embedding: Optional[list[float]] = None
            if self._embedder is not None:
                try:
                    query_embedding = await self._embedder.generate_async(
                        query, ct=ct
                    )
                except Exception:
                    # Embedding failure is non-fatal — fall back to recency.
                    query_embedding = None

            entries = await self._store.search_async(
                query_embedding, self._top_k, ct=ct
            )
            if len(entries) == 0:
                return ""

            return self._format_entries(entries)
        except Exception:
            # RAG is strictly best-effort — never break inference.
            return ""

    def _format_entries(self, entries: list[EpisodicMemoryEntry]) -> str:
        # Half-budget per side, integer-divided to match the C# `_maxCharsPerEntry / 2`.
        half = self._max_chars_per_entry // 2
        sb = "[Relevant past exchanges — for context only]\n"

        for e in entries:
            user = _truncate(e.user_text, half)
            asst = _truncate(e.assistant_text, half)
            when = _format_when(e.recorded_at_utc) + " UTC"

            sb += "• [" + when + "] "
            if e.app_context is not None and len(e.app_context.strip()) > 0:
                sb += "(" + e.app_context + ") "
            sb += "User: " + user + "\n"
            sb += "  B!: " + asst + "\n"

        return sb


def _truncate(text: str, max_len: int) -> str:
    """Truncate to *max_len*, replacing the last kept char with an ellipsis (matches C#)."""
    if text is None or len(text) == 0:
        return ""
    if len(text) <= max_len:
        return text
    return text[: max_len - 1] + "…"


def _format_when(d) -> str:
    """Format a datetime as ``yyyy-MM-dd HH:mm`` in UTC (matches the C# ToString)."""
    from datetime import timezone

    # Normalise to UTC so the wall-clock fields match the C# UTC value.
    if d.tzinfo is not None:
        d = d.astimezone(timezone.utc)
    return (
        f"{d.year:04d}-{d.month:02d}-{d.day:02d} "
        f"{d.hour:02d}:{d.minute:02d}"
    )


# ─────────────────────────────────────────────────────────────────────────────
# RagPipelineBuilder — CircleAI.Memory.RagPipelineBuilder
# ─────────────────────────────────────────────────────────────────────────────


class RagPipelineBuilder:
    """Fluent builder for constructing a :class:`RagContextBuilder` with an
    episodic store, optional embedder, and tuning parameters.

    Example::

        rag = (
            RagPipelineBuilder.create()
            .with_in_memory_store()
            .with_top_k(10)
            .with_max_chars_per_entry(500)
            .build()
        )
        context = await rag.build_context_async("user query")
    """

    def __init__(self) -> None:
        self._store: Optional[IEpisodicMemoryStore] = None
        self._embedder: Optional[ITextEmbedder] = None
        self._top_k = 5
        self._max_chars_per_entry = 300

    @staticmethod
    def create() -> "RagPipelineBuilder":
        """Create a new :class:`RagPipelineBuilder` instance."""
        return RagPipelineBuilder()

    def with_store(self, store: IEpisodicMemoryStore) -> "RagPipelineBuilder":
        """Set the episodic memory store to retrieve past exchanges from."""
        if store is None:
            raise ValueError("store required")
        self._store = store
        return self

    def with_in_memory_store(self) -> "RagPipelineBuilder":
        """Convenience: create an :class:`InMemoryEpisodicStore` and use it.

        Suitable for tests and short-lived processes where persistence is not
        needed.
        """
        self._store = InMemoryEpisodicStore()
        return self

    def with_embedder(self, embedder: ITextEmbedder) -> "RagPipelineBuilder":
        """Set the text embedder for semantic similarity search. When not set,
        the builder falls back to recency-based retrieval.
        """
        if embedder is None:
            raise ValueError("embedder required")
        self._embedder = embedder
        return self

    def with_top_k(self, top_k: int) -> "RagPipelineBuilder":
        """Set the max number of relevant past episodes to include. Default 5, min 1."""
        if top_k < 1:
            raise ValueError("top_k must be at least 1.")
        self._top_k = top_k
        return self

    def with_max_chars_per_entry(self, max_chars: int) -> "RagPipelineBuilder":
        """Set the max characters taken from each episode's texts. Default 300, min 50."""
        if max_chars < 50:
            raise ValueError("max_chars must be at least 50.")
        self._max_chars_per_entry = max_chars
        return self

    def build(self) -> RagContextBuilder:
        """Build the :class:`RagContextBuilder` from the accumulated configuration."""
        if self._store is None:
            raise ValueError(
                "An episodic memory store is required. Call with_store() or "
                "with_in_memory_store() before build()."
            )
        return RagContextBuilder(
            self._store, self._embedder, self._top_k, self._max_chars_per_entry
        )
