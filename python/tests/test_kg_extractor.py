"""test_kg_extractor.py

Verifies HeuristicKnowledgeGraphExtractor: bidirectional mentions/seenin triples
on content words, stop-word + short-word filtering, dedup, and the memory-id
fallback to user_text when no episode id is given. Mirrors the TypeScript pilot
(kg_extractor.test.ts) and Go port.
"""
from __future__ import annotations

from circle_ai.memory.extractor import HeuristicKnowledgeGraphExtractor

ex = HeuristicKnowledgeGraphExtractor()


async def test_emits_two_way_link_per_content_word_keyed_by_episode_id() -> None:
    triples = await ex.extract_from_turn_async("Durban weather is sunny", "", "ep1")

    # content words: durban, weather, sunny  ("is" is a short stop word)
    assert len(triples) == 6

    def has(s: str, p: str, o: str) -> bool:
        return any(t.subject == s and t.predicate == p and t.object == o for t in triples)

    assert has("ep1", "mentions", "durban")
    assert has("durban", "seenin", "ep1")
    assert has("ep1", "mentions", "weather")
    assert has("ep1", "mentions", "sunny")


async def test_drops_stop_words_and_words_shorter_than_three_chars() -> None:
    triples = await ex.extract_from_turn_async("I am at the shop", "", "ep2")
    objects = [t.object for t in triples if t.predicate == "mentions"]
    # "i","am","at","the" are all stop/short; only "shop" survives.
    assert objects == ["shop"]


async def test_dedupes_a_repeated_word() -> None:
    triples = await ex.extract_from_turn_async("test test test", "", "ep3")
    assert len(triples) == 2  # one mentions + one seenin for "test"


async def test_includes_assistant_side_content_words() -> None:
    triples = await ex.extract_from_turn_async("tell me about", "Johannesburg traffic", "ep4")
    objects = sorted(t.object for t in triples if t.predicate == "mentions")
    assert objects == ["johannesburg", "tell", "traffic"]


async def test_falls_back_to_user_text_as_memory_id_when_no_episode_id() -> None:
    triples = await ex.extract_from_turn_async("hello world", "", None)
    assert any(t.subject == "hello world" and t.predicate == "mentions" for t in triples)


async def test_returns_nothing_for_an_empty_turn() -> None:
    assert await ex.extract_from_turn_async("", "", None) == []


async def test_tags_every_triple_with_source_and_default_confidence() -> None:
    triples = await ex.extract_from_turn_async("coffee", "", "ep5")
    assert len(triples) > 0
    for t in triples:
        assert t.source == "ep5"
        assert t.confidence == 0.6
