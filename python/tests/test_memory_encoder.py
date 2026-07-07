"""test_memory_encoder.py

Verifies CompanionMemoryEncoder end-to-end: a turn handed to the background
encoder fills the knowledge graph so associative recall can later reach the
episode; attributed beliefs are formed off the hot path (a third party's fact
never becomes the user's); the queue drops rather than blocks when full;
close_async drains remaining work; and an extractor failure is captured, not
fatal. Mirrors the TypeScript pilot (memory_encoder.test.ts) and Go port.
"""
from __future__ import annotations

from typing import Optional

from circle_ai.companion.belief import HeuristicBeliefExtractor, SelfBeliefStore
from circle_ai.companion.memory_encoder import CompanionMemoryEncoder
from circle_ai.memory.extractor import HeuristicKnowledgeGraphExtractor
from circle_ai.memory.graph import (
    InMemoryHippoRagStore,
    InMemoryKnowledgeGraph,
    KnowledgeTriple,
)


class ThrowingExtractor:
    async def extract_from_turn_async(
        self, user_text, assistant_text, source_episode_id, *, ct=None
    ) -> list[KnowledgeTriple]:
        raise RuntimeError("boom")


# ── end-to-end ────────────────────────────────────────────────────────────────


async def test_encodes_a_turn_so_recall_reaches_the_episode_by_a_content_word() -> None:
    graph = InMemoryKnowledgeGraph()
    enc = CompanionMemoryEncoder(HeuristicKnowledgeGraphExtractor(), graph)

    enc.enqueue("I love hiking in Drakensberg", "Sounds wonderful", "ep-hike")
    await enc.close_async()

    assert len(graph.all_triples()) > 0, "graph should have filled from the turn"

    hippo = InMemoryHippoRagStore(graph)
    hits = await hippo.multi_hop_recall_async("drakensberg", 5)
    episode = next((h for h in hits if h.item.id == "ep-hike"), None)
    assert episode is not None, "recall should reach the episode via the extracted edges"
    assert episode.item.text == "I love hiking in Drakensberg"


async def test_forms_attributed_beliefs_off_hot_path_mother_never_user_fact() -> None:
    graph = InMemoryKnowledgeGraph()
    beliefs = SelfBeliefStore()
    enc = CompanionMemoryEncoder(
        HeuristicKnowledgeGraphExtractor(),
        graph,
        HeuristicBeliefExtractor(),
        beliefs,
    )

    enc.enqueue("my mother is diabetic", "Noted", "ep1")
    enc.enqueue("i am vegetarian", "Got it", "ep2")
    await enc.close_async()

    facts = beliefs.self_facts()
    assert not any("diabetic" in f.object for f in facts), (
        "mother's condition must never be a user fact"
    )
    assert any(f.object == "vegetarian" for f in facts)
    assert any(b.object == "diabetic" for b in beliefs.non_self()), (
        "it is still remembered as an audit fact"
    )


# ── queue behaviour ───────────────────────────────────────────────────────────


async def test_drops_writes_beyond_capacity_rather_than_blocking() -> None:
    graph = InMemoryKnowledgeGraph()
    enc = CompanionMemoryEncoder(
        HeuristicKnowledgeGraphExtractor(), graph, None, None, 2
    )

    # Enqueued synchronously before the drain resumes: the 3rd overflows a
    # capacity-2 queue and is dropped.
    enc.enqueue("alpha", "", "e1")
    enc.enqueue("bravo", "", "e2")
    enc.enqueue("charlie", "", "e3")
    await enc.close_async()

    assert graph.get_node("e1") is not None
    assert graph.get_node("e2") is not None
    assert graph.get_node("e3") is None, "the overflow write should have been dropped"


async def test_ignores_an_enqueue_with_a_blank_episode_id() -> None:
    graph = InMemoryKnowledgeGraph()
    enc = CompanionMemoryEncoder(HeuristicKnowledgeGraphExtractor(), graph)
    enc.enqueue("hello", "", "")
    enc.enqueue("hello", "", "   ")
    await enc.close_async()
    assert len(graph.all_triples()) == 0


async def test_captures_an_extractor_failure_without_crashing_the_drain() -> None:
    graph = InMemoryKnowledgeGraph()
    enc = CompanionMemoryEncoder(ThrowingExtractor(), graph)
    enc.enqueue("x", "", "e1")
    await enc.close_async()

    assert isinstance(enc.last_error, Exception)
    assert str(enc.last_error) == "boom"
    # The node was upserted before the extractor ran, so it survives.
    assert graph.get_node("e1") is not None
