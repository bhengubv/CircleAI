"""Inference-layer contracts for CircleAI Python.

Mirrors CircleAI.Inference in the C# port:

    * IChatGenerator   — text generator contract + generate_response_async
                          default-equivalent.
    * IChatGenerator   gains ChatResponse-returning helper.
    * ChatCapability   — flag enum used by IModelSelector.
    * IModelSelector / ModelSelection — capability + device-fit picker.
"""
from __future__ import annotations

import time
from dataclasses import dataclass
from enum import IntFlag
from typing import (
    TYPE_CHECKING,
    AsyncGenerator,
    Optional,
    Protocol,
    Sequence,
    runtime_checkable,
)

from ..models.models import ChatMessage, ChatResponse, FinishReason

if TYPE_CHECKING:  # pragma: no cover
    from ..device.device_probe import DeviceProbe, DeviceTier


# ── GenerationOptions ────────────────────────────────────────────────────


@dataclass
class GenerationOptions:
    """Knobs for a single generation call."""

    max_tokens: int = 512
    temperature: float = 0.7
    top_p: float = 0.9
    top_k: int = 40
    seed: Optional[int] = None
    stop_sequences: Optional[list[str]] = None


# ── IChatGenerator ───────────────────────────────────────────────────────


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


async def generate_response_async(
    generator: IChatGenerator,
    messages: list[ChatMessage],
    options: Optional[GenerationOptions] = None,
) -> ChatResponse:
    """Wrap IChatGenerator.generate_async into a structured ChatResponse.

    Python's Protocol doesn't support default method implementations the way
    C# default-interface-methods do, so this is exposed as a free function.
    Native generators may shadow it with a method that reports exact token
    counts from the inference engine — this default approximates.
    """
    started = time.monotonic()
    text = await generator.generate_async(messages, options)
    latency_ms = (time.monotonic() - started) * 1000.0

    tokens_in = _approx_tokens_messages(messages)
    tokens_out = _approx_tokens(text)

    return ChatResponse(
        text=text,
        tokens_in=tokens_in,
        tokens_out=tokens_out,
        latency_ms=latency_ms,
        finish_reason=FinishReason.STOP,
    )


def _approx_tokens(text: Optional[str]) -> int:
    if not text:
        return 0
    # Crude 4-chars-per-token approximation; matches the C# fallback.
    return max(1, len(text) // 4)


def _approx_tokens_messages(messages: Sequence[ChatMessage]) -> int:
    return sum(_approx_tokens(m.content) for m in messages)


# ── ChatCapability + IModelSelector ──────────────────────────────────────


class ChatCapability(IntFlag):
    """Capabilities a chat model declares.

    Mirrors CircleAI.Inference.ChatCapability. Selectors filter by these
    flags so consumers can request "vision + tools" and only get a model
    that handles both.
    """

    NONE = 0
    DEFAULT = 1
    TOOLS = 2
    VISION = 4
    LONG_CONTEXT = 8
    REASONING = 16


@dataclass(frozen=True)
class ModelSelection:
    """One selector result. tier is the device tier the pick was sized for."""

    model_id: str
    requires_download: bool
    estimated_bytes: int
    tier: "DeviceTier"


@runtime_checkable
class IModelSelector(Protocol):
    """Picks a model that fits the device + the requested capabilities."""

    def best_fit(
        self,
        probe: "DeviceProbe",
        required: ChatCapability = ChatCapability.DEFAULT,
    ) -> ModelSelection:
        """Returns the highest-quality entry that satisfies every flag in
        `required` AND has MinRamGb <= probe RAM AND MinStorageGb <= free."""
        ...

    def all_candidates(self, probe: "DeviceProbe") -> list[ModelSelection]:
        """Every selection candidate in registry order — diagnostics use."""
        ...
