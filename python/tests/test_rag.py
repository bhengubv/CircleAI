"""test_rag.py

Exercises RagContextBuilder + RagPipelineBuilder. Mirrors the TypeScript
rag.test.ts (and CircleAI.Tests.RagContextBuilderTests) plus the fluent-builder
surface and the embedder ranking path.
"""
from __future__ import annotations

import uuid
from datetime import datetime, timezone
from typing import Optional

import pytest

from circle_ai.memory.episodic_memory import EpisodicMemoryEntry
from circle_ai.memory.in_memory_episodic_store import InMemoryEpisodicStore
from circle_ai.memory.rag import (
    ITextEmbedder,
    RagContextBuilder,
    RagPipelineBuilder,
)


def _episodic(**overrides) -> EpisodicMemoryEntry:
    return EpisodicMemoryEntry(
        id=overrides.get("id", uuid.uuid4()),
        recorded_at_utc=overrides.get(
            "recorded_at_utc", datetime(2026, 6, 1, 12, 34, tzinfo=timezone.utc)
        ),
        user_text=overrides.get("user_text", "u"),
        assistant_text=overrides.get("assistant_text", "a"),
        app_context=overrides.get("app_context"),
        embedding=overrides.get("embedding"),
        tags=overrides.get("tags"),
    )


def _count_occurrences(text: str, token: str) -> int:
    count = 0
    start = 0
    while True:
        i = text.find(token, start)
        if i < 0:
            break
        count += 1
        start = i + len(token)
    return count


class _FixedEmbedder:
    """Embedder that maps any query to a fixed vector."""

    def __init__(self, vec: list[float]) -> None:
        self._vec = vec

    async def generate_async(
        self, text: str, *, ct: Optional[object] = None
    ) -> list[float]:
        return list(self._vec)


class _ThrowingEmbedder:
    async def generate_async(
        self, text: str, *, ct: Optional[object] = None
    ) -> list[float]:
        raise RuntimeError("embedder offline")


class _ThrowingEpisodicStore:
    """Store that always throws — used to test resilience."""

    async def add_async(self, *a, **k):
        raise RuntimeError("store failure")

    async def search_async(self, *a, **k):
        raise RuntimeError("store failure")

    async def get_recent_async(self, *a, **k):
        raise RuntimeError("store failure")

    async def count_async(self, *a, **k):
        raise RuntimeError("store failure")

    async def prune_older_than_async(self, *a, **k):
        raise RuntimeError("store failure")


# ══════════════════════════════════════════════════════════════════════════
# Constructor guards
# ══════════════════════════════════════════════════════════════════════════


def test_throws_when_store_is_none():
    with pytest.raises(ValueError):
        RagContextBuilder(None)  # type: ignore[arg-type]


# ══════════════════════════════════════════════════════════════════════════
# Empty / missing query
# ══════════════════════════════════════════════════════════════════════════


async def test_empty_query_returns_empty():
    b = RagContextBuilder(InMemoryEpisodicStore())
    assert await b.build_context_async("") == ""


async def test_whitespace_query_returns_empty():
    b = RagContextBuilder(InMemoryEpisodicStore())
    assert await b.build_context_async("   ") == ""


# ══════════════════════════════════════════════════════════════════════════
# Empty store
# ══════════════════════════════════════════════════════════════════════════


async def test_empty_store_returns_empty():
    b = RagContextBuilder(InMemoryEpisodicStore())
    assert await b.build_context_async("hello") == ""


# ══════════════════════════════════════════════════════════════════════════
# Formatting (recency fallback, no embedder)
# ══════════════════════════════════════════════════════════════════════════


async def test_returns_formatted_block_with_header_and_both_texts():
    store = InMemoryEpisodicStore()
    await store.add_async(
        _episodic(
            user_text="What is SDPKT?",
            assistant_text="SDPKT is the TGN wallet.",
            recorded_at_utc=datetime(2026, 6, 1, 11, 0, tzinfo=timezone.utc),
        )
    )

    b = RagContextBuilder(store, None, 3)
    result = await b.build_context_async("tell me about the wallet")

    assert result != ""
    assert "What is SDPKT?" in result
    assert "SDPKT is the TGN wallet." in result
    assert "[Relevant past exchanges" in result


async def test_formats_utc_timestamp_and_labels_user_and_b():
    store = InMemoryEpisodicStore()
    await store.add_async(
        _episodic(
            user_text="q",
            assistant_text="r",
            recorded_at_utc=datetime(2026, 6, 1, 9, 5, tzinfo=timezone.utc),
        )
    )
    b = RagContextBuilder(store, None, 1)
    result = await b.build_context_async("anything")
    assert "[2026-06-01 09:05 UTC]" in result
    assert "User: q" in result
    assert "B!: r" in result


