"""test_llm_extractor.py

Verifies LlmKnowledgeGraphExtractor: parses a clean JSON array of triples,
tolerates prose/markdown-fence-wrapped JSON, defaults confidence when "c" is
missing/invalid, clamps out-of-range confidence, skips objects with blank
s/p/o, skips non-object array entries, and returns [] on garbage / on an empty
turn / on a failing generator. Mirrors the TypeScript reference
(llm_extractor.test.ts) and is faithful to the C# LlmKnowledgeGraphExtractor.
"""
from __future__ import annotations

from datetime import datetime
from typing import Optional

from circle_ai.memory.llm_extractor import LlmKnowledgeGraphExtractor
from circle_ai.models.models import ChatMessage


class FakeChatGenerator:
    """Minimal fake IChatGenerator that returns a canned reply, records messages."""

    def __init__(self, reply: str) -> None:
        self._reply = reply
        self.last_messages: list[ChatMessage] = []

    async def generate_async(
        self, messages: list[ChatMessage], options: Optional[object] = None
    ) -> str:
        self.last_messages = messages
        return self._reply

    async def stream_async(self, messages, options=None):  # pragma: no cover
        yield self._reply


class ThrowingChatGenerator:
    """A generator that always throws — exercises the graceful-degradation path."""

    async def generate_async(
        self, messages: list[ChatMessage], options: Optional[object] = None
    ) -> str:
        raise RuntimeError("model offline")

    async def stream_async(self, messages, options=None):  # pragma: no cover
        if False:
            yield ""


# ── clean JSON ───────────────────────────────────────────────────────────────


async def test_parses_a_plain_json_array_of_triples() -> None:
    gen = FakeChatGenerator(
        '[{"s":"Tony","p":"has_daughter","o":"Alex","c":0.9},'
        '{"s":"Alex","p":"lives_in","o":"Durban","c":0.5}]'
    )
    ex = LlmKnowledgeGraphExtractor(gen)
    triples = await ex.extract_from_turn_async("hi", "ok", "ep1")

    assert len(triples) == 2
    assert triples[0].subject == "Tony"
    assert triples[0].predicate == "has_daughter"
    assert triples[0].object == "Alex"
    assert triples[0].confidence == 0.9
    assert triples[0].source == "ep1"
    assert isinstance(triples[0].recorded_at_utc, datetime)
    assert triples[1].object == "Durban"
    assert triples[1].confidence == 0.5


async def test_sends_verbatim_system_prompt_and_framed_user_message() -> None:
    gen = FakeChatGenerator("[]")
    ex = LlmKnowledgeGraphExtractor(gen)
    await ex.extract_from_turn_async("the weather", "is sunny", "ep1")

    assert len(gen.last_messages) == 2
    assert gen.last_messages[0].role == "system"
    assert gen.last_messages[0].content.startswith("You are a knowledge-graph extractor.")
    assert gen.last_messages[1].role == "user"
    assert gen.last_messages[1].content == "USER:\nthe weather\nASSISTANT:\nis sunny\n"


# ── defensive parsing ────────────────────────────────────────────────────────


async def test_extracts_json_embedded_in_prose_or_markdown_fences() -> None:
    gen = FakeChatGenerator(
        "Sure! Here are the triples:\n```json\n"
        '[{"s":"Paris","p":"capital_of","o":"France","c":0.95}]\n'
        "```\nHope that helps."
    )
    ex = LlmKnowledgeGraphExtractor(gen)
    triples = await ex.extract_from_turn_async("u", "a", "ep2")

    assert len(triples) == 1
    assert triples[0].subject == "Paris"
    assert triples[0].predicate == "capital_of"
    assert triples[0].object == "France"
    assert triples[0].confidence == 0.95


async def test_defaults_confidence_to_075_when_c_missing() -> None:
    gen = FakeChatGenerator('[{"s":"a","p":"b","o":"c"}]')
    ex = LlmKnowledgeGraphExtractor(gen)
    triples = await ex.extract_from_turn_async("u", "a", "ep3")
    assert len(triples) == 1
    assert triples[0].confidence == 0.75


async def test_defaults_confidence_to_075_when_c_non_numeric() -> None:
    gen = FakeChatGenerator('[{"s":"a","p":"b","o":"c","c":"high"}]')
    ex = LlmKnowledgeGraphExtractor(gen)
    triples = await ex.extract_from_turn_async("u", "a", "ep3")
    assert triples[0].confidence == 0.75


async def test_clamps_confidence_into_0_1() -> None:
    gen = FakeChatGenerator(
        '[{"s":"a","p":"b","o":"c","c":5},{"s":"d","p":"e","o":"f","c":-2}]'
    )
    ex = LlmKnowledgeGraphExtractor(gen)
    triples = await ex.extract_from_turn_async("u", "a", "ep3")
    assert triples[0].confidence == 1
    assert triples[1].confidence == 0


async def test_skips_objects_whose_spo_are_blank_or_missing() -> None:
    gen = FakeChatGenerator(
        '[{"s":"","p":"b","o":"c"},{"s":"a","p":"  ","o":"c"},'
        '{"s":"a","p":"b"},{"s":"keep","p":"p","o":"o"}]'
    )
    ex = LlmKnowledgeGraphExtractor(gen)
    triples = await ex.extract_from_turn_async("u", "a", "ep3")
    assert len(triples) == 1
    assert triples[0].subject == "keep"


async def test_skips_non_object_array_entries() -> None:
    gen = FakeChatGenerator('[1, "two", null, {"s":"a","p":"b","o":"c"}]')
    ex = LlmKnowledgeGraphExtractor(gen)
    triples = await ex.extract_from_turn_async("u", "a", "ep3")
    assert len(triples) == 1
    assert triples[0].subject == "a"


# ── empty results ────────────────────────────────────────────────────────────


async def test_returns_empty_on_pure_garbage_no_brackets() -> None:
    gen = FakeChatGenerator("I could not find any facts, sorry.")
    ex = LlmKnowledgeGraphExtractor(gen)
    assert await ex.extract_from_turn_async("u", "a", "ep4") == []


async def test_returns_empty_on_malformed_json_inside_brackets() -> None:
    gen = FakeChatGenerator('[{"s":"a", "p": }]')
    ex = LlmKnowledgeGraphExtractor(gen)
    assert await ex.extract_from_turn_async("u", "a", "ep4") == []


async def test_returns_empty_when_json_is_object_not_array() -> None:
    gen = FakeChatGenerator('{"s":"a","p":"b","o":"c"}')
    ex = LlmKnowledgeGraphExtractor(gen)
    # No '[' before ']' — object braces only, so no valid slice.
    assert await ex.extract_from_turn_async("u", "a", "ep4") == []


async def test_returns_empty_when_both_texts_blank_no_llm_call() -> None:
    gen = FakeChatGenerator('[{"s":"a","p":"b","o":"c"}]')
    ex = LlmKnowledgeGraphExtractor(gen)
    assert await ex.extract_from_turn_async("   ", "", None) == []
    # The generator was never asked.
    assert gen.last_messages == []


async def test_returns_empty_when_generator_throws() -> None:
    ex = LlmKnowledgeGraphExtractor(ThrowingChatGenerator())
    assert await ex.extract_from_turn_async("u", "a", "ep5") == []
