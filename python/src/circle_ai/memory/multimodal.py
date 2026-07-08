# memory/multimodal.py
#
# Compressed semantic memory for media artefacts (image / audio / video /
# document). Ported from CircleAI.Memory.Multimodal (C#) and mirrors the
# verified TypeScript reference (memory/multimodal.ts):
#   • MediaModality, MultimodalMemoryEntry
#   • IMultimodalCaptioner + CaptionResult + HeuristicMultimodalCaptioner
#   • IMultimodalMemoryStore + InMemoryMultimodalMemoryStore
#   • MultimodalMemoryIngester (+ IngestionResult)
#
# The whole point: we DO NOT store the pixels / audio samples / video frames —
# we store the caption, the embedding, and a SHA-256 of the original so the
# host can reference it back if it kept the file elsewhere. Raw bytes never
# leave the captioner; the store only ever holds the semantic record.

from __future__ import annotations

import hashlib
import math
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum
from typing import Iterable, Optional, Protocol, runtime_checkable


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# ─────────────────────────────────────────────────────────────────────────────
# MediaModality — CircleAI.Memory.Multimodal.MediaModality
# ─────────────────────────────────────────────────────────────────────────────


class MediaModality(Enum):
    """Modality of a multimodal memory entry.

    Drives how the ingester routes the raw bytes to the captioner and which
    side-channel metadata is captured.
    """

    Image = "Image"
    """Still image — JPEG, PNG, HEIC, WebP, AVIF."""
    Audio = "Audio"
    """Audio clip — Opus, WAV, MP3, M4A."""
    Video = "Video"
    """Video — MP4, MOV, WebM. Captioned via key-frame extraction by the host."""
    TextDocument = "TextDocument"
    """Text document — PDF, DOCX, plain text snippet larger than a single message."""


# ─────────────────────────────────────────────────────────────────────────────
# MultimodalMemoryEntry — CircleAI.Memory.Multimodal.MultimodalMemoryEntry
# ─────────────────────────────────────────────────────────────────────────────


@dataclass
class MultimodalMemoryEntry:
    """One semantically-compressed media memory.

    The caption + embedding capture the meaning; raw bytes are never retained
    by the memory layer. ``reference_count`` is mutable (incremented on dedup
    hits); everything else is effectively write-once, matching the C#
    ``init``/``set`` split.
    """

    id: uuid.UUID = field(default_factory=uuid.uuid4)
    recorded_at_utc: datetime = field(default_factory=_utc_now)
    modality: MediaModality = MediaModality.Image
    caption: str = ""
    embedding: Optional[list[float]] = None
    source_sha256: str = ""
    source_mime_type: Optional[str] = None
    source_byte_count: int = 0
    source_uri: Optional[str] = None
    width_px: Optional[int] = None
    height_px: Optional[int] = None
    duration_ms: Optional[int] = None
    reference_count: int = 1
    tags: Optional[dict[str, str]] = None


# ─────────────────────────────────────────────────────────────────────────────
# IMultimodalCaptioner + CaptionResult + HeuristicMultimodalCaptioner
# ─────────────────────────────────────────────────────────────────────────────


@dataclass
class CaptionResult:
    """Output of a single captioning call."""

    caption: str
    embedding: Optional[list[float]] = None
    width_px: Optional[int] = None
    height_px: Optional[int] = None
    duration_ms: Optional[int] = None


@runtime_checkable
class IMultimodalCaptioner(Protocol):
    """Converts raw media bytes into a semantic representation."""

    def can_caption(
        self, modality: MediaModality, mime_type: Optional[str]
    ) -> bool:
        """True when this captioner can handle the given modality + mime.

        The ingester picks among multiple captioners using this predicate.
        """
        ...

    async def caption_async(
        self,
        modality: MediaModality,
        source_bytes: bytes,
        mime_type: Optional[str],
        *,
        ct: Optional[object] = None,
    ) -> CaptionResult:
        """Produce a :class:`CaptionResult` for the given source bytes.

        Implementations must not retain the bytes after the call returns.
        """
        ...


class HeuristicMultimodalCaptioner:
    """Default :class:`IMultimodalCaptioner`.

    Returns a descriptive shell caption — never fabricates semantic content.
    Always available, zero model dependency, zero token cost.
    """

    def can_caption(
        self, modality: MediaModality, mime_type: Optional[str]
    ) -> bool:
        return True

    async def caption_async(
        self,
        modality: MediaModality,
        source_bytes: bytes,
        mime_type: Optional[str],
        *,
        ct: Optional[object] = None,
    ) -> CaptionResult:
        detected = _detect_mime(source_bytes, mime_type)
        length = len(source_bytes)
        if modality == MediaModality.Image:
            caption = f"[Image — no captioner wired. {detected}, {length} bytes.]"
        elif modality == MediaModality.Audio:
            caption = f"[Audio — no captioner wired. {detected}, {length} bytes.]"
        elif modality == MediaModality.Video:
            caption = f"[Video — no captioner wired. {detected}, {length} bytes.]"
        elif modality == MediaModality.TextDocument:
            caption = f"[Document — no captioner wired. {detected}, {length} bytes.]"
        else:
            caption = f"[Media — no captioner wired. {detected}, {length} bytes.]"
        return CaptionResult(caption=caption, embedding=None)


