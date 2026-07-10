"""Cloud fallback chains — port of CircleAI.Hosting.CloudFallback.

  * ``IConfigurableChatGenerator`` — reports whether a generator can serve
    calls (e.g. API key present) + display label + status.
  * ``CloudFallbackChain`` — composite ``IChatGenerator`` that walks an ordered
    list and uses the first ready generator (start-of-call ordering, fail-soft
    frame skipping).
  * ``BackupBrainOrchestrator`` + ``BrainHealth`` / ``BrainStatus`` /
    ``BackupBrainPolicy`` — runtime between-turn failover with degraded-state
    tracking + cool-down half-open retry.
  * ``FakeConfigurableChatGenerator`` — deterministic local fake standing in
    for the real HTTP cloud generators (OpenAI/Groq/etc., which are all
    ``IChatGenerator`` + ``IConfigurableChatGenerator``). Real providers speak
    HTTP/SSE and are injected; this fake keeps the chain + orchestrator logic
    fully testable in-memory.

The chain/orchestrator algorithms (ready checks, fail-soft frame detection,
degraded thresholds, cool-down, per-turn retry cap) are ported faithfully.
"""
from __future__ import annotations

import datetime as _dt
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from enum import IntEnum
from typing import AsyncGenerator, Callable, List, Optional, Sequence, Set

from ...inference.inference import GenerationOptions, IChatGenerator
from ...models.models import ChatMessage

__all__ = [
    "IConfigurableChatGenerator",
    "CloudFallbackChain",
    "BrainHealth",
    "BrainStatus",
    "BackupBrainPolicy",
    "BackupBrainOrchestrator",
    "FakeConfigurableChatGenerator",
]

_UTC = _dt.timezone.utc


class IConfigurableChatGenerator(ABC):
    """(3.2.0) Reports whether a generator can currently serve calls. Mirrors
    ``IConfigurableChatGenerator``. Implementors are also :class:`IChatGenerator`s.
    """

    @property
    @abstractmethod
    def is_configured(self) -> bool:
        """True when the generator can serve calls (e.g. API key present)."""
        ...

    @property
    @abstractmethod
    def engine_label(self) -> str:
        """Display name (e.g. ``"OpenAI · gpt-4o-mini"``)."""
        ...

    @property
    @abstractmethod
    def status_message(self) -> str:
        """Human-readable explanation of the current state."""
        ...


class CloudFallbackChain:
    """(3.2.0) Tries an ordered list of :class:`IChatGenerator`s and streams
    from the first one ready. A generator that yields a fail-soft
    ``[… not configured]`` frame doesn't count as ready — the chain skips it.
    Generators that raise are also skipped. Mirrors ``CloudFallbackChain``.
    """

    __slots__ = ("_generators",)

    def __init__(self, generators: Sequence[IChatGenerator]) -> None:
        if generators is None:
            raise ValueError("generators is required")
        self._generators: List[IChatGenerator] = list(generators)

    @property
    def generators(self) -> List[IChatGenerator]:
        return list(self._generators)

    async def generate_async(
        self,
        messages: Sequence[ChatMessage],
        options: Optional[GenerationOptions] = None,
    ) -> str:
        for g in self._generators:
            if not _is_ready(g):
                continue
            try:
                return await g.generate_async(messages, options)
            except Exception:  # noqa: BLE001 - fall through to next generator
                continue
        return "[CloudFallbackChain: no configured generator could serve the request]"

    async def stream_async(
        self,
        messages: Sequence[ChatMessage],
        options: Optional[GenerationOptions] = None,
    ) -> AsyncGenerator[str, None]:
        for g in self._generators:
            if not _is_ready(g):
                continue

            # Attempt the stream; only commit to this generator if it produces
            # a real frame. The fail-soft sentinel is filtered so we can move on.
            yielded = False
            gen = g.stream_async(messages, options)
            faulted = False
            try:
                while True:
                    try:
                        chunk = await gen.__anext__()
                    except StopAsyncIteration:
                        break
                    except Exception:  # noqa: BLE001 - mid-stream fault
                        faulted = True
                        break

                    if not yielded and _is_fail_soft_frame(chunk):
                        # Generator declined the call (e.g. no API key).
                        break

                    yielded = True
                    yield chunk
            finally:
                aclose = getattr(gen, "aclose", None)
                if aclose is not None:
                    await aclose()

            if faulted:
                # Faulted mid-stream: if we already yielded, stop; else next gen.
                if yielded:
                    return
                continue

            if yielded:
                return

        yield "[CloudFallbackChain: no configured generator could serve the request]"