async def test_respects_top_k_counts_bullet_prefixes():
    store = InMemoryEpisodicStore()
    for i in range(10):
        await store.add_async(
            _episodic(user_text=f"question {i}", assistant_text=f"answer {i}")
        )

    b = RagContextBuilder(store, None, 2)
    result = await b.build_context_async("any question")
    assert _count_occurrences(result, "• [") == 2


async def test_includes_app_context_when_set():
    store = InMemoryEpisodicStore()
    await store.add_async(
        _episodic(user_text="bid query", assistant_text="bid answer", app_context="tgn.bidbaas")
    )
    b = RagContextBuilder(store, None, 3)
    result = await b.build_context_async("bidding")
    assert "tgn.bidbaas" in result


async def test_truncates_long_texts_to_half_budget_with_ellipsis():
    store = InMemoryEpisodicStore()
    long_text = "x" * 500
    await store.add_async(_episodic(user_text=long_text, assistant_text="a"))
    # max_chars_per_entry 100 → half 50 → truncate to 49 chars + "…"
    b = RagContextBuilder(store, None, 1, 100)
    result = await b.build_context_async("q")
    assert ("x" * 49 + "…") in result
    assert ("x" * 51) not in result


# ══════════════════════════════════════════════════════════════════════════
# Embedder ranking path
# ══════════════════════════════════════════════════════════════════════════


async def test_ranks_by_embedding_when_embedder_supplied():
    store = InMemoryEpisodicStore()
    await store.add_async(_episodic(user_text="near", assistant_text="n", embedding=[1, 0]))
    await store.add_async(_episodic(user_text="far", assistant_text="f", embedding=[0, 1]))

    b = RagContextBuilder(store, _FixedEmbedder([1, 0]), 1)
    result = await b.build_context_async("anything")
    assert "near" in result
    assert "far" not in result


async def test_falls_back_to_recency_when_embedder_throws():
    store = InMemoryEpisodicStore()
    await store.add_async(
        _episodic(
            user_text="only",
            assistant_text="entry",
            recorded_at_utc=datetime(2026, 6, 1, tzinfo=timezone.utc),
        )
    )
    b = RagContextBuilder(store, _ThrowingEmbedder(), 3)
    result = await b.build_context_async("q")
    assert "only" in result


# ══════════════════════════════════════════════════════════════════════════
# Resilience — store throws
# ══════════════════════════════════════════════════════════════════════════


async def test_returns_empty_when_store_throws():
    b = RagContextBuilder(_ThrowingEpisodicStore())
    assert await b.build_context_async("query") == ""


# ══════════════════════════════════════════════════════════════════════════
# RagPipelineBuilder
# ══════════════════════════════════════════════════════════════════════════


async def test_builder_builds_from_in_memory_store_and_works():
    store = InMemoryEpisodicStore()
    await store.add_async(_episodic(user_text="hi", assistant_text="hello"))
    rag = (
        RagPipelineBuilder.create()
        .with_store(store)
        .with_top_k(2)
        .with_max_chars_per_entry(500)
        .build()
    )
    ctx = await rag.build_context_async("greeting")
    assert "hi" in ctx


async def test_builder_with_in_memory_store_wires_fresh_store():
    rag = RagPipelineBuilder.create().with_in_memory_store().build()
    assert await rag.build_context_async("nothing stored") == ""


def test_builder_build_without_store_throws():
    with pytest.raises(ValueError, match="(?i)episodic memory store is required"):
        RagPipelineBuilder.create().build()


def test_builder_with_top_k_rejects_below_1():
    with pytest.raises(ValueError):
        RagPipelineBuilder.create().with_top_k(0)


def test_builder_with_max_chars_rejects_below_50():
    with pytest.raises(ValueError):
        RagPipelineBuilder.create().with_max_chars_per_entry(49)


async def test_builder_with_embedder_wires_semantic_ranking():
    store = InMemoryEpisodicStore()
    await store.add_async(_episodic(user_text="near", assistant_text="n", embedding=[1, 0]))
    await store.add_async(_episodic(user_text="far", assistant_text="f", embedding=[0, 1]))
    rag = (
        RagPipelineBuilder.create()
        .with_store(store)
        .with_embedder(_FixedEmbedder([1, 0]))
        .with_top_k(1)
        .build()
    )
    ctx = await rag.build_context_async("q")
    assert "near" in ctx


def test_itextembedder_protocol_is_runtime_checkable():
    assert isinstance(_FixedEmbedder([1, 0]), ITextEmbedder)
