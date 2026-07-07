"""test_companion_session.py

Verifies the concrete CompanionSession end-to-end: a turn recalls fused memory +
the user's own facts into the system prompt, calls the generator, persists the
exchange, hands it to the background encoder, recalls it on a later turn, and
streams. Mirrors the TypeScript pilot (companion_session.test.ts) and Go port.
"""
from __future__ import annotations

import uuid
from datetime import datetime, timezone
from typing import AsyncGenerator, Optional

from circle_ai.companion.belief import HeuristicBeliefExtractor, SelfBeliefStore
from circle_ai.companion.companion_types import InterfaceKind
from circle_ai.companion.memory_encoder import CompanionMemoryEncoder
from circle_ai.companion.session import CompanionSession, CompanionSessionOptions
from circle_ai.memory.extractor import HeuristicKnowledgeGraphExtractor
from circle_ai.memory.graph import InMemoryKnowledgeGraph
from circle_ai.memory.episodic_memory import EpisodicMemoryEntry
from circle_ai.memory.in_memory_episodic_store import InMemoryEpisodicStore
from circle_ai.memory.recall import FusedRecall
from circle_ai.models.models import ChatMessage


class CapturingGenerator:
    """Records the prompt it was handed and returns a canned reply / chunks."""

    def __init__(self, reply: str, chunks: Optional[list[str]] = None) -> None:
        self._reply = reply
        self._chunks = chunks
        self.last_messages: list[ChatMessage] = []

    async def generate_async(
        self, messages: list[ChatMessage], options=None
    ) -> str:
        self.last_messages = list(messages)
        return self._reply

    async def stream_async(
        self, messages: list[ChatMessage], options=None
    ) -> AsyncGenerator[str, None]:
        self.last_messages = list(messages)
        for c in self._chunks if self._chunks is not None else [self._reply]:
            yield c


async def _record_self_fact(beliefs: SelfBeliefStore, text: str) -> None:
    bx = HeuristicBeliefExtractor()
    for b in await bx.extract_async(text, "t0"):
        beliefs.record(b)


def _make_session(
    generator,
    episodic: InMemoryEpisodicStore,
    *,
    beliefs: Optional[SelfBeliefStore] = None,
    encoder: Optional[CompanionMemoryEncoder] = None,
) -> CompanionSession:
    recall = FusedRecall(episodic, None)
    return CompanionSession(
        generator,
        episodic,
        recall,
        CompanionSessionOptions(
            session_id="s1",
            identity_id="u1",
            interface=InterfaceKind.MOBILE,
            beliefs=beliefs,
            encoder=encoder,
        ),
    )


def _seed_entry(id: str, user_text: str, assistant_text: str) -> EpisodicMemoryEntry:
    return EpisodicMemoryEntry(
        id=uuid.uuid5(uuid.NAMESPACE_OID, id),
        recorded_at_utc=datetime(2026, 1, 1, tzinfo=timezone.utc),
        user_text=user_text,
        assistant_text=assistant_text,
    )


# ── send path ─────────────────────────────────────────────────────────────────


async def test_injects_recalled_memories_and_user_facts_into_system_prompt() -> None:
    episodic = InMemoryEpisodicStore()
    await episodic.add_async(
        _seed_entry("seed1", "I have a peanut allergy", "Noted")
    )
    beliefs = SelfBeliefStore()
    await _record_self_fact(beliefs, "i am vegetarian")

    gen = CapturingGenerator("Here are some options")
    session = _make_session(gen, episodic, beliefs=beliefs)

    reply = await session.send_async("what can I eat?")
    assert reply == "Here are some options"

    system = gen.last_messages[0]
    assert system.role == "system"
    assert "peanut allergy" in system.content, "recalled memory should be in the prompt"
    assert "vegetarian" in system.content, "user fact should be in the prompt"

    # The user's actual message is the last turn handed to the generator.
    assert gen.last_messages[-1].content == "what can I eat?"


async def test_persists_the_turn_and_grows_the_history() -> None:
    episodic = InMemoryEpisodicStore()
    session = _make_session(CapturingGenerator("ok"), episodic)

    await session.send_async("hello")
    assert await episodic.count_async() == 1
    assert len(session.history) == 2  # user + assistant
    assert session.history[0].role == "user"
    assert session.history[1].role == "assistant"


async def test_recalls_a_prior_turn_on_a_later_turn() -> None:
    episodic = InMemoryEpisodicStore()
    gen = CapturingGenerator("noted")
    session = _make_session(gen, episodic)

    await session.send_async("my favourite colour is blue")
    await session.send_async("what's my favourite colour?")

    system = gen.last_messages[0]
    assert "favourite colour is blue" in system.content, "the earlier turn should be recalled"


async def test_hands_the_turn_to_the_background_encoder_filling_the_graph() -> None:
    episodic = InMemoryEpisodicStore()
    graph = InMemoryKnowledgeGraph()
    encoder = CompanionMemoryEncoder(HeuristicKnowledgeGraphExtractor(), graph)
    session = _make_session(CapturingGenerator("ok"), episodic, encoder=encoder)

    await session.send_async("remember my dentist appointment")
    await encoder.close_async()

    assert any(t.object == "dentist" for t in graph.all_triples()), (
        "the encoder should have extracted the turn into the graph"
    )


# ── stream + context ──────────────────────────────────────────────────────────


async def test_streams_chunks_and_still_persists_the_full_reply() -> None:
    episodic = InMemoryEpisodicStore()
    gen = CapturingGenerator("unused", ["Hel", "lo"])
    session = _make_session(gen, episodic)

    chunks: list[str] = []
    async for c in session.stream_async("hi"):
        chunks.append(c)

    assert chunks == ["Hel", "lo"]
    assert await episodic.count_async() == 1
    assert session.history[1].content == "Hello"  # accumulated reply persisted


async def test_get_context_reflects_memories_recalled_on_last_turn() -> None:
    episodic = InMemoryEpisodicStore()
    await episodic.add_async(_seed_entry("seed1", "I live in Durban", "Nice"))
    session = _make_session(CapturingGenerator("ok"), episodic)

    await session.send_async("where do I live?")
    assert "I live in Durban" in session.get_context().recent_memory_snippets


async def test_agent_async_returns_a_reply_and_persists() -> None:
    episodic = InMemoryEpisodicStore()
    session = _make_session(CapturingGenerator("done"), episodic)
    reply = await session.agent_async("do the thing")
    assert reply == "done"
    assert await episodic.count_async() == 1
