# memory/recall.py
#
# Fused associative recall (Reciprocal Rank Fusion). Ported from
# CircleAI.Companion (IRecall, FusedRecall) — the C# reference — and mirrors the
# TypeScript pilot (memory/recall.ts) and Go port (memory_recall.go).
#
# Fuses two memory systems with incomparable score spaces — episodic cosine
# similarity and graph association (Personalised PageRank) — into one ranked
# context. RRF combines ranked lists by *position*, so it needs no shared score
# scale: each source contributes 1 / (k + rank).
#
# Cold-start is automatic: a new user has an empty graph, so only episodic
# contributes and the fused order equals the episodic order — no special case.

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional, Protocol, runtime_checkable

from .episodic_memory import EpisodicMemoryEntry
from .graph import IHippoRagStore, MemoryHit, MemoryItem
from .stores import IEpisodicMemoryStore


@runtime_checkable
class IRecall(Protocol):
    """Unified memory recall — the most relevant memories for a turn."""

    async def recall_async(
        self,
        query: str,
        query_embedding: Optional[list[float]],
        top_k: int = 5,
        *,
        ct: Optional[object] = None,
    ) -> list[MemoryHit]:
        """Recall the top-k most relevant memories for the current turn.

        ``query`` drives graph association; ``query_embedding`` drives episodic
        cosine similarity (may be ``None`` -> episodic recency fallback).
        """
        ...


@dataclass(frozen=True)
class FusedRecallOptions:
    """Tuning for :class:`FusedRecall`."""

    # Candidates pulled from each source before fusion. Default 20.
    candidate_pool_size: int = 20
    # RRF damping constant k. Default 60 (the standard value).
    rrf_k: int = 60
    # Graph hits whose backing confidence (metadata key "confidence") is below
    # this are dropped. Applied only when a hit actually carries a confidence
    # value. Default 0.4.
    graph_confidence_threshold: float = 0.4


class FusedRecall:
    """Reciprocal-Rank-Fusion recall over episodic similarity + graph association."""

    def __init__(
        self,
        episodic: IEpisodicMemoryStore,
        graph: Optional[IHippoRagStore] = None,
        options: Optional[FusedRecallOptions] = None,
    ) -> None:
        if episodic is None:
            raise ValueError("episodic required")
        self._episodic = episodic
        self._graph = graph
        self._opts = options or FusedRecallOptions()

    async def recall_async(
        self,
        query: str,
        query_embedding: Optional[list[float]],
        top_k: int = 5,
        *,
        ct: Optional[object] = None,
    ) -> list[MemoryHit]:
        if top_k <= 0:
            raise ValueError("top_k must be positive")

        pool = self._opts.candidate_pool_size

        # Fast path: episodic similarity (or recency when the embedding is null).
        episodic = await self._episodic.search_async(query_embedding, pool, ct=ct)

        # Slow path: graph association. Optional and best-effort — a missing,
        # empty, or failing graph degrades to pure episodic, never throws. An
        # empty query cannot seed a graph walk, so skip it.
        graph: list[MemoryHit] = []
        if self._graph is not None and query is not None and len(query.strip()) > 0:
            try:
                graph = list(
                    await self._graph.multi_hop_recall_async(query, pool, ct=ct)
                )
            except Exception:
                graph = []

        # Reciprocal Rank Fusion: accumulate 1 / (k + rank) per candidate across
        # both ranked lists, keyed by normalised text so a memory surfaced by
        # both sources reinforces rather than duplicates.
        k = self._opts.rrf_k
        fused: dict[str, _FusedCandidate] = {}

        def accumulate(item: MemoryItem, one_based_rank: int) -> None:
            key = _normalise_key(item.text)
            if len(key) == 0:
                return
            contribution = 1.0 / (k + one_based_rank)
            existing = fused.get(key)
            if existing is not None:
                existing.score += contribution
            else:
                fused[key] = _FusedCandidate(item, contribution)

        for i, e in enumerate(episodic):
            accumulate(_adapt_episodic(e), i + 1)

        for i, hit in enumerate(graph):
            if _is_below_confidence(hit, self._opts.graph_confidence_threshold):
                continue
            accumulate(hit.item, i + 1)

        ordered = sorted(fused.values(), key=lambda c: c.score, reverse=True)
        return [MemoryHit(item=c.item, score=c.score) for c in ordered[:top_k]]


@dataclass
class _FusedCandidate:
    item: MemoryItem
    score: float


def _is_below_confidence(hit: MemoryHit, threshold: float) -> bool:
    meta = hit.item.metadata
    if not meta:
        return False
    raw = meta.get("confidence")
    if raw is None:
        return False
    try:
        c = float(raw)
    except (TypeError, ValueError):
        return False
    # NaN/inf: Python float() parses "inf"/"nan"; only finite < threshold gates.
    if c != c or c in (float("inf"), float("-inf")):
        return False
    return c < threshold


def _adapt_episodic(e: EpisodicMemoryEntry) -> MemoryItem:
    meta: dict[str, str] = {
        "source": "episodic",
        "recordedAt": e.recorded_at_utc.isoformat(),
    }
    if e.assistant_text:
        meta["assistantText"] = e.assistant_text
    if e.app_context:
        meta["appContext"] = e.app_context
    return MemoryItem(id=str(e.id), text=e.user_text, metadata=meta)


def _normalise_key(text: Optional[str]) -> str:
    """Lowercase + collapse internal whitespace so equivalent texts fuse to one key."""
    if not text or len(text.strip()) == 0:
        return ""
    out: list[str] = []
    prev_space = False
    for ch in text.strip():
        if ch.isspace():
            if not prev_space:
                out.append(" ")
                prev_space = True
        else:
            out.append(ch.lower())
            prev_space = False
    return "".join(out)
