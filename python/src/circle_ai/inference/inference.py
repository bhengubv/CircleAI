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

from enum import IntEnum

from ..models.models import (
    ChatFragment,
    ChatFragmentKind,
    ChatMessage,
    ChatResponse,
    FinishReason,
)


# ── PowerBudget (RT-11) ───────────────────────────────────────────────────


class PowerBudget(IntEnum):
    """Per-call power budget — how much device energy this generation is worth.

    Mirrors CircleAI.Inference.PowerBudget. The runtime translates the budget
    into a per-call max-tokens cap (and, when fallback chains exist, into a
    model-size pick).
    """

    NONE = 0
    """Opt out of automatic budget control — honour max_tokens literally."""

    LOW = 1
    """Battery-conscious. ~64 token cap, prefers TQ4 KV, smaller model in chain."""

    NORMAL = 2
    """Default balanced behaviour. ~512 token cap. Auto-downgrades to LOW below 15% battery."""

    HIGH = 3
    """Quality-first. ~2048 token cap, full FP16 KV. Auto-throttles on thermal warnings."""

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

    include_reasoning: bool = True
    """Whether to surface the model's reasoning trace (Qwen3
    ``<think>…</think>``) on the call.

    When ``True`` (default) the generator separates reasoning from the final
    answer: ``ChatResponse.reasoning_content`` gets the reasoning,
    ``ChatResponse.text`` gets the answer. Streaming callers see fragments
    tagged with ``ChatFragmentKind.REASONING``.

    When ``False`` the generator still RUNS reasoning (this is per-call output
    gating, NOT a thinking disable) but the reasoning text is dropped — only
    the final answer reaches the caller. Use this for JSON-strict consumers.
    """

    budget: PowerBudget = PowerBudget.NORMAL
    """(RT-11) Declarative power budget. The runtime maps it to a max-tokens
    cap and (eventually) model size. NORMAL auto-downgrades to LOW when battery
    is below 15%. Use NONE to honour max_tokens literally.
    """

    use_prefix_cache: bool = False
    """(RT-06) Whether the runtime should consult the cross-session prefix
    cache for a warm (model_id, system_prompt) snapshot before resetting the
    model handle. First call populates; subsequent calls reload it instead of
    running the system-prompt prefill.
    """


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
        """Stream the assistant reply chunk-by-chunk. Content only — any
        reasoning emitted inside ``<think>…</think>`` is filtered out. Use
        :func:`stream_fragments_async` when you also need the reasoning stream.
        """
        ...

    async def save_session_async(self, path: str) -> bool:
        """(RT-02) Save the current model session to ``path``. Returns
        ``True`` on success. Default protocol contract returns ``False`` —
        native generators (MNN-backed) override.
        """
        return False

    async def load_session_async(self, path: str) -> bool:
        """(RT-02) Load a previously-saved session from ``path``. Returns
        ``True`` on success. Default protocol contract returns ``False`` —
        native generators (MNN-backed) override.
        """
        return False


async def generate_response_async(
    generator: IChatGenerator,
    messages: list[ChatMessage],
    options: Optional[GenerationOptions] = None,
) -> ChatResponse:
    """Wrap IChatGenerator.generate_async into a structured ChatResponse.

    Python's Protocol doesn't support default method implementations the way
    C# default-interface-methods do, so this is exposed as a free function.
    Native generators may shadow it with a method that reports exact token
    counts and surfaces ``ChatResponse.reasoning_content`` — this default
    approximates and leaves reasoning_content as ``None``.
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
        reasoning_content=None,
    )


async def stream_fragments_async(
    generator: IChatGenerator,
    messages: list[ChatMessage],
    options: Optional[GenerationOptions] = None,
) -> AsyncGenerator[ChatFragment, None]:
    """Wrap IChatGenerator.stream_async into the fragment-tagged stream.

    Default helper: yields each chunk from ``stream_async`` tagged as
    ``ChatFragmentKind.CONTENT``. Generators that surface reasoning must
    expose their own ``stream_fragments_async`` method that interleaves
    ``REASONING`` fragments — this helper does NOT split ``<think>`` tags
    (that requires generator-level token routing).
    """
    async for chunk in generator.stream_async(messages, options):
        yield ChatFragment(kind=ChatFragmentKind.CONTENT, text=chunk)


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