def _is_ready(g: IChatGenerator) -> bool:
    if isinstance(g, IConfigurableChatGenerator):
        return g.is_configured
    return True


def _is_fail_soft_frame(chunk: str) -> bool:
    return chunk.startswith("[") and (
        "not configured" in chunk.lower() or "cloudfallbackchain" in chunk.lower()
    )


# ── BackupBrainOrchestrator ────────────────────────────────────────────────


class BrainHealth(IntEnum):
    """(3.3.0) Health state of one brain in the chain. Mirrors ``BrainHealth``."""

    HEALTHY = 0
    DEGRADED = 1
    COOLING_DOWN = 2


@dataclass(frozen=True, slots=True)
class BrainStatus:
    """(3.3.0) Snapshot of brain health for monitoring. Mirrors ``BrainStatus``."""

    label: str
    health: BrainHealth
    consecutive_failures: int


@dataclass(frozen=True, slots=True)
class BackupBrainPolicy:
    """(3.3.0) Policy knobs. Mirrors ``BackupBrainPolicy``.

    ``cool_down_duration`` defaults to 30 s (via :attr:`cool_down_or_default`).
    """

    degraded_after_failures: int = 2
    cool_down_duration: Optional[_dt.timedelta] = None
    max_retries_per_turn: int = 3

    @property
    def cool_down_or_default(self) -> _dt.timedelta:
        return self.cool_down_duration or _dt.timedelta(seconds=30)


class _BrainEntry:
    __slots__ = ("brain", "gate", "consecutive", "degraded_since", "is_degraded")

    def __init__(self, brain: IChatGenerator) -> None:
        self.brain = brain
        self.gate = threading.RLock()
        self.consecutive = 0
        self.degraded_since = _dt.datetime.min.replace(tzinfo=_UTC)
        self.is_degraded = False

    def health_at(self, now: _dt.datetime, cool_down: _dt.timedelta) -> BrainHealth:
        if not self.is_degraded:
            return BrainHealth.HEALTHY
        if now - self.degraded_since >= cool_down:
            return BrainHealth.COOLING_DOWN  # half-open: ready for retry
        return BrainHealth.DEGRADED

    def record_success(self) -> None:
        with self.gate:
            self.consecutive = 0
            self.is_degraded = False

    def record_failure(self, threshold: int, now: _dt.datetime) -> None:
        with self.gate:
            self.consecutive += 1
            if self.consecutive >= threshold:
                self.is_degraded = True
                self.degraded_since = now


class BackupBrainOrchestrator:
    """(3.3.0) Wraps an ordered set of brains; switches on failure, retries the
    primary after a cool-down. Between-turn failover (vs the chain's
    start-of-call ordering). Mirrors ``BackupBrainOrchestrator``.

    ``clock`` is injectable for deterministic cool-down tests.
    """

    __slots__ = ("_brains", "_policy", "_clock")

    def __init__(
        self,
        brains: Sequence[IChatGenerator],
        policy: Optional[BackupBrainPolicy] = None,
        clock: Optional[Callable[[], _dt.datetime]] = None,
    ) -> None:
        if brains is None:
            raise ValueError("brains is required")
        self._brains: List[_BrainEntry] = [_BrainEntry(b) for b in brains]
        if len(self._brains) == 0:
            raise ValueError("At least one brain is required.")
        self._policy = policy or BackupBrainPolicy()
        self._clock = clock or (lambda: _dt.datetime.now(_UTC))

    @property
    def statuses(self) -> List[BrainStatus]:
        now = self._clock()
        result: List[BrainStatus] = []
        for e in self._brains:
            with e.gate:
                h = e.health_at(now, self._policy.cool_down_or_default)
                label = (
                    e.brain.engine_label
                    if isinstance(e.brain, IConfigurableChatGenerator)
                    else type(e.brain).__name__
                )
                result.append(BrainStatus(label, h, e.consecutive))
        return result

    async def generate_async(
        self,
        messages: Sequence[ChatMessage],
        options: Optional[GenerationOptions] = None,
    ) -> str:
        max_retries = min(self._policy.max_retries_per_turn, len(self._brains))
        tried: Set[int] = set()
        for _attempt in range(max_retries):
            pick = self._pick_available(tried)
            if pick is None:
                break
            tried.add(id(pick))
            try:
                result = await pick.brain.generate_async(messages, options)
                pick.record_success()
                return result
            except Exception:  # noqa: BLE001 - try next backup
                pick.record_failure(self._policy.degraded_after_failures, self._clock())
        return "[All brains failed.]"

    async def stream_async(
        self,
        messages: Sequence[ChatMessage],
        options: Optional[GenerationOptions] = None,
    ) -> AsyncGenerator[str, None]:
        max_retries = min(self._policy.max_retries_per_turn, len(self._brains))
        tried: Set[int] = set()
        for _attempt in range(max_retries):
            pick = self._pick_available(tried)
            if pick is None:
                break
            tried.add(id(pick))
            streamed_any = False
            failed = False
            async for chunk in _iterate_stream_safe(pick, messages, options):
                if chunk is None:
                    failed = True
                    break
                streamed_any = True
                yield chunk
            if failed:
                pick.record_failure(self._policy.degraded_after_failures, self._clock())
                if not streamed_any:
                    continue  # try the backup
            if streamed_any:
                pick.record_success()
                return
        yield "[All brains failed.]"

    def _pick_available(self, skip: Set[int]) -> Optional[_BrainEntry]:
        now = self._clock()
        for e in self._brains:
            if id(e) in skip:
                continue
            with e.gate:
                h = e.health_at(now, self._policy.cool_down_or_default)
                if h in (BrainHealth.HEALTHY, BrainHealth.COOLING_DOWN):
                    return e
        # None healthy — pick first untried brain anyway (degraded might recover).
        for e in self._brains:
            if id(e) not in skip:
                return e
        return None


