"""test_knowledge_graph.py

Verifies InMemoryKnowledgeGraph (triples + nodes) and InMemoryHippoRagStore
(Personalised PageRank multi-hop recall) — including the three precision
guarantees: no-seed->empty, seeds excluded from results, confidence-weighting.
Mirrors the TypeScript pilot (knowledge_graph.test.ts) and Go port.
"""
from __future__ import annotations

import pytest

from circle_ai.memory.graph import (
    InMemoryHippoRagStore,
    InMemoryKnowledgeGraph,
    KnowledgeNode,
    MemoryItem,
)


# ── InMemoryKnowledgeGraph ────────────────────────────────────────────────────


def test_stores_and_returns_triples() -> None:
    kg = InMemoryKnowledgeGraph()
    kg.add_triple("a", "rel", "b", "ep1", 1.0)
    all_t = kg.all_triples()
    assert len(all_t) == 1
    assert all_t[0].subject == "a"
    assert all_t[0].object == "b"
    assert all_t[0].confidence == 1.0


def test_replaces_a_triple_with_same_spo() -> None:
    kg = InMemoryKnowledgeGraph()
    kg.add_triple("a", "rel", "b", "ep1", 0.5)
    kg.add_triple("a", "rel", "b", "ep2", 0.9)
    all_t = kg.all_triples()
    assert len(all_t) == 1
    assert all_t[0].confidence == 0.9
    assert all_t[0].source == "ep2"


def test_upserts_and_fetches_nodes() -> None:
    kg = InMemoryKnowledgeGraph()
    kg.upsert_node(KnowledgeNode(id="heart", kind="organ", name="the heart"))
    node = kg.get_node("heart")
    assert node is not None and node.name == "the heart"
    assert kg.get_node("missing") is None


def test_rejects_out_of_range_confidence() -> None:
    kg = InMemoryKnowledgeGraph()
    with pytest.raises(ValueError):
        kg.add_triple("a", "r", "b", None, 1.5)


# ── InMemoryHippoRagStore — multi-hop recall ──────────────────────────────────


async def test_reaches_associated_nodes_across_hops_and_excludes_seed() -> None:
    # chest -> heart -> father_cardiac_event
    kg = InMemoryKnowledgeGraph()
    kg.add_triple("chest", "relates", "heart", "ep1", 1.0)
    kg.add_triple("heart", "relates", "father_cardiac_event", "ep2", 1.0)
    hippo = InMemoryHippoRagStore(kg)

    hits = await hippo.multi_hop_recall_async("chest tightness", 5)
    ids = [h.item.id for h in hits]

    assert "chest" not in ids, "seed node must be excluded"
    assert "heart" in ids, "one-hop node should be recalled"
    assert "father_cardiac_event" in ids, "two-hop node should be recalled"

    # One hop carries more PPR mass than two hops.
    heart = next(h for h in hits if h.item.id == "heart")
    father = next(h for h in hits if h.item.id == "father_cardiac_event")
    assert heart.score >= father.score


async def test_returns_empty_when_no_query_term_touches_the_graph() -> None:
    kg = InMemoryKnowledgeGraph()
    kg.add_triple("chest", "relates", "heart", "ep1", 1.0)
    hippo = InMemoryHippoRagStore(kg)

    hits = await hippo.multi_hop_recall_async("banana apple", 5)
    assert len(hits) == 0


async def test_returns_empty_on_an_empty_graph() -> None:
    hippo = InMemoryHippoRagStore(InMemoryKnowledgeGraph())
    hits = await hippo.multi_hop_recall_async("anything", 5)
    assert len(hits) == 0


async def test_confidence_weights_edge_spread_stated_fact_outranks_guess() -> None:
    # root -> alpha (stated, 1.0) and root -> beta (guessed, 0.1)
    kg = InMemoryKnowledgeGraph()
    kg.add_triple("root", "r", "alpha", "ep1", 1.0)
    kg.add_triple("root", "r", "beta", "ep2", 0.1)
    hippo = InMemoryHippoRagStore(kg)

    hits = await hippo.multi_hop_recall_async("root", 5)
    ids = [h.item.id for h in hits]
    assert "root" not in ids, "seed excluded"
    assert hits[0].item.id == "alpha"
    assert hits[1].item.id == "beta"
    assert hits[0].score > hits[1].score


async def test_uses_node_name_as_recall_text_when_present() -> None:
    kg = InMemoryKnowledgeGraph()
    kg.add_triple("chest", "relates", "heart", "ep1", 1.0)
    kg.upsert_node(KnowledgeNode(id="heart", kind="organ", name="the heart"))
    hippo = InMemoryHippoRagStore(kg)

    hits = await hippo.multi_hop_recall_async("chest", 5)
    heart = next(h for h in hits if h.item.id == "heart")
    assert heart.item.text == "the heart"


async def test_index_async_registers_item_and_metadata_as_triples() -> None:
    kg = InMemoryKnowledgeGraph()
    hippo = InMemoryHippoRagStore(kg)
    await hippo.index_async(
        MemoryItem(id="note1", text="durban weather", metadata={"topic": "durban"})
    )

    preds = sorted(t.predicate for t in kg.read_triples("note1"))
    assert preds == ["memory_text", "topic"]


async def test_recalls_a_memory_node_reached_from_a_query_term_seed() -> None:
    # Extractor-style reverse edge: the term "durban" points to the memory that
    # mentions it, so a forward walk from the seed reaches the memory node.
    kg = InMemoryKnowledgeGraph()
    kg.add_triple("durban", "seenin", "note1", "ep1", 1.0)
    kg.upsert_node(KnowledgeNode(id="note1", kind="memory", name="durban weather"))
    hippo = InMemoryHippoRagStore(kg)

    hits = await hippo.multi_hop_recall_async("durban", 5)
    ids = [h.item.id for h in hits]
    assert "durban" not in ids, "seed excluded"
    assert "note1" in ids
    assert next(h for h in hits if h.item.id == "note1").item.text == "durban weather"
