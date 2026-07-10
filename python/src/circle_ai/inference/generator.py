"""Deterministic on-device chat generator.

A concrete :class:`~circle_ai.inference.inference.IChatGenerator` standing in
for the native ``QwenTextGenerator`` / ``KimiVlGenerator`` from the C# port.
The native generators own MNN model state and stream tokens off a native
callback; that is not portable to pure-Python. This generator reproduces the
*observable contract* of the native ones deterministically:

  * builds a Qwen ChatML prompt from the message history
    (``build_qwen_chat_prompt`` — port of ``QwenTextGenerator.BuildQwenChatPrompt``),
  * derives a reproducible reply from the prompt + seed,
  * emits an optional ``<think>...</think>`` reasoning block when the caller
    asks for reasoning, routed through the same state machine as
    ``MnnTokenRouter`` (:mod:`circle_ai.inference.think_router`),
  * honours ``GenerationOptions`` — ``max_tokens`` via the
    :class:`PowerBudgetPolicy` cap, ``stop_sequences``, ``include_reasoning``,
    ``use_prefix_cache``,
  * accepts vision turns (``ChatMessage.image_bytes``) and folds a marker of
    the image into the reply so a vision path is exercised,
  * implements the RT-02 save/load session round-trip via the portable marker
    file the C# default-interface methods write.

Determinism: identical (messages, options.seed) always yield identical output,
so cross-language fixtures and tests are stable.
"""
from __future__ import annotations

import hashlib
import os
import time
from typing import AsyncGenerator, List, Optional

from ..models.models import (
    ChatFragment,
    ChatFragmentKind,
    ChatMessage,
    ChatResponse,
    FinishReason,
)
from .inference import GenerationOptions, PowerBudget
from .kv_compression import PowerBudgetPolicy
from .prefix_cache import PrefixCacheService
from .think_router import route_text, split_content_reasoning

__all__ = [
    "DeterministicChatGenerator",
    "build_qwen_chat_prompt",
]

_IM_START = "<|im_start|>"
_IM_END = "<|im_end|>"
_END_OF_TEXT = "<|endoftext|>"
_DEFAULT_STOP_SEQUENCES = [_IM_END, _IM_START, _END_OF_TEXT]

_SESSION_MARKER = "circleai-session-marker"


def build_qwen_chat_prompt(messages: List[ChatMessage]) -> str:
    """Build a Qwen ChatML prompt. Port of ``QwenTextGenerator.BuildQwenChatPrompt``.

    Each turn is wrapped in ``<|im_start|>role\\n...\\n<|im_end|>\\n`` and a
    final open assistant turn is appended for the model to complete. A blank
    role falls back to ``user``; role is trimmed + lower-cased.
    """
    parts: List[str] = []
    for m in messages:
        role = m.role.strip().lower() if m.role and m.role.strip() else "user"
        parts.append(_IM_START)
        parts.append(role)
        parts.append("\n")
        parts.append(m.content or "")
        parts.append("\n")
        parts.append(_IM_END)
        parts.append("\n")
    parts.append(_IM_START)
    parts.append("assistant\n")
    return "".join(parts)


def _extract_system_prompt(messages: List[ChatMessage]) -> Optional[str]:
    """First system-role message content, or ``None``. Mirrors
    ``QwenTextGenerator.ExtractSystemPrompt``.
    """
    for m in messages:
        if m.role and m.role.lower() == "system":
            return m.content
    return None


def _latest_image(messages: List[ChatMessage]) -> Optional[bytes]:
    """Most recent turn's image bytes, or ``None``. Mirrors the KimiVl
    ``LastOrDefault(m => m.ImageBytes is { Length: > 0 })`` selection.
    """
    found: Optional[bytes] = None
    for m in messages:
        if m.image_bytes:
            found = m.image_bytes
    return found


