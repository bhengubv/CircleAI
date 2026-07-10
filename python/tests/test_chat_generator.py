"""test_chat_generator.py

Verifies the deterministic IChatGenerator port + the <think> reasoning router +
Qwen ChatML prompt building + RT-02 session round-trip + vision path.
"""
from __future__ import annotations

import pytest

from circle_ai.inference import (
    ChatFragmentKind,
    DeterministicChatGenerator,
    GenerationOptions,
    PowerBudget,
    build_qwen_chat_prompt,
    find_stop_sequence,
    generate_response_async,
    route_text,
    stream_fragments_async,
)
from circle_ai.models.models import ChatMessage


def _msgs():
    return [ChatMessage("system", "be nice"), ChatMessage("user", "hello")]


# ── think router ─────────────────────────────────────────────────────────


def test_route_text_splits_think_block():
    frs = route_text("hi<think>reasoning</think>bye", [], True)
    assert [(f.kind, f.text) for f in frs] == [
        (ChatFragmentKind.CONTENT, "hi"),
        (ChatFragmentKind.REASONING, "reasoning"),
        (ChatFragmentKind.CONTENT, "bye"),
    ]


def test_route_text_drops_reasoning_when_disabled():
    frs = route_text("a<think>secret</think>b", [], False)
    assert [f.text for f in frs] == ["a", "b"]
    assert all(f.kind == ChatFragmentKind.CONTENT for f in frs)


def test_route_text_open_without_close_is_all_reasoning():
    frs = route_text("visible<think>dangling", [], True)
    kinds = [(f.kind, f.text) for f in frs]
    assert kinds == [
        (ChatFragmentKind.CONTENT, "visible"),
        (ChatFragmentKind.REASONING, "dangling"),
    ]


def test_route_text_stop_sequence_truncates():
    # Content after the stop marker is dropped, marker itself excluded.
    frs = route_text("keep<|im_end|>drop", ["<|im_end|>"], True)
    assert [f.text for f in frs] == ["keep"]


def test_find_stop_sequence_first_stop_in_list_order():
    # Mirrors C# TryFindStopSequence: returns the index of the FIRST stop in
    # list order that occurs (not the leftmost position across all stops).
    assert find_stop_sequence("abcXdefY", ["Y", "X"]) == 7  # "Y" checked first
    assert find_stop_sequence("abcXdefY", ["X", "Y"]) == 3  # "X" checked first
    assert find_stop_sequence("none", ["Z"]) == -1
    assert find_stop_sequence("skipempty", ["", "e"]) == 4


# ── Qwen ChatML prompt ───────────────────────────────────────────────────


def test_build_qwen_chat_prompt_shape():
    p = build_qwen_chat_prompt([ChatMessage("System", "s"), ChatMessage("user", "u")])
    assert p == (
        "<|im_start|>system\ns\n<|im_end|>\n"
        "<|im_start|>user\nu\n<|im_end|>\n"
        "<|im_start|>assistant\n"
    )


def test_build_qwen_chat_prompt_blank_role_defaults_user():
    p = build_qwen_chat_prompt([ChatMessage("  ", "x")])
    assert p.startswith("<|im_start|>user\nx\n<|im_end|>\n")


# ── generator determinism + reasoning ────────────────────────────────────


async def test_generator_is_deterministic():
    g = DeterministicChatGenerator("m1")
    r1 = await g.generate_response_async(_msgs(), GenerationOptions(seed=7))
    r2 = await g.generate_response_async(_msgs(), GenerationOptions(seed=7))
    assert r1.text == r2.text
    assert r1.reasoning_content == r2.reasoning_content


async def test_generator_seed_changes_output():
    g = DeterministicChatGenerator("m1")
    r1 = await g.generate_response_async(_msgs(), GenerationOptions(seed=1))
    r2 = await g.generate_response_async(_msgs(), GenerationOptions(seed=2))
    assert r1.text != r2.text


async def test_generate_response_surfaces_reasoning():
    g = DeterministicChatGenerator("m1")
    r = await g.generate_response_async(_msgs(), GenerationOptions(seed=3, include_reasoning=True))
    assert r.reasoning_content is not None
    assert "<think>" not in r.text and "</think>" not in r.text
    assert r.tokens_out >= 1


async def test_include_reasoning_false_drops_reasoning():
    g = DeterministicChatGenerator("m1")
    r = await g.generate_response_async(_msgs(), GenerationOptions(seed=3, include_reasoning=False))
    assert r.reasoning_content is None
    assert "<think>" not in r.text


async def test_stream_async_is_content_only():
    g = DeterministicChatGenerator("m1")
    chunks = [c async for c in g.stream_async(_msgs(), GenerationOptions(seed=3))]
    joined = "".join(chunks)
    assert "<think>" not in joined and "</think>" not in joined


async def test_stream_fragments_tags_reasoning_and_content():
    g = DeterministicChatGenerator("m1")
    kinds = set()
    async for f in g.stream_fragments_async(_msgs(), GenerationOptions(seed=3)):
        kinds.add(f.kind)
    assert ChatFragmentKind.REASONING in kinds
    assert ChatFragmentKind.CONTENT in kinds


async def test_generate_async_equals_streamed_content():
    g = DeterministicChatGenerator("m1")
    opts = GenerationOptions(seed=9)
    full = await g.generate_async(_msgs(), opts)
    chunks = "".join([c async for c in g.stream_async(_msgs(), opts)])
    assert full == chunks


# ── power budget cap in generator ────────────────────────────────────────


async def test_low_budget_caps_content_length():
    g = DeterministicChatGenerator("m1")
    # LOW caps at 64 tokens ~= 256 chars of content.
    r = await g.generate_response_async(
        _msgs(), GenerationOptions(seed=5, budget=PowerBudget.LOW, include_reasoning=False)
    )
    assert len(r.text) <= 64 * 4


# ── vision path ──────────────────────────────────────────────────────────


async def test_vision_input_changes_output_and_marks_image():
    g = DeterministicChatGenerator("vlm", supports_vision=True)
    text_only = await g.generate_async(
        [ChatMessage("user", "describe")], GenerationOptions(seed=1, include_reasoning=False)
    )
    with_img = await g.generate_async(
        [ChatMessage("user", "describe", image_bytes=b"\x89PNG_fake")],
        GenerationOptions(seed=1, include_reasoning=False),
    )
    assert text_only != with_img
    assert "[image]" in with_img


# ── session round-trip (RT-02) ───────────────────────────────────────────


async def test_session_save_and_load_roundtrip(tmp_path):
    g = DeterministicChatGenerator("m1")
    path = str(tmp_path / "sess.marker")
    assert await g.save_session_async(path) is True
    assert await g.load_session_async(path) is True


async def test_load_session_missing_file_returns_false(tmp_path):
    g = DeterministicChatGenerator("m1")
    assert await g.load_session_async(str(tmp_path / "nope")) is False


async def test_save_session_requires_path():
    g = DeterministicChatGenerator("m1")
    with pytest.raises(ValueError):
        await g.save_session_async("")


# ── disposal + free-function default ─────────────────────────────────────


async def test_disposed_generator_raises():
    g = DeterministicChatGenerator("m1")
    g.dispose()
    with pytest.raises(RuntimeError):
        await g.generate_async(_msgs())


async def test_free_function_generate_response_wraps_content_only():
    # generate_response_async (module free fn) uses generate_async → content only,
    # reasoning_content stays None (matches C# default-interface fallback).
    g = DeterministicChatGenerator("m1")
    r = await generate_response_async(g, _msgs(), GenerationOptions(seed=3))
    assert r.reasoning_content is None
    assert "<think>" not in r.text
