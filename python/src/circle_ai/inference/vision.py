"""Vision input container for multimodal inference.

Port of ``CircleAI.Inference.VisionInput`` — the image data container passed
to a vision-capable generator (Kimi-VL / Qwen-VL) so an image is embedded
before the text prompt.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

__all__ = ["VisionInput"]


@dataclass(frozen=True, slots=True)
class VisionInput:
    """Raw image data to be embedded by the vision encoder before generation.

    Mirrors ``CircleAI.Inference.VisionInput``. ``image_bytes`` is required;
    ``mime_type`` is an advisory hint (e.g. ``"image/jpeg"``) that callers may
    use to track format — it is not required by the encoder, which sniffs the
    magic bytes.
    """

    image_bytes: bytes
    mime_type: Optional[str] = None

    def __post_init__(self) -> None:
        if self.image_bytes is None:
            raise ValueError("image_bytes is required")
