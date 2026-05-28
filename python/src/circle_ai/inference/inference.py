from __future__ import annotations

from dataclasses import dataclass
from typing import AsyncGenerator, Optional, Protocol, runtime_checkable

from ..models.models import ChatMessage


@dataclass
class GenerationOptions:
    """Knobs for a single generation call."""

    max_tokens: int = 512
    temperature: float = 0.7
    top_p: float = 0.9
    top_k: int = 40
    seed: Optional[int] = None
    stop_sequences: Optional[list[str]] = None


@runtime_checkable
class IChatGenerator(Protocol):
    """Contract for an on-device chat-style text generator."""

    async def generate_async(
        self,
        messages: list[ChatMessage],
        options: Optional[GenerationOptions] = None,
    ) -> str:
        """Generate a complete assistant reply for the given conversation."""
        ...

    async def stream_async(
        self,
        messages: list[ChatMessage],
        options: Optional[GenerationOptions] = None,
    ) -> AsyncGenerator[str, None]:
        """Stream the assistant reply chunk-by-chunk."""
        ...
