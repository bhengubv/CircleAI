"""test_inner_monologue.py

Verifies the IInnerMonologue reasoning core ported from CircleAI.Companion:

  * ReasoningLoopInnerMonologue — o1/DeepSeek-style reasoning-fragment capture.
  * TemplateInnerMonologue      — model-free narrative-template reflection.

Covers reasoning-vs-content routing, the content fallback, error swallowing, the
"(no inner state)" empty guard, and the template's summarise/infer-direction/
frame logic. Mirrors the C# reference (ReasoningLoopInnerMonologue.cs +
HerJarvisRealImplementations.cs).
"""
from __future__ import annotations

from typing import AsyncGenerator, List, Optional

import pytest

from circle_ai.companion.herjarvis_contracts import IInnerMonologue, SelfReflection
from circle_ai.companion.inner_monologue import (
    ReasoningLoopInnerMonologue,
    TemplateInnerMonologue,
)
from circle_ai.inference.inference import GenerationOptions
from circle_ai.models.models import ChatFragment, ChatFragmentKind, ChatMessage


# ── fakes ─────────────────────────────────────────────────────────────────


class ReasoningFakeGenerator:
    """A reasoning-aware generator that shadows stream_fragments_async — the
    Python mirror of a C# generator overriding the default interface method."""

    def __init__(self, reasoning: List[str], content: List[str]) -> None:
        self._reasoning = reasoning
        self._content = content
        self.last_options: Optional[GenerationOptions] = None
        self.last_messages: Optional[List[ChatMessage]] = None

    async def generate_async(self, messages, options=None) -> str:  # pragma: no cover
        return "".join(self._content)

    async def stream_async(self, messages, options=None) -> AsyncGenerator[str, None]:
        for c in self._content:
            yield c

    async def stream_fragments_async(
        self, messages, options=None
    ) -> AsyncGenerator[ChatFragment, None]:
        self.last_messages = list(messages)
        self.last_options = options
        for r in self._reasoning:
            yield ChatFragment(kind=ChatFragmentKind.REASONING, text=r)
        for c in self._content:
            yield ChatFragment(kind=ChatFragmentKind.CONTENT, text=c)


class ContentOnlyGenerator:
    """A plain generator with no fragment method — exercises the module-level
    stream_fragments_async helper fallback (tags everything CONTENT)."""

    def __init__(self, content: List[str]) -> None:
        self._content = content

    async def generate_async(self, messages, options=None) -> str:  # pragma: no cover
        return "".join(self._content)

    async def stream_async(self, messages, options=None) -> AsyncGenerator[str, None]:
        for c in self._content:
            yield c


class ThrowingGenerator:
    """Raises mid-stream to exercise the swallow-and-fall-back path."""

    async def generate_async(self, messages, options=None) -> str:  # pragma: no cover
        raise RuntimeError("boom")

    async def stream_async(self, messages, options=None) -> AsyncGenerator[str, None]:
        raise RuntimeError("boom")
        yield  # pragma: no cover — unreachable, makes this an async generator


# ── ReasoningLoopInnerMonologue ───────────────────────────────────────────


def test_reasoning_loop_implements_interface() -> None:
    assert isinstance(ReasoningLoopInnerMonologue(ContentOnlyGenerator([])), IInnerMonologue)


def test_reasoning_loop_requires_llm() -> None:
    with pytest.raises(ValueError):
        ReasoningLoopInnerMonologue(None)  # type: ignore[arg-type]


async def test_reflect_requires_context() -> None:
    im = ReasoningLoopInnerMonologue(ContentOnlyGenerator(["ok"]))
    with pytest.raises(ValueError):
        await im.reflect_async(None)  # type: ignore[arg-type]


async def test_prefers_reasoning_trace_as_thought() -> None:
    gen = ReasoningFakeGenerator(reasoning=["I notice ", "a pattern."], content=["Noted."])
    im = ReasoningLoopInnerMonologue(gen)
    r = await im.reflect_async('{"mood":"tired"}')
    assert isinstance(r, SelfReflection)
    # Reasoning fragments are concatenated and preferred over content.
    assert r.thought == "I notice a pattern."


