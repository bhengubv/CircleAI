# streaming_tool_progress.py
#
# Port of CircleAI.Telephony StreamingToolProgress.cs (C# — the EXACT spec).
#
# (3.3.0) Long-running tools push progress updates (% complete + status text)
# while they run, so the AI can keep the caller informed.
#
# C# delegate StreamingToolHandler -> an async Callable taking (arguments_json,
# progress_sink, ct). C# static StreamingToolRunner -> a module function. C#
# Func<DateTimeOffset> clock -> Callable[[], datetime]. The BriefingSynthesiser
# TTS delegate lives in warm_transfer_orchestrator (imported here).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Awaitable, Callable, List, Optional

from .contracts import ICallSession
from .primitives import AudioFrame, CallMediaFormat
from .tool_calling import ToolInvocation, ToolResult
from .warm_transfer_orchestrator import BriefingSynthesiser


@dataclass(frozen=True, slots=True)
class ToolProgressUpdate:
    """(3.3.0) One progress update from a streaming tool.

    Mirrors ``record(string CallId, float PercentComplete, string? StatusText,
    DateTimeOffset EmittedAt)``.
    """

    call_id: str
    percent_complete: float
    status_text: Optional[str]
    emitted_at: datetime


class IToolProgressSink(ABC):
    """(3.3.0) The sink a tool pushes progress updates into."""

    @abstractmethod
    async def emit_async(self, update: ToolProgressUpdate, *, ct: Optional[object] = None) -> None:
        """Emit one update. Implementations decide whether to forward to the caller."""


# (3.3.0) Streaming tool handler — accepts a progress sink it can push updates
# into. C# ``delegate ValueTask<string> StreamingToolHandler(string
# argumentsJson, IToolProgressSink progressSink, CancellationToken ct)``.
StreamingToolHandler = Callable[..., Awaitable[str]]


class SpokenToolProgressSink(IToolProgressSink):
    """(3.3.0) Default sink that throttles updates (>= ``min_interval`` apart) and
    speaks each via TTS to the active call session."""

    def __init__(
        self,
        session: ICallSession,
        tts: BriefingSynthesiser,
        min_interval: Optional[timedelta] = None,
        clock: Optional[Callable[[], datetime]] = None,
    ) -> None:
        if session is None:
            raise ValueError("session must not be None")
        if tts is None:
            raise ValueError("tts must not be None")
        self._session = session
        self._tts = tts
        self._min_interval = min_interval if min_interval is not None else timedelta(seconds=2)
        self._clock = clock if clock is not None else (lambda: datetime.now(timezone.utc))
        self._gate = threading.Lock()
        self._last_spoken = datetime.fromtimestamp(0, tz=timezone.utc)

    async def emit_async(self, update: ToolProgressUpdate, *, ct: Optional[object] = None) -> None:
        if update is None:
            raise ValueError("update must not be None")
        if not update.status_text or update.status_text.isspace():
            return

        now = self._clock()
        with self._gate:
            should_speak = (now - self._last_spoken) >= self._min_interval
            if should_speak:
                self._last_spoken = now
        if not should_speak:
            return

        audio = await self._tts(update.status_text, ct)
        if audio:
            await self._session.send_audio_async(
                AudioFrame(audio, CallMediaFormat.PCM24000, timedelta(0)), ct=ct
            )


class RecordingToolProgressSink(IToolProgressSink):
    """(3.3.0) Sink that records updates for observability without speaking them."""

    def __init__(self) -> None:
        self._gate = threading.Lock()
        self._updates: List[ToolProgressUpdate] = []

    @property
    def updates(self) -> List[ToolProgressUpdate]:
        with self._gate:
            return list(self._updates)

    async def emit_async(self, update: ToolProgressUpdate, *, ct: Optional[object] = None) -> None:
        if update is None:
            raise ValueError("update must not be None")
        with self._gate:
            self._updates.append(update)


async def run_streaming_tool_async(
    invocation: ToolInvocation,
    handler: StreamingToolHandler,
    sink: IToolProgressSink,
    *,
    ct: Optional[object] = None,
) -> ToolResult:
    """(3.3.0) Run a streaming tool handler against a progress sink.

    Mirrors ``StreamingToolRunner.RunAsync``.
    """
    if invocation is None:
        raise ValueError("invocation must not be None")
    if handler is None:
        raise ValueError("handler must not be None")
    if sink is None:
        raise ValueError("sink must not be None")

    try:
        result_json = await handler(invocation.arguments_json, sink, ct)
        return ToolResult(invocation.call_id, True, result_json if result_json else "{}")
    except Exception as ex:
        return ToolResult(invocation.call_id, False, "{}", str(ex))
