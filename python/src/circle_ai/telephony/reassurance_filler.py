# reassurance_filler.py
#
# Port of CircleAI.Telephony ReassuranceFiller.cs (C# — the EXACT spec).
#
# (3.3.0) When a tool call takes more than the awkward-silence threshold
# (~600 ms) the AI plays a filler line like "Give me a moment to check that…" so
# the caller doesn't think the line dropped.
#
# C# linked CancellationTokenSource + background Task.Delay loop -> an asyncio
# background task the driver cancels when the work completes (or throws). The
# CancelledError swallow mirrors the C# ``catch (OperationCanceledException)``
# around the awaited fillerTask. Rotation counters use a lock (C# Interlocked).
# The generic work delegate ``Func<CancellationToken, Task<T>>`` -> an async
# Callable; ``T`` is returned unchanged.

from __future__ import annotations

import asyncio
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import timedelta
from typing import Awaitable, Callable, ClassVar, List, Optional, TypeVar

from .contracts import ICallSession
from .primitives import AudioFrame, CallMediaFormat
from .warm_transfer_orchestrator import BriefingSynthesiser

T = TypeVar("T")


@dataclass(frozen=True, slots=True)
class ReassuranceVocabulary:
    """(3.3.0) Phrases the filler picks from. Rotated to avoid repetition."""

    short_fillers: List[str]
    long_fillers: List[str]

    #: (3.3.0) Sensible English defaults. ClassVar so it is not a dataclass field.
    Default: ClassVar["ReassuranceVocabulary"]


ReassuranceVocabulary.Default = ReassuranceVocabulary(
    short_fillers=[
        "One moment.",
        "Let me check.",
        "Give me a sec.",
        "Just a moment.",
    ],
    long_fillers=[
        "Still looking that up for you.",
        "This is taking a bit longer than usual — bear with me.",
        "Almost there — still pulling that information.",
        "Thanks for your patience, I'm checking that now.",
    ],
)


@dataclass(frozen=True, slots=True)
class ReassuranceFillerOptions:
    """(3.3.0) Configuration for the filler driver.

    ``short_filler_after``: silence after which to play a short filler (default 600 ms).
    ``long_filler_every``: cadence for long fillers after the first short one (default 3 s).
    ``vocabulary``: phrase pool.
    """

    short_filler_after: Optional[timedelta] = None
    long_filler_every: Optional[timedelta] = None
    vocabulary: Optional[ReassuranceVocabulary] = None

    @property
    def short_filler_after_or_default(self) -> timedelta:
        return self.short_filler_after if self.short_filler_after is not None else timedelta(milliseconds=600)

    @property
    def long_filler_every_or_default(self) -> timedelta:
        return self.long_filler_every if self.long_filler_every is not None else timedelta(seconds=3)

    @property
    def vocabulary_or_default(self) -> ReassuranceVocabulary:
        return self.vocabulary if self.vocabulary is not None else ReassuranceVocabulary.Default


class IReassuranceFiller(ABC):
    """(3.3.0) Driver that plays fillers while a long task runs."""

    @abstractmethod
    async def run_with_filler_async(
        self,
        work: Callable[[Optional[object]], Awaitable[T]],
        session: ICallSession,
        tts: BriefingSynthesiser,
        *,
        ct: Optional[object] = None,
    ) -> T:
        """Run ``work``. If it doesn't complete before the short-filler threshold,
        speak a short phrase via ``tts``; while still pending speak long phrases on
        the configured cadence. Returns the work's result."""


class DefaultReassuranceFiller(IReassuranceFiller):
    """(3.3.0) Default in-memory filler driver."""

    def __init__(self, options: Optional[ReassuranceFillerOptions] = None) -> None:
        self._options = options if options is not None else ReassuranceFillerOptions()
        self._lock = threading.Lock()
        self._short_rotation = 0
        self._long_rotation = 0

    async def run_with_filler_async(
        self,
        work: Callable[[Optional[object]], Awaitable[T]],
        session: ICallSession,
        tts: BriefingSynthesiser,
        *,
        ct: Optional[object] = None,
    ) -> T:
        if work is None:
            raise ValueError("work must not be None")
        if session is None:
            raise ValueError("session must not be None")
        if tts is None:
            raise ValueError("tts must not be None")

        filler_task = asyncio.ensure_future(self._speak_fillers_async(session, tts, ct))
        try:
            result = await work(ct)
            filler_task.cancel()
            try:
                await filler_task
            except asyncio.CancelledError:
                pass
            return result
        except BaseException:
            filler_task.cancel()
            try:
                await filler_task
            except asyncio.CancelledError:
                pass
            raise

    async def _speak_fillers_async(
        self, session: ICallSession, tts: BriefingSynthesiser, ct: Optional[object]
    ) -> None:
        vocab = self._options.vocabulary_or_default
        try:
            await asyncio.sleep(self._options.short_filler_after_or_default.total_seconds())
            await self._speak_async(session, tts, self._next_short(vocab), ct)

            while True:
                await asyncio.sleep(self._options.long_filler_every_or_default.total_seconds())
                await self._speak_async(session, tts, self._next_long(vocab), ct)
        except asyncio.CancelledError:
            # expected when work finishes
            raise

    def _next_short(self, v: ReassuranceVocabulary) -> str:
        if len(v.short_fillers) == 0:
            return "One moment."
        with self._lock:
            self._short_rotation += 1
            idx = self._short_rotation - 1
        return v.short_fillers[abs(idx) % len(v.short_fillers)]

    def _next_long(self, v: ReassuranceVocabulary) -> str:
        if len(v.long_fillers) == 0:
            return "Almost there."
        with self._lock:
            self._long_rotation += 1
            idx = self._long_rotation - 1
        return v.long_fillers[abs(idx) % len(v.long_fillers)]

    @staticmethod
    async def _speak_async(
        session: ICallSession, tts: BriefingSynthesiser, text: str, ct: Optional[object]
    ) -> None:
        audio = await tts(text, ct)
        if audio:
            await session.send_audio_async(
                AudioFrame(audio, CallMediaFormat.PCM24000, timedelta(0)), ct=ct
            )