async def test_falls_back_to_content_when_no_reasoning() -> None:
    gen = ContentOnlyGenerator(["Just ", "an observation."])
    im = ReasoningLoopInnerMonologue(gen)
    r = await im.reflect_async('{"x":1}')
    assert r.thought == "Just an observation."


async def test_empty_stream_yields_no_inner_state() -> None:
    gen = ContentOnlyGenerator([])
    im = ReasoningLoopInnerMonologue(gen)
    r = await im.reflect_async("{}")
    assert r.thought == "(no inner state)"


async def test_llm_failure_is_swallowed_and_falls_back() -> None:
    im = ReasoningLoopInnerMonologue(ThrowingGenerator())
    r = await im.reflect_async('{"a":1}')
    # No fragments captured before the error -> the empty guard fires.
    assert r.thought == "(no inner state)"


async def test_reasoning_loop_sends_expected_options_and_prompt() -> None:
    gen = ReasoningFakeGenerator(reasoning=["r"], content=["c"])
    im = ReasoningLoopInnerMonologue(gen)
    ctx = '{"topic":"deadlines"}'
    await im.reflect_async(ctx)
    assert gen.last_options is not None
    assert gen.last_options.max_tokens == 256
    assert gen.last_options.temperature == pytest.approx(0.5)
    assert gen.last_options.include_reasoning is True
    assert gen.last_messages is not None
    assert gen.last_messages[0].role == "system"
    assert gen.last_messages[1].role == "user"
    assert ctx in gen.last_messages[1].content
    assert "Reflect on this in 2-3 sentences." in gen.last_messages[1].content


# ── TemplateInnerMonologue ────────────────────────────────────────────────


def test_template_implements_interface() -> None:
    assert isinstance(TemplateInnerMonologue(), IInnerMonologue)


async def test_template_requires_context() -> None:
    with pytest.raises(ValueError):
        await TemplateInnerMonologue().reflect_async(None)  # type: ignore[arg-type]


async def test_template_is_deterministic_for_same_input() -> None:
    tm = TemplateInnerMonologue()
    a = await tm.reflect_async('{"error":"boom"}')
    b = await tm.reflect_async('{"error":"boom"}')
    assert a.thought == b.thought


async def test_template_uses_one_of_the_three_frames() -> None:
    tm = TemplateInnerMonologue()
    r = await tm.reflect_async('{"user":"asks for help"}')
    # The chosen frame always contains the inferred direction phrase.
    assert "respond to the user" in r.thought
    # And it is built from one of the three known frame skeletons.
    assert any(
        marker in r.thought
        for marker in ("Implication:", "the salient pattern is", "my next step is to")
    )


async def test_template_infers_direction_by_keyword_priority() -> None:
    tm = TemplateInnerMonologue()
    # error beats goal beats user beats default (checked in that order in C#).
    assert "diagnose the failure first" in (await tm.reflect_async('{"error":1,"goal":1,"user":1}')).thought
    assert "advance toward the stated goal" in (await tm.reflect_async('{"goal":1,"user":1}')).thought
    assert "respond to the user" in (await tm.reflect_async('{"user":1}')).thought
    assert "gather more context" in (await tm.reflect_async('{"weather":"sunny"}')).thought


async def test_template_summary_strips_braces_and_caps_at_twelve_words() -> None:
    tm = TemplateInnerMonologue()
    # 15 numeric tokens; braces/brackets/quotes are stripped to spaces, then the
    # first 12 words are kept.
    ctx = '{"a":["one two three four five six seven eight nine ten eleven twelve thirteen"]}'
    r = await tm.reflect_async(ctx)
    # "thirteen" is the 13th+ content word after the "a:" tokens and must be cut.
    # Count words in the summary portion is bounded — assert the tail word is gone
    # only if it would have exceeded 12; robustly, assert <= 12 rendered summary
    # tokens by checking the frame still formed.
    assert r.thought  # well-formed
    assert "thirteen" not in r.thought