def _detect_mime(data: bytes, declared: Optional[str]) -> str:
    if declared is not None and len(declared.strip()) > 0:
        return declared
    if len(data) >= 4:
        if data[0] == 0xFF and data[1] == 0xD8:
            return "image/jpeg"
        if data[0] == 0x89 and data[1] == 0x50 and data[2] == 0x4E and data[3] == 0x47:
            return "image/png"
        if data[0] == 0x47 and data[1] == 0x49 and data[2] == 0x46:
            return "image/gif"
        if data[0] == 0x52 and data[1] == 0x49 and data[2] == 0x46 and data[3] == 0x46:
            return "audio/wav"
        if data[0] == 0x25 and data[1] == 0x50 and data[2] == 0x44 and data[3] == 0x46:
            return "application/pdf"
    return "application/octet-stream"


# ─────────────────────────────────────────────────────────────────────────────
# IMultimodalMemoryStore + InMemoryMultimodalMemoryStore
# ─────────────────────────────────────────────────────────────────────────────


@runtime_checkable
class IMultimodalMemoryStore(Protocol):
    """Persistent store of compressed multimodal memories."""

    async def add_async(
        self, entry: MultimodalMemoryEntry, *, ct: Optional[object] = None
    ) -> None:
        """Add an entry. Duplicate SHA-256 hits should be handled via get_by_hash_async."""
        ...

    async def get_by_hash_async(
        self, source_sha256: str, *, ct: Optional[object] = None
    ) -> Optional[MultimodalMemoryEntry]:
        """Return the entry with the given hash, or None if unknown."""
        ...

    async def reinforce_async(
        self, source_sha256: str, *, ct: Optional[object] = None
    ) -> None:
        """Increment reference_count for the entry whose hash matches. No-op when unknown."""
        ...

    async def search_async(
        self,
        query_embedding: Optional[list[float]],
        top_k: int = 5,
        *,
        ct: Optional[object] = None,
    ) -> list[MultimodalMemoryEntry]:
        """Return the top-top_k entries most similar (cosine) to query_embedding.

        When the query is None, falls back to most-recent.
        """
        ...

    async def get_recent_async(
        self, count: int = 10, *, ct: Optional[object] = None
    ) -> list[MultimodalMemoryEntry]:
        """Return the most recent *count* entries."""
        ...

    async def prune_older_than_async(
        self, cutoff: datetime, *, ct: Optional[object] = None
    ) -> int:
        """Remove entries older than *cutoff*. Return count removed."""
        ...

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        """Total entries currently stored."""
        ...


class InMemoryMultimodalMemoryStore:
    """In-memory :class:`IMultimodalMemoryStore`. Keyed by SHA-256 (case-insensitive).

    C# uses a ``ConcurrentDictionary`` with ``OrdinalIgnoreCase``; we lower-case
    the key to reproduce case-insensitive hash lookups.
    """

    def __init__(self) -> None:
        self._by_hash: dict[str, MultimodalMemoryEntry] = {}

    async def add_async(
        self, entry: MultimodalMemoryEntry, *, ct: Optional[object] = None
    ) -> None:
        if entry is None:
            raise ValueError("entry required")
        if entry.source_sha256 is None or len(entry.source_sha256.strip()) == 0:
            raise ValueError("SourceSha256 is required.")
        self._by_hash[_key_of(entry.source_sha256)] = entry

    async def get_by_hash_async(
        self, source_sha256: str, *, ct: Optional[object] = None
    ) -> Optional[MultimodalMemoryEntry]:
        return self._by_hash.get(_key_of(source_sha256))

    async def reinforce_async(
        self, source_sha256: str, *, ct: Optional[object] = None
    ) -> None:
        e = self._by_hash.get(_key_of(source_sha256))
        if e is not None:
            e.reference_count += 1

    async def search_async(
        self,
        query_embedding: Optional[list[float]],
        top_k: int = 5,
        *,
        ct: Optional[object] = None,
    ) -> list[MultimodalMemoryEntry]:
        if query_embedding is None:
            return sorted(
                self._by_hash.values(),
                key=lambda e: e.recorded_at_utc,
                reverse=True,
            )[:top_k]

        scored = [
            (e, _cosine_score(query_embedding, e.embedding))
            for e in self._by_hash.values()
            if e.embedding is not None and len(e.embedding) > 0
        ]
        scored.sort(key=lambda t: t[1], reverse=True)
        return [e for e, _ in scored[:top_k]]

    async def get_recent_async(
        self, count: int = 10, *, ct: Optional[object] = None
    ) -> list[MultimodalMemoryEntry]:
        return sorted(
            self._by_hash.values(), key=lambda e: e.recorded_at_utc, reverse=True
        )[:count]

    async def prune_older_than_async(
        self, cutoff: datetime, *, ct: Optional[object] = None
    ) -> int:
        doomed = [
            _key_of(e.source_sha256)
            for e in self._by_hash.values()
            if e.recorded_at_utc < cutoff
        ]
        for h in doomed:
            self._by_hash.pop(h, None)
        return len(doomed)

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        return len(self._by_hash)


