# inference.py
#
# Python port of Circle.AI.Inference public surface.
#
# Covers:
#   GenerationOptions — knobs for a single generation call
#   IChatGenerator    — on-device chat-style text generator ABC

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import AsyncGenerator, Optional

from .models import ChatMessage


# ---------------------------------------------------------------------------
# GenerationOptions
# ---------------------------------------------------------------------------

@dataclass
class GenerationOptions:
    """Knobs for a single generation call."""

    max_tokens: int = 512
    temperature: float = 0.7
    top_p: float = 0.9
    top_k: int = 40
    seed: Optional[int] = None
    stop_sequences: Optional[list[str]] = None


# ---------------------------------------------------------------------------
# IChatGenerator ABC
# ---------------------------------------------------------------------------

class IChatGenerator(ABC):
    """Contract for an on-device chat-style text generator.

    Implementations own native model state and must be disposed (use as an
    async context manager, or call ``close()`` explicitly).
    """

    @abstractmethod
    async def generate_async(
        self,
        messages: list[ChatMessage],
        options: Optional[GenerationOptions] = None,
        *,
        ct: Optional[object] = None,
    ) -> str:
        """Generate a complete assistant reply for the given conversation."""
        ...

    @abstractmethod
    async def stream_async(
        self,
        messages: list[ChatMessage],
        options: Optional[GenerationOptions] = None,
        *,
        ct: Optional[object] = None,
    ) -> AsyncGenerator[str, None]:
        """Stream the assistant reply chunk-by-chunk.

        Each yielded string is the next chunk to append to the output.
        Callers should concatenate in order.
        """
        ...

    def close(self) -> None:  # noqa: B027 — intentionally non-abstract default
        """Release native resources.  Override in concrete implementations."""

    def __enter__(self) -> "IChatGenerator":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()