async def _iterate_stream_safe(
    pick: _BrainEntry,
    messages: Sequence[ChatMessage],
    options: Optional[GenerationOptions],
) -> AsyncGenerator[Optional[str], None]:
    """Yield each chunk; yield ``None`` (sentinel) on any fault. Mirrors
    ``IterateStreamSafe``.
    """
    try:
        gen = pick.brain.stream_async(messages, options)
    except Exception:  # noqa: BLE001 - init fault
        yield None
        return

    try:
        while True:
            try:
                chunk = await gen.__anext__()
            except StopAsyncIteration:
                return
            except Exception:  # noqa: BLE001 - mid-stream fault
                yield None
                return
            yield chunk
    finally:
        aclose = getattr(gen, "aclose", None)
        if aclose is not None:
            await aclose()


# ── Deterministic local fake generator ─────────────────────────────────────


class FakeConfigurableChatGenerator(IChatGenerator, IConfigurableChatGenerator):
    """Deterministic local :class:`IChatGenerator` + :class:`IConfigurableChatGenerator`.

    Stands in for the real HTTP cloud generators (OpenAI/Groq/Cerebras/…) which
    all speak SSE — this keeps the chain/orchestrator fully testable in-memory.

    Behaviour knobs:
      * ``configured`` — drives ``is_configured``; when False the stream yields
        a single fail-soft ``[… not configured]`` frame (matches the C#
        ``OpenAiChatGenerator`` fail-soft path).
      * ``reply`` — text returned by ``generate_async`` / streamed in chunks.
      * ``fail`` — when True, ``generate_async``/``stream_async`` raise (used to
        exercise orchestrator failover + degraded tracking).
      * ``chunk_size`` — how the reply is split for streaming.
    """

    __slots__ = ("_id", "_reply", "_configured", "_fail", "_chunk_size")

    def __init__(
        self,
        engine_id: str = "fake",
        reply: str = "hello from fake",
        configured: bool = True,
        fail: bool = False,
        chunk_size: int = 4,
    ) -> None:
        self._id = engine_id
        self._reply = reply
        self._configured = configured
        self._fail = fail
        self._chunk_size = max(1, chunk_size)

    @property
    def is_configured(self) -> bool:
        return self._configured

    @property
    def engine_label(self) -> str:
        return f"{self._id} · fake"

    @property
    def status_message(self) -> str:
        return f"Ready · {self._id}" if self._configured else f"{self._id} API key not configured."

    async def generate_async(
        self,
        messages: Sequence[ChatMessage],
        options: Optional[GenerationOptions] = None,
    ) -> str:
        if not self._configured:
            return f"[{self.status_message}]"
        if self._fail:
            raise RuntimeError(f"{self._id} simulated failure")
        return self._reply

    async def stream_async(
        self,
        messages: Sequence[ChatMessage],
        options: Optional[GenerationOptions] = None,
    ) -> AsyncGenerator[str, None]:
        if not self._configured:
            yield f"[{self.status_message}]"
            return
        if self._fail:
            raise RuntimeError(f"{self._id} simulated failure")
        for i in range(0, len(self._reply), self._chunk_size):
            yield self._reply[i : i + self._chunk_size]

    async def save_session_async(self, path: str) -> bool:
        return False

    async def load_session_async(self, path: str) -> bool:
        return False
