"""test_fused_recall.py

Verifies FusedRecall: Reciprocal Rank Fusion order, cross-source reinforcement,
cold-start degradation to episodic, the graph confidence gate, empty-query
short-circuit, and dedup by normalised text. Mirrors the TypeScript pilot
(fused_recall.test.ts) and Go port.
"""
from __future__ import annotations

import uuid
from datetime import datetime, timezone
from typing import Optional

from circle_ai.memory.episodic_memory import EpisodicMemoryEntry
from circle_ai.memory.graph import MemoryHit, MemoryItem
from circle_ai.memory.recall import FusedRecall


# ── Test doubles ──────────────────────────────────────────────────────────────


def _ep(id: str, user_text: str) -> EpisodicMemoryEntry:
    return EpisodicMemoryEntry(
        id=uuid.uuid5(uuid.NAMESPACE_OID, id),
        user_text=user_text,
        assistant_text="",
        recorded_at_utc=datetime(2026, 1, 1, tzinfo=timezone.utc),
    )


class FakeEpisodic:
    """Episodic store that returns a fixed, pre-ranked list from search_async."""

    def __init__(self, hits: list[EpisodicMemoryEntry]) -> None:
        self._hits = hits

    async def add_async(self, entry, *, ct=None) -> None:  # pragma: no cover
        pass

    async def search_async(self, query_embedding, top_k=5, *, ct=None):
        return self._hits[:top_k]

    async def get_recent_async(self, count=10, *, ct=None):  # pragma: no cover
        return self._hits[:count]

    async def count_async(self, *, ct=None) -> int:  # pragma: no cover
        return len(self._hits)

    async def prune_older_than_async(self, cutoff, *, ct=None) -> int:  # pragma: no cover
        return 0


class FakeHippo:
    """HippoRAG store that returns a fixed, pre-ranked list from multi_hop_recall_async."""

    backend_id = "fake-hippo"

    def __init__(self, hits: list[MemoryHit]) -> None:
        self._hits = hits

    async def index_async(self, item, *, ct=None) -> None:  # pragma: no cover
        pass

    async def multi_hop_recall_async(self, query, top_k=5, *, ct=None):
        return self._hits[:top_k]


class ThrowingHippo:
    backend_id = "boom"

    async def index_async(self, item, *, ct=None) -> None:  # pragma: no cover
        pass

    async def multi_hop_recall_async(self, query, top_k=5, *, ct=None):
        raise RuntimeError("graph unavailable")


def _graph_hit(id: str, text: str, confidence: Optional[str] = None) -> MemoryHit:
    metadata = None if confidence is None else {"confidence": confidence}
    return MemoryHit(item=MemoryItem(id=id, text=text, metadata=metadata), score=1.0)


# ── RRF ordering ──────────────────────────────────────────────────────────────


async def test_memory_in_both_sources_outranks_one_source() -> None:
    episodic = FakeEpisodic([_ep("a", "A"), _ep("b", "B"), _ep("c", "C")])
    graph = FakeHippo([_graph_hit("g", "B")])  # reinforces B
    recall = FusedRecall(episodic, graph)

    hits = await recall.recall_async("q", None, 5)
    assert [h.item.text for h in hits] == ["B", "A", "C"]


async def test_cold_start_no_graph_yields_episodic_order() -> None:
    episodic = FakeEpisodic([_ep("a", "A"), _ep("b", "B"), _ep("c", "C")])
    recall = FusedRecall(episodic, None)

    hits = await recall.recall_async("q", None, 5)
    assert [h.item.text for h in hits] == ["A", "B", "C"]


async def test_respects_top_k() -> None:
    episodic = FakeEpisodic([_ep("a", "A"), _ep("b", "B"), _ep("c", "C")])
    recall = FusedRecall(episodic, None)

    hits = await recall.recall_async("q", None, 2)
    assert len(hits) == 2
    assert [h.item.text for h in hits] == ["A", "B"]


# ── integrity gates ───────────────────────────────────────────────────────────


async def test_drops_graph_hits_below_confidence_threshold() -> None:
    episodic = FakeEpisodic([])
    graph = FakeHippo([_graph_hit("low", "LOW", "0.2"), _graph_hit("high", "HIGH", "0.9")])
    recall = FusedRecall(episodic, graph)

    hits = await recall.recall_async("q", None, 5)
    texts = [h.item.text for h in hits]
    assert "LOW" not in texts, "below-threshold hit must be dropped"
    assert "HIGH" in texts


async def test_keeps_graph_hits_without_confidence_metadata() -> None:
    episodic = FakeEpisodic([])
    graph = FakeHippo([_graph_hit("g", "NOCONF")])
    recall = FusedRecall(episodic, graph)

    hits = await recall.recall_async("q", None, 5)
    assert [h.item.text for h in hits] == ["NOCONF"]


async def test_skips_graph_entirely_for_empty_or_whitespace_query() -> None:
    episodic = FakeEpisodic([_ep("a", "A")])
    graph = FakeHippo([_graph_hit("g", "GRAPH")])
    recall = FusedRecall(episodic, graph)

    hits = await recall.recall_async("   ", None, 5)
    texts = [h.item.text for h in hits]
    assert texts == ["A"]
    assert "GRAPH" not in texts


async def test_degrades_to_episodic_when_the_graph_throws() -> None:
    episodic = FakeEpisodic([_ep("a", "A")])
    recall = FusedRecall(episodic, ThrowingHippo())

    hits = await recall.recall_async("q", None, 5)
    assert [h.item.text for h in hits] == ["A"]


# ── dedup ─────────────────────────────────────────────────────────────────────


async def test_fuses_two_hits_with_same_normalised_text_into_one() -> None:
    episodic = FakeEpisodic([_ep("a", "Durban  Weather")])
    graph = FakeHippo([_graph_hit("g", "durban weather")])  # same key
    recall = FusedRecall(episodic, graph)

    hits = await recall.recall_async("q", None, 5)
    assert len(hits) == 1
