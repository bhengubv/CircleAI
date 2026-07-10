# vision/cloud/contracts.py
#
# Port of CircleAI.Vision.Cloud/Contracts.cs (C# — the EXACT spec).
#
# (3.2.0) Image-generation contract surface. CircleAI.Vision is detection-only;
# this pack is its generation counterpart (lifted from
# Concierge.Shared.Media.IImageRuntime). Null implementation ships out of the box;
# the OpenAI / Stability generators speak HTTP and take an injected transport.
#
# C# -> Python mapping:
#   byte[]                  -> bytes
#   IReadOnlyList<T>        -> tuple[T, ...]
#   Task<T>                 -> async def -> T
#   DateTimeOffset          -> datetime (tz-aware, UTC)

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import Optional, Tuple


@dataclass(frozen=True, slots=True)
class ImageGenerationRequest:
    """(3.2.0) One image-generation request. Mirrors ``ImageGenerationRequest``.

    :param prompt: Text prompt.
    :param negative_prompt: Optional negative prompt (Stability supports it; OpenAI ignores).
    :param size: Square size in pixels — typical 512 / 768 / 1024 / 1536.
    :param count: Number of images to produce (1..n).
    :param style: Optional style preset id (provider-specific).
    """

    prompt: str
    negative_prompt: Optional[str] = None
    size: int = 1024
    count: int = 1
    style: Optional[str] = None


@dataclass(frozen=True, slots=True)
class ImageArtifact:
    """(3.2.0) One generated image. Either ``url`` OR ``bytes_``, never both.

    Mirrors ``ImageArtifact`` — ``record(string GeneratorId, string Prompt,
    string MimeType, string? Url, byte[]? Bytes, DateTimeOffset GeneratedAtUtc)``.
    The C# ``Bytes`` field is spelled ``bytes_`` here to avoid shadowing the
    builtin.
    """

    generator_id: str
    prompt: str
    mime_type: str
    url: Optional[str]
    bytes_: Optional[bytes]
    generated_at_utc: datetime


class IImageGenerator(ABC):
    """(3.2.0) Generate images from a text prompt. Mirrors ``IImageGenerator``."""

    @property
    @abstractmethod
    def generator_id(self) -> str:
        """Backend self-identification — "openai-images" / "stability" / "null"."""
        ...

    @property
    @abstractmethod
    def display_label(self) -> str:
        """Display label for the UI selector."""
        ...

    @property
    @abstractmethod
    def is_configured(self) -> bool:
        """True when the generator has the credentials it needs."""
        ...

    @property
    @abstractmethod
    def status_message(self) -> str:
        """Status message for the UI."""
        ...

    @abstractmethod
    async def generate_async(
        self, request: ImageGenerationRequest, ct: object = None
    ) -> Tuple[ImageArtifact, ...]:
        """Generate images. Fail-soft: empty tuple when not configured."""
        ...


class NullImageGenerator(IImageGenerator):
    """(3.2.0) Empty generator — always returns no images.

    Mirrors ``CircleAI.Vision.Cloud.NullImageGenerator``.
    """

    _instance: "NullImageGenerator | None" = None

    @classmethod
    def instance(cls) -> "NullImageGenerator":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def generator_id(self) -> str:
        return "null"

    @property
    def display_label(self) -> str:
        return "No image generator"

    @property
    def is_configured(self) -> bool:
        return False

    @property
    def status_message(self) -> str:
        return "No image generator wired. Configure OpenAI:ApiKey or Stability:ApiKey to enable."

    async def generate_async(
        self, request: ImageGenerationRequest, ct: object = None
    ) -> Tuple[ImageArtifact, ...]:
        return ()


__all__ = [
    "ImageGenerationRequest",
    "ImageArtifact",
    "IImageGenerator",
    "NullImageGenerator",
]
