# video/null_implementations.py
#
# Port of CircleAI.Video/NullImplementations.cs (C# — the EXACT spec).
#
# (3.1.0) Safe null defaults — every interface has a working implementation that
# returns empty or fail-closed answers. Absence of a real backend degrades to
# deterministic empty answers, never a crash. InMemoryStyleReference is a
# thread-safe in-memory catalogue suitable for production use until a persistent
# store lands.

from __future__ import annotations

import threading
from datetime import timedelta
from typing import Dict, Optional, Tuple

from .contracts import IStyleReference, IStyleScript, IVideoGenerator
from .primitives import (
    StyleId,
    StyleReference,
    StyleScriptRequest,
    StyleScriptResult,
    VideoGenerationRequest,
    VideoGenerationResult,
)


class NullVideoGenerator(IVideoGenerator):
    """(3.1.0) Returns an empty video — zero bytes, declared mime type
    "video/mp4". A real consumer that ends up with this backend should fall back
    to audio-only style mail.

    Mirrors ``CircleAI.Video.NullVideoGenerator``.
    """

    _instance: "NullVideoGenerator | None" = None

    @classmethod
    def instance(cls) -> "NullVideoGenerator":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    async def generate_async(
        self, request: VideoGenerationRequest, ct: object = None
    ) -> VideoGenerationResult:
        return VideoGenerationResult(
            video_bytes=b"",
            mime_type="video/mp4",
            duration=timedelta(0),
            frame_count=0,
            resolution=request.resolution,
            backend_id="null",
        )


class NullStyleScript(IStyleScript):
    """(3.1.0) Returns the source message unchanged with a zero estimated
    duration. Lets consumers swap in a real LLM-backed rewriter without changing
    the wiring.

    Mirrors ``CircleAI.Video.NullStyleScript``.
    """

    _instance: "NullStyleScript | None" = None

    @classmethod
    def instance(cls) -> "NullStyleScript":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    async def rewrite_async(
        self, request: StyleScriptRequest, ct: object = None
    ) -> StyleScriptResult:
        return StyleScriptResult(
            rewritten_text=request.source_message,
            style=request.style,
            voice_persona_id=None,
            estimated_spoken_duration=timedelta(0),
        )


class InMemoryStyleReference(IStyleReference):
    """(3.1.0) Thread-safe in-memory style catalogue. Hosting layers (txtMe,
    content authoring tools) register their style packs on startup and the picker
    reads from here.

    Mirrors ``CircleAI.Video.InMemoryStyleReference`` — the C# backing store is a
    ``Dictionary<string, StyleReference>(StringComparer.OrdinalIgnoreCase)`` keyed
    on ``style.Id.Value``. We reproduce the case-insensitive keying while keeping
    the original inserted ordering for ``list_async`` (updating an existing key
    keeps its slot, matching .NET dict semantics).
    """

    def __init__(self) -> None:
        # key = casefolded id value; entry = (original-cased id value, style).
        self._by_id: Dict[str, Tuple[str, StyleReference]] = {}
        self._gate = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def register_async(self, style: StyleReference, ct: object = None) -> None:
        with self._gate:
            self._by_id[style.id.value.casefold()] = (style.id.value, style)

    async def get_async(self, id: StyleId, ct: object = None) -> Optional[StyleReference]:
        with self._gate:
            entry = self._by_id.get(id.value.casefold())
            return entry[1] if entry is not None else None

    async def list_async(self, ct: object = None) -> Tuple[StyleReference, ...]:
        with self._gate:
            return tuple(entry[1] for entry in self._by_id.values())


__all__ = [
    "NullVideoGenerator",
    "NullStyleScript",
    "InMemoryStyleReference",
]