class DeterministicChatGenerator:
    """Deterministic :class:`IChatGenerator`.

    Stands in for the native MNN generators. Construct with an optional
    ``model_id`` (used for prefix-cache keying and the session marker),
    ``supports_vision`` to flag the vision path, and an injectable
    :class:`PrefixCacheService` for RT-06.
    """

    __slots__ = ("_model_id", "_supports_vision", "_prefix_cache", "_disposed")

    def __init__(
        self,
        model_id: str = "deterministic-local",
        *,
        supports_vision: bool = False,
        prefix_cache: Optional[PrefixCacheService] = None,
    ) -> None:
        if not model_id or not model_id.strip():
            raise ValueError("model_id is required")
        self._model_id = model_id
        self._supports_vision = supports_vision
        self._prefix_cache = prefix_cache
        self._disposed = False

    @property
    def model_id(self) -> str:
        return self._model_id

    @property
    def supports_vision(self) -> bool:
        return self._supports_vision

    # ── IChatGenerator surface ────────────────────────────────────────────

    async def generate_async(
        self,
        messages: List[ChatMessage],
        options: Optional[GenerationOptions] = None,
    ) -> str:
        """Generate a complete assistant reply (content only)."""
        self._throw_if_disposed()
        if messages is None:
            raise ValueError("messages is required")
        parts: List[str] = []
        async for chunk in self.stream_async(messages, options):
            parts.append(chunk)
        return "".join(parts)

    async def stream_async(
        self,
        messages: List[ChatMessage],
        options: Optional[GenerationOptions] = None,
    ) -> AsyncGenerator[str, None]:
        """Stream the reply chunk-by-chunk. Content only — reasoning filtered."""
        async for f in self.stream_fragments_async(messages, options):
            if f.kind == ChatFragmentKind.CONTENT and f.text:
                yield f.text

    async def stream_fragments_async(
        self,
        messages: List[ChatMessage],
        options: Optional[GenerationOptions] = None,
    ) -> AsyncGenerator[ChatFragment, None]:
        """Stream fragments tagged CONTENT / REASONING via the think router."""
        self._throw_if_disposed()
        if messages is None:
            raise ValueError("messages is required")
        options = options or GenerationOptions()

        # RT-11: resolve the declarative budget into a per-call token cap.
        requested = options.max_tokens if options.max_tokens > 0 else 512
        resolved = PowerBudgetPolicy.resolve(options.budget, requested)
        max_tokens = max(1, resolved.max_tokens)

        # RT-06: consult / populate the prefix cache when opted in.
        if options.use_prefix_cache and self._prefix_cache is not None:
            system_prompt = _extract_system_prompt(messages)
            key = PrefixCacheService.key_for(self._model_id, system_prompt)
            if key is not None:
                if os.path.isfile(self._prefix_cache.path_for(key)):
                    self._prefix_cache.touch(key)
                else:
                    # Populate: write a marker snapshot, then bound the cache.
                    with open(self._prefix_cache.path_for(key), "w", encoding="utf-8") as fh:
                        fh.write(f"{_SESSION_MARKER}\nmodel:{self._model_id}\n")
                    await self._prefix_cache.evict_if_needed_async()

        prompt = build_qwen_chat_prompt(messages)
        image = _latest_image(messages)
        stops = (
            list(options.stop_sequences)
            if options.stop_sequences
            else list(_DEFAULT_STOP_SEQUENCES)
        )

        full_text = self._render(prompt, image, options, max_tokens)
        for f in route_text(full_text, stops, options.include_reasoning):
            yield f

    async def generate_response_async(
        self,
        messages: List[ChatMessage],
        options: Optional[GenerationOptions] = None,
    ) -> ChatResponse:
        """Structured response with content + reasoning separated + token counts.

        Native generators override the free-function default; this method is the
        generator-level override that surfaces ``reasoning_content`` — matching
        ``QwenTextGenerator.GenerateResponseAsync``.
        """
        self._throw_if_disposed()
        started = time.monotonic()
        fragments: List[ChatFragment] = []
        async for f in self.stream_fragments_async(messages, options):
            fragments.append(f)
        latency_ms = (time.monotonic() - started) * 1000.0

        content, reasoning = split_content_reasoning(fragments)
        tokens_in = _approx_tokens_messages(messages)
        tokens_out = _approx_tokens(content)
        return ChatResponse(
            text=content,
            tokens_in=tokens_in,
            tokens_out=tokens_out,
            latency_ms=latency_ms,
            finish_reason=FinishReason.STOP,
            reasoning_content=reasoning,
        )

    async def save_session_async(self, path: str) -> bool:
        """(RT-02) Save a portable session marker. Mirrors the C# default
        ``SaveSessionAsync``: writes ``circleai-session-marker`` + type + UTC.
        """
        if not path or not path.strip():
            raise ValueError("path required")
        marker = (
            f"{_SESSION_MARKER}\n"
            f"type:{type(self).__module__}.{type(self).__qualname__}\n"
            f"model:{self._model_id}\n"
        )
        d = os.path.dirname(path)
        if d:
            os.makedirs(d, exist_ok=True)
        with open(path, "w", encoding="utf-8") as fh:
            fh.write(marker)
        return True

    async def load_session_async(self, path: str) -> bool:
        """(RT-02) Verify the marker written by :meth:`save_session_async`."""
        if not path or not path.strip():
            raise ValueError("path required")
        if not os.path.isfile(path):
            return False
        with open(path, "r", encoding="utf-8") as fh:
            text = fh.read()
        return text.startswith(_SESSION_MARKER)

    def dispose(self) -> None:
        """Mark the generator disposed. Further calls raise (mirrors the C#
        ``ObjectDisposedException`` guard).
        """
        self._disposed = True

    # ── Deterministic rendering ───────────────────────────────────────────

    def _render(
        self,
        prompt: str,
        image: Optional[bytes],
        options: GenerationOptions,
        max_tokens: int,
    ) -> str:
        """Produce a reproducible reply for ``prompt`` (+ optional image).

        The reply is a deterministic function of (prompt, image digest, seed).
        It optionally opens with a ``<think>...</think>`` reasoning block when
        the caller wants reasoning surfaced, so the routing path is exercised.
        The body length is bounded by ``max_tokens`` (approx 4 chars/token,
        matching the SDK's token approximation).
        """
        seed = options.seed if options.seed is not None else 0
        digest_src = f"{prompt}|seed={seed}"
        if image is not None:
            digest_src += "|img=" + hashlib.sha256(image).hexdigest()[:16]
        digest = hashlib.sha256(digest_src.encode("utf-8")).hexdigest()

        segments: List[str] = []

        if options.include_reasoning:
            # A short deterministic reasoning trace, tagged for the router.
            reason = f"Considering the request (h={digest[:8]})."
            segments.append(f"<think>{reason}</think>")

        body_parts = ["Response"]
        if image is not None:
            body_parts.append("[image]")
        body_parts.append(digest[:24])
        body = " ".join(body_parts)

        segments.append(body)
        text = "".join(segments)

        # Bound the *content* length by the token cap (approx 4 chars/token).
        # We cap only the trailing content so any reasoning block stays intact.
        char_cap = max_tokens * 4
        if options.include_reasoning and segments:
            reasoning_block = segments[0]
            content = segments[1] if len(segments) > 1 else ""
            content = content[:char_cap]
            return reasoning_block + content
        return text[:char_cap]

    def _throw_if_disposed(self) -> None:
        if self._disposed:
            raise RuntimeError("DeterministicChatGenerator has been disposed")


def _approx_tokens(text: Optional[str]) -> int:
    if not text:
        return 0
    return max(1, len(text) // 4)


def _approx_tokens_messages(messages: List[ChatMessage]) -> int:
    return sum(_approx_tokens(m.content) for m in messages)