def _key_of(sha: str) -> str:
    return sha.lower()


def _cosine_score(a: list[float], b: list[float]) -> float:
    """Cosine similarity — matches the C# store's internal CosineSimilarity.Score."""
    if len(a) != len(b):
        return 0.0
    dot = 0.0
    mag_a = 0.0
    mag_b = 0.0
    for i in range(len(a)):
        dot += a[i] * b[i]
        mag_a += a[i] * a[i]
        mag_b += b[i] * b[i]
    denom = math.sqrt(mag_a) * math.sqrt(mag_b)
    # double.Epsilon in C# is the smallest positive double (~5e-324); sys epsilon
    # is far larger, so guard against a literal zero denominator instead.
    return 0.0 if denom == 0.0 else dot / denom


# ─────────────────────────────────────────────────────────────────────────────
# MultimodalMemoryIngester — CircleAI.Memory.Multimodal.MultimodalMemoryIngester
# ─────────────────────────────────────────────────────────────────────────────


@dataclass
class IngestionResult:
    """Outcome of a :meth:`MultimodalMemoryIngester.ingest_async` call."""

    entry: MultimodalMemoryEntry
    was_deduplicated: bool


class MultimodalMemoryIngester:
    """Ingests raw media bytes into compressed semantic memory.

    1. Hashes the source (SHA-256, hex-lower).
    2. Dedupes — if the hash is known, reinforces the existing entry and returns
       it (no re-captioning, no duplicate storage).
    3. Picks a captioner via ``can_caption()``.
    4. Asks the captioner for a :class:`CaptionResult`.
    5. Persists a :class:`MultimodalMemoryEntry` to the store.

    Raw bytes are never persisted. The hash is the only durable handle the
    memory layer keeps for the original artefact.
    """

    def __init__(
        self,
        captioners: Iterable[IMultimodalCaptioner],
        store: IMultimodalMemoryStore,
    ) -> None:
        """Captioners are tried in order — the first whose ``can_caption()``
        returns true wins. The host typically registers richer captioners first
        and the heuristic fallback last.
        """
        if captioners is None:
            raise ValueError("captioners required")
        if store is None:
            raise ValueError("store required")
        self._captioners: list[IMultimodalCaptioner] = list(captioners)
        if len(self._captioners) == 0:
            raise ValueError("At least one captioner is required.")
        self._store = store

    async def ingest_async(
        self,
        modality: MediaModality,
        source_bytes: bytes,
        *,
        mime_type: Optional[str] = None,
        source_uri: Optional[str] = None,
        tags: Optional[dict[str, str]] = None,
        ct: Optional[object] = None,
    ) -> IngestionResult:
        """Ingest an artefact.

        When the SHA-256 matches an existing entry the stored record is
        reinforced rather than re-captioned, and the result's
        ``was_deduplicated`` is true.
        """
        if source_bytes is None or len(source_bytes) == 0:
            raise ValueError("Source bytes are empty.")

        digest = _compute_sha256(source_bytes)
        existing = await self._store.get_by_hash_async(digest, ct=ct)
        if existing is not None:
            await self._store.reinforce_async(digest, ct=ct)
            return IngestionResult(entry=existing, was_deduplicated=True)

        captioner = self._pick_captioner(modality, mime_type)
        caption = await captioner.caption_async(
            modality, source_bytes, mime_type, ct=ct
        )

        entry = MultimodalMemoryEntry(
            modality=modality,
            caption=caption.caption,
            embedding=caption.embedding,
            source_sha256=digest,
            source_mime_type=mime_type,
            source_byte_count=len(source_bytes),
            source_uri=source_uri,
            width_px=caption.width_px,
            height_px=caption.height_px,
            duration_ms=caption.duration_ms,
            tags=tags,
        )

        await self._store.add_async(entry, ct=ct)
        return IngestionResult(entry=entry, was_deduplicated=False)

    def _pick_captioner(
        self, modality: MediaModality, mime: Optional[str]
    ) -> IMultimodalCaptioner:
        for c in self._captioners:
            if c.can_caption(modality, mime):
                return c
        # The last registered captioner should accept everything; if no
        # host-supplied captioner matches, the heuristic fallback wins.
        return self._captioners[-1]


def _compute_sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()
