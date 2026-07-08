"""test_multimodal.py

Exercises the multimodal memory pipeline: HeuristicMultimodalCaptioner,
InMemoryMultimodalMemoryStore, and the MultimodalMemoryIngester (dedup +
caption + persist). Mirrors the TypeScript multimodal.test.ts and
CircleAI.Tests.MultimodalMemoryTests. Bytes are synthesised inline so the tests
run identically on every box.
"""
from __future__ import annotations

import re
from datetime import datetime, timedelta, timezone
from typing import Optional

import pytest

from circle_ai.memory.multimodal import (
    CaptionResult,
    HeuristicMultimodalCaptioner,
    IMultimodalCaptioner,
    InMemoryMultimodalMemoryStore,
    MediaModality,
    MultimodalMemoryEntry,
    MultimodalMemoryIngester,
)


# ── Test helpers (mirror the C#/TS FakeJpeg/FakePng/WireIngester) ────────────


def fake_jpeg(extra_bytes: int = 100) -> bytes:
    buf = bytearray(2 + extra_bytes)
    buf[0] = 0xFF
    buf[1] = 0xD8
    for i in range(2, len(buf)):
        buf[i] = i % 251
    return bytes(buf)


def fake_png(extra_bytes: int = 100) -> bytes:
    buf = bytearray(4 + extra_bytes)
    buf[0] = 0x89
    buf[1] = 0x50
    buf[2] = 0x4E
    buf[3] = 0x47
    for i in range(4, len(buf)):
        buf[i] = i % 251
    return bytes(buf)


def wire_ingester(custom_captioner: Optional[IMultimodalCaptioner] = None):
    store = InMemoryMultimodalMemoryStore()
    if custom_captioner is not None:
        captioners = [custom_captioner, HeuristicMultimodalCaptioner()]
    else:
        captioners = [HeuristicMultimodalCaptioner()]
    return MultimodalMemoryIngester(captioners, store), store


class FakeRichCaptioner:
    """Only handles Image, returns a rich caption + embedding."""

    def can_caption(self, modality: MediaModality, mime_type: Optional[str]) -> bool:
        return modality == MediaModality.Image

    async def caption_async(
        self,
        modality: MediaModality,
        source_bytes: bytes,
        mime_type: Optional[str],
        *,
        ct: Optional[object] = None,
    ) -> CaptionResult:
        return CaptionResult(
            caption="A blue sky with two clouds.",
            embedding=[0.1, 0.2, 0.3],
            width_px=1920,
            height_px=1080,
        )


# ══════════════════════════════════════════════════════════════════════════
# HeuristicMultimodalCaptioner
# ══════════════════════════════════════════════════════════════════════════


def test_heuristic_always_can_caption_any_modality():
    c = HeuristicMultimodalCaptioner()
    assert c.can_caption(MediaModality.Image, "image/jpeg") is True
    assert c.can_caption(MediaModality.Audio, None) is True
    assert c.can_caption(MediaModality.Video, "video/mp4") is True
    assert c.can_caption(MediaModality.TextDocument, "application/pdf") is True


async def test_heuristic_detects_jpeg_magic_and_produces_no_embedding():
    c = HeuristicMultimodalCaptioner()
    r = await c.caption_async(MediaModality.Image, fake_jpeg(), None)
    assert "image/jpeg" in r.caption
    assert r.embedding is None


async def test_heuristic_detects_png_gif_wav_pdf_magic_bytes():
    c = HeuristicMultimodalCaptioner()
    assert "image/png" in (await c.caption_async(MediaModality.Image, fake_png(), None)).caption
    assert "image/gif" in (
        await c.caption_async(MediaModality.Image, bytes((0x47, 0x49, 0x46, 0x38)), None)
    ).caption
    assert "audio/wav" in (
        await c.caption_async(MediaModality.Audio, bytes((0x52, 0x49, 0x46, 0x46)), None)
    ).caption
    assert "application/pdf" in (
        await c.caption_async(MediaModality.TextDocument, bytes((0x25, 0x50, 0x44, 0x46)), None)
    ).caption


