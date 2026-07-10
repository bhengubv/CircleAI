"""test_video_contracts.py — CircleAI.Video contract surface + implementations.

Covers the null generator + null style-script (fail-closed passthrough), the
VideoResolution presets + StyleId value semantics, and the thread-safe
InMemoryStyleReference catalogue (register / get / list, case-insensitive keying,
insertion ordering, update-in-place, concurrency). C# (CircleAI.Video) is the
reference.
"""
from __future__ import annotations

import asyncio
from datetime import timedelta

import pytest

from circle_ai.video import (
    AudioTrack,
    InMemoryStyleReference,
    NullStyleScript,
    NullVideoGenerator,
    StyleAttribution,
    StyleId,
    StyleReference,
    StyleReferenceFrame,
    StyleScriptRequest,
    VideoGenerationRequest,
    VideoResolution,
)


# ── primitives ───────────────────────────────────────────────────────────────────


def test_video_resolution_presets():
    assert VideoResolution.P480 == VideoResolution(720, 480)
    assert VideoResolution.P720 == VideoResolution(1280, 720)
    assert VideoResolution.P1080 == VideoResolution(1920, 1080)


def test_style_id_value_and_str():
    sid = StyleId("noir-detective")
    assert sid.value == "noir-detective"
    assert str(sid) == "noir-detective"
    # value equality (frozen dataclass)
    assert StyleId("x") == StyleId("x")
    assert StyleId("x") != StyleId("y")


# ── null video generator ─────────────────────────────────────────────────────────


async def test_null_video_generator_returns_empty_video_echoing_resolution():
    g = NullVideoGenerator.instance()
    assert g.backend_id == "null"
    req = VideoGenerationRequest(
        prompt="hi there",
        duration=timedelta(seconds=5),
        resolution=VideoResolution.P720,
    )
    res = await g.generate_async(req)
    assert res.video_bytes == b""
    assert res.mime_type == "video/mp4"
    assert res.duration == timedelta(0)
    assert res.frame_count == 0
    assert res.resolution == VideoResolution.P720  # echoes the request resolution
    assert res.backend_id == "null"


def test_video_generation_request_defaults():
    req = VideoGenerationRequest(prompt="p", duration=timedelta(seconds=3), resolution=VideoResolution.P480)
    assert req.frame_rate == 24
    assert req.style_id is None
    assert req.reference_image is None
    assert req.audio_track is None
    assert req.seed is None


# ── null style script ────────────────────────────────────────────────────────────


async def test_null_style_script_passthrough():
    s = NullStyleScript.instance()
    assert s.backend_id == "null"
    req = StyleScriptRequest(source_message="original words", style=StyleId("pooh-1926"))
    res = await s.rewrite_async(req)
    assert res.rewritten_text == "original words"  # unchanged
    assert res.style == StyleId("pooh-1926")
    assert res.voice_persona_id is None
    assert res.estimated_spoken_duration == timedelta(0)


# ── in-memory style reference catalogue ──────────────────────────────────────────


def _style(id_value, name="Name", persona=None):
    return StyleReference(
        id=StyleId(id_value),
        display_name=name,
        short_description="desc",
        attribution=StyleAttribution(source="src", license="CC0", url=None),
        voice_persona_id=persona,
        frames=(StyleReferenceFrame(image_bytes=b"\x00", mime_type="image/png", caption="c"),),
    )


async def test_in_memory_style_reference_register_get_list():
    cat = InMemoryStyleReference()
    assert cat.backend_id == "in-memory"
    assert await cat.get_async(StyleId("missing")) is None
    assert await cat.list_async() == ()

    a = _style("noir", "Noir")
    b = _style("anime", "Anime")
    await cat.register_async(a)
    await cat.register_async(b)

    assert await cat.get_async(StyleId("noir")) == a
    assert await cat.get_async(StyleId("anime")) == b
    listed = await cat.list_async()
    assert set(s.id.value for s in listed) == {"noir", "anime"}
    assert len(listed) == 2


async def test_in_memory_style_reference_is_case_insensitive():
    cat = InMemoryStyleReference()
    await cat.register_async(_style("Pooh-1926", "Winnie"))
    # OrdinalIgnoreCase: any casing of the id retrieves the same style.
    got = await cat.get_async(StyleId("pooh-1926"))
    assert got is not None
    assert got.display_name == "Winnie"
    got2 = await cat.get_async(StyleId("POOH-1926"))
    assert got2 is not None and got2.display_name == "Winnie"


async def test_in_memory_style_reference_update_in_place_keeps_single_entry():
    cat = InMemoryStyleReference()
    await cat.register_async(_style("noir", "First"))
    # Re-register the same id (different casing) -> replaces, does not duplicate.
    await cat.register_async(_style("NOIR", "Second"))
    listed = await cat.list_async()
    assert len(listed) == 1
    assert listed[0].display_name == "Second"


async def test_in_memory_style_reference_list_preserves_insertion_order():
    cat = InMemoryStyleReference()
    for v in ("one", "two", "three"):
        await cat.register_async(_style(v))
    assert [s.id.value for s in await cat.list_async()] == ["one", "two", "three"]


async def test_in_memory_style_reference_concurrent_registration_is_safe():
    cat = InMemoryStyleReference()

    async def add(i):
        await cat.register_async(_style(f"style-{i}"))

    await asyncio.gather(*(add(i) for i in range(50)))
    listed = await cat.list_async()
    assert len(listed) == 50
    assert set(s.id.value for s in listed) == {f"style-{i}" for i in range(50)}


def test_audio_track_shape():
    tr = AudioTrack(audio_pcm16_mono=b"\x01\x02", sample_rate_hz=16_000, duration=timedelta(seconds=1))
    assert tr.sample_rate_hz == 16_000
    assert tr.duration == timedelta(seconds=1)
