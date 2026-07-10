# video/contracts.py
#
# Port of CircleAI.Video/Contracts.cs (C# — the EXACT spec).
#
# (3.1.0) The CircleAI.Video contract surface. Three interfaces — one generator,
# one script rewriter, one style catalogue. Null implementations ship out of the
# box (see null_implementations.py); real backends (CogVideoX-2B ONNX->MNN,
# LTX-Video distilled-2B) are injected.
#
# Driving use case: txtMe Video Mail. Sender calls, no answer, types a message.
# Recipient's B! renders the message as a short styled video.
#
# C# -> Python mapping:
#   ValueTask<T>          -> async def -> T
#   ValueTask             -> async def -> None
#   IReadOnlyList<T>      -> tuple[T, ...]
#   T?  (nullable record) -> Optional[T]

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Optional, Tuple

from .primitives import (
    StyleId,
    StyleReference,
    StyleScriptRequest,
    StyleScriptResult,
    VideoGenerationRequest,
    VideoGenerationResult,
)


class IVideoGenerator(ABC):
    """(3.1.0) Generate a short video from a text prompt (and optional style +
    reference frame + audio track). First concrete backend is CogVideoX-2B;
    LTX-Video distilled-2B follows. Both run on-device (<= 12 GB VRAM quantised).

    Mirrors ``CircleAI.Video.IVideoGenerator``.
    """

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "cogvideox-2b", "ltx-video-2b-distilled", "null"."""
        ...

    @abstractmethod
    async def generate_async(
        self, request: VideoGenerationRequest, ct: object = None
    ) -> VideoGenerationResult:
        """Synthesise the requested video. Raises if the device cannot satisfy the request."""
        ...


class IStyleScript(ABC):
    """(3.1.0) Rewrite a user message in a chosen style's voice. Runs against the
    existing IChatGenerator with a style-specific system prompt — no new model
    needed for this leg.

    Mirrors ``CircleAI.Video.IStyleScript``.
    """

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "circleai-llm", "null"."""
        ...

    @abstractmethod
    async def rewrite_async(
        self, request: StyleScriptRequest, ct: object = None
    ) -> StyleScriptResult:
        """Rewrite the source message in the requested style."""
        ...


class IStyleReference(ABC):
    """(3.1.0) Catalogue of registered styles — public-domain illustrations,
    original-character renders, genre presets (noir, space-opera,
    storybook-watercolour, claymation, anime, …). Lets the txtMe UI present a
    picker and lets the generator look up grounding frames.

    Mirrors ``CircleAI.Video.IStyleReference``.
    """

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "in-memory", "embedded-defaults", "null"."""
        ...

    @abstractmethod
    async def register_async(self, style: StyleReference, ct: object = None) -> None:
        """Register a style (typically at host startup)."""
        ...

    @abstractmethod
    async def get_async(self, id: StyleId, ct: object = None) -> Optional[StyleReference]:
        """Look up one style by id."""
        ...

    @abstractmethod
    async def list_async(self, ct: object = None) -> Tuple[StyleReference, ...]:
        """Enumerate every registered style — drives picker UIs."""
        ...


__all__ = [
    "IVideoGenerator",
    "IStyleScript",
    "IStyleReference",
]