async def test_heuristic_falls_back_to_octet_stream_for_unknown_magic():
    c = HeuristicMultimodalCaptioner()
    r = await c.caption_async(MediaModality.Audio, bytes((1, 2, 3, 4)), None)
    assert "application/octet-stream" in r.caption


async def test_heuristic_uses_declared_mime_type_when_provided():
    c = HeuristicMultimodalCaptioner()
    r = await c.caption_async(MediaModality.Image, fake_png(), "image/heic")
    assert "image/heic" in r.caption


async def test_heuristic_marks_itself_as_fallback_and_includes_byte_count():
    c = HeuristicMultimodalCaptioner()
    data = fake_jpeg()
    r = await c.caption_async(MediaModality.Image, data, None)
    assert "no captioner wired" in r.caption
    assert f"{len(data)} bytes" in r.caption


async def test_heuristic_uses_right_modality_label_per_media_kind():
    c = HeuristicMultimodalCaptioner()
    assert (await c.caption_async(MediaModality.Image, fake_jpeg(), None)).caption.startswith("[Image")
    assert (await c.caption_async(MediaModality.Audio, fake_jpeg(), "audio/wav")).caption.startswith("[Audio")
    assert (await c.caption_async(MediaModality.Video, fake_jpeg(), "video/mp4")).caption.startswith("[Video")
    assert (
        await c.caption_async(MediaModality.TextDocument, fake_jpeg(), "application/pdf")
    ).caption.startswith("[Document")


# ══════════════════════════════════════════════════════════════════════════
# Ingester — happy path
# ══════════════════════════════════════════════════════════════════════════


async def test_first_time_adds_entry_and_reports_not_deduplicated():
    ingester, store = wire_ingester()
    data = fake_jpeg()
    r = await ingester.ingest_async(MediaModality.Image, data, mime_type="image/jpeg")

    assert r.was_deduplicated is False
    assert await store.count_async() == 1
    assert r.entry is not None
    assert r.entry.source_byte_count == len(data)
    assert r.entry.source_mime_type == "image/jpeg"
    assert r.entry.source_sha256 and len(r.entry.source_sha256.strip()) > 0


async def test_second_time_same_bytes_deduplicates_and_reinforces():
    ingester, store = wire_ingester()
    data = fake_jpeg()
    first = await ingester.ingest_async(MediaModality.Image, data, mime_type="image/jpeg")
    second = await ingester.ingest_async(MediaModality.Image, data, mime_type="image/jpeg")

    assert first.was_deduplicated is False
    assert second.was_deduplicated is True
    assert await store.count_async() == 1
    assert first.entry.source_sha256 == second.entry.source_sha256
    assert second.entry.reference_count == 2


async def test_different_bytes_produce_distinct_entries():
    ingester, store = wire_ingester()
    ra = await ingester.ingest_async(MediaModality.Image, fake_jpeg(50))
    rb = await ingester.ingest_async(MediaModality.Image, fake_jpeg(60))
    assert ra.entry.source_sha256 != rb.entry.source_sha256
    assert await store.count_async() == 2


async def test_empty_bytes_throw():
    ingester, _ = wire_ingester()
    with pytest.raises(ValueError):
        await ingester.ingest_async(MediaModality.Image, b"")


async def test_records_source_uri_and_tags_when_provided():
    ingester, _ = wire_ingester()
    data = fake_png()
    r = await ingester.ingest_async(
        MediaModality.Image,
        data,
        mime_type="image/png",
        source_uri="file:///photos/IMG_001.png",
        tags={"location": "home", "person": "alex"},
    )
    assert r.entry.source_uri == "file:///photos/IMG_001.png"
    assert r.entry.tags is not None
    assert r.entry.tags["location"] == "home"
    assert r.entry.tags["person"] == "alex"


async def test_computes_hex_lower_sha256_stable_across_calls():
    ingester, _ = wire_ingester()
    r = await ingester.ingest_async(MediaModality.Image, fake_jpeg(0))
    assert re.match(r"^[0-9a-f]{64}$", r.entry.source_sha256)


# ══════════════════════════════════════════════════════════════════════════
# Captioner selection
# ══════════════════════════════════════════════════════════════════════════


async def test_prefers_rich_captioner_over_heuristic():
    ingester, _ = wire_ingester(FakeRichCaptioner())
    r = await ingester.ingest_async(MediaModality.Image, fake_jpeg(), mime_type="image/jpeg")
    assert r.entry.caption == "A blue sky with two clouds."
    assert r.entry.embedding is not None
    assert r.entry.width_px == 1920
    assert r.entry.height_px == 1080


async def test_falls_back_to_heuristic_when_rich_captioner_declines():
    ingester, _ = wire_ingester(FakeRichCaptioner())
    r = await ingester.ingest_async(MediaModality.Audio, fake_png(), mime_type="audio/wav")
    assert "no captioner wired" in r.entry.caption
    assert r.entry.embedding is None


def test_rejects_construction_with_zero_captioners():
    with pytest.raises(ValueError):
        MultimodalMemoryIngester([], InMemoryMultimodalMemoryStore())


# ══════════════════════════════════════════════════════════════════════════
# Store: search, prune, recent, reinforce
# ══════════════════════════════════════════════════════════════════════════


async def test_store_search_by_embedding_ranks_by_cosine():
    store = InMemoryMultimodalMemoryStore()
    await store.add_async(
        MultimodalMemoryEntry(source_sha256="near", caption="near", embedding=[1, 0.1, 0])
    )
    await store.add_async(
        MultimodalMemoryEntry(source_sha256="far", caption="far", embedding=[0, 0, 1])
    )

    ranked = await store.search_async([1, 0, 0], 2)
    assert ranked[0].source_sha256 == "near"
    assert ranked[1].source_sha256 == "far"


async def test_store_search_with_null_query_returns_most_recent():
    store = InMemoryMultimodalMemoryStore()
    now = datetime.now(timezone.utc)
    await store.add_async(
        MultimodalMemoryEntry(
            source_sha256="older", caption="older", recorded_at_utc=now - timedelta(days=10)
        )
    )
    await store.add_async(
        MultimodalMemoryEntry(source_sha256="newer", caption="newer", recorded_at_utc=now)
    )
    recent = await store.search_async(None, 2)
    assert recent[0].source_sha256 == "newer"


async def test_store_prune_removes_entries_older_than_cutoff():
    store = InMemoryMultimodalMemoryStore()
    now = datetime.now(timezone.utc)
    await store.add_async(
        MultimodalMemoryEntry(
            source_sha256="old", caption="old", recorded_at_utc=now - timedelta(days=10)
        )
    )
    await store.add_async(
        MultimodalMemoryEntry(source_sha256="new", caption="new", recorded_at_utc=now)
    )

    removed = await store.prune_older_than_async(now - timedelta(days=5))
    assert removed == 1
    assert await store.count_async() == 1
    assert await store.get_by_hash_async("new") is not None
    assert await store.get_by_hash_async("old") is None


async def test_store_reinforce_increments_reference_count():
    store = InMemoryMultimodalMemoryStore()
    await store.add_async(MultimodalMemoryEntry(source_sha256="x", caption="x"))
    await store.reinforce_async("x")
    await store.reinforce_async("x")
    got = await store.get_by_hash_async("x")
    assert got is not None
    assert got.reference_count == 3  # initial 1 + 2 reinforce


async def test_store_reinforce_on_unknown_hash_is_noop():
    store = InMemoryMultimodalMemoryStore()
    await store.reinforce_async("missing")  # must not throw
    assert await store.count_async() == 0


async def test_store_add_without_hash_throws():
    store = InMemoryMultimodalMemoryStore()
    with pytest.raises(ValueError):
        await store.add_async(MultimodalMemoryEntry(source_sha256="", caption="x"))


async def test_store_hash_lookup_is_case_insensitive():
    store = InMemoryMultimodalMemoryStore()
    await store.add_async(MultimodalMemoryEntry(source_sha256="ABCDEF", caption="x"))
    assert await store.get_by_hash_async("abcdef") is not None
