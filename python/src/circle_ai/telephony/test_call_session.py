# test_call_session.py
#
# Port of CircleAI.Telephony TestCallSession.cs (C# — the EXACT spec).
#
# (3.3.0) Build voice loops without paying for a real carrier minute.
# TestCallSession is an in-memory ICallSession that lets a test harness inject
# inbound audio + DTMF, capture outbound audio, and drive lifecycle events on
# demand.
#
# C# Channel<T> (unbounded, single-reader / multi-writer) -> an asyncio.Queue
# with a _CLOSED sentinel to end the async-iteration (the tree's canonical
# Channel-port idiom, per circle_ai.networking). C# event EventHandler<CallStatus>
# -> an add_/remove_status_changed handler list; TriggerStatusChange invokes each
# with (self, status). Guid.NewGuid().ToString("n") -> uuid.uuid4().hex.

from __future__ import annotations

import asyncio
import threading
import uuid
from datetime import datetime, timezone
from typing import AsyncIterator, List, Optional

from .contracts import ICallSession, StatusChangedHandler
from .primitives import (
    AudioFrame,
    CallDirection,
    CallInfo,
    CallMediaFormat,
    CallStatus,
    DtmfEvent,
    TransferMode,
)

_CLOSED = object()


class TestCallSession(ICallSession):
    """(3.3.0) In-memory ICallSession for harnesses + unit tests."""

    def __init__(self, info: Optional[CallInfo] = None) -> None:
        self._inbound_audio: "asyncio.Queue[object]" = asyncio.Queue()
        self._inbound_dtmf: "asyncio.Queue[object]" = asyncio.Queue()
        self._outbound_audio: List[AudioFrame] = []
        self._outbound_dtmf: List[str] = []
        self._gate = threading.Lock()
        self._audio_lock = threading.Lock()
        self._dtmf_lock = threading.Lock()
        self._status = CallStatus.ACTIVE
        self._status_handlers: List[StatusChangedHandler] = []
        self._info = info if info is not None else CallInfo(
            call_id=uuid.uuid4().hex,
            direction=CallDirection.INBOUND,
            from_="+15555550100",
            to="+15555550200",
            carrier_id="test",
            media_format=CallMediaFormat.PCM16000,
            started_at_utc=datetime.now(timezone.utc),
        )

    @property
    def info(self) -> CallInfo:
        return self._info

    @property
    def status(self) -> CallStatus:
        with self._gate:
            return self._status

    def add_status_changed(self, handler: StatusChangedHandler) -> None:
        if handler is None:
            raise ValueError("handler must not be None")
        with self._gate:
            self._status_handlers.append(handler)

    def remove_status_changed(self, handler: StatusChangedHandler) -> None:
        with self._gate:
            try:
                self._status_handlers.remove(handler)
            except ValueError:
                pass

    @property
    def sent_audio_frames(self) -> List[AudioFrame]:
        """(3.3.0) Outbound audio frames the AI has emitted, captured for assertions."""
        with self._audio_lock:
            return list(self._outbound_audio)

    @property
    def sent_dtmf(self) -> List[str]:
        """(3.3.0) Outbound DTMF strings the AI has emitted."""
        with self._dtmf_lock:
            return list(self._outbound_dtmf)

    def inject_inbound_audio(self, frame: AudioFrame) -> None:
        """(3.3.0) Inject one inbound audio frame for the AI to consume via receive_audio_async."""
        if frame is None:
            raise ValueError("frame must not be None")
        self._inbound_audio.put_nowait(frame)

    def inject_inbound_dtmf(self, ev: DtmfEvent) -> None:
        """(3.3.0) Inject one inbound DTMF event."""
        if ev is None:
            raise ValueError("ev must not be None")
        self._inbound_dtmf.put_nowait(ev)

    def end_inbound_streams(self) -> None:
        """(3.3.0) Stop the inbound streams cleanly."""
        self._inbound_audio.put_nowait(_CLOSED)
        self._inbound_dtmf.put_nowait(_CLOSED)

    def trigger_status_change(self, new_status: CallStatus) -> None:
        """(3.3.0) Trigger a status change (e.g. caller hangs up)."""
        with self._gate:
            self._status = new_status
            handlers = list(self._status_handlers)
        for handler in handlers:
            handler(self, new_status)

    async def receive_audio_async(self, *, ct: Optional[object] = None) -> AsyncIterator[AudioFrame]:
        while True:
            item = await self._inbound_audio.get()
            if item is _CLOSED:
                return
            yield item  # type: ignore[misc]

    async def receive_dtmf_async(self, *, ct: Optional[object] = None) -> AsyncIterator[DtmfEvent]:
        while True:
            item = await self._inbound_dtmf.get()
            if item is _CLOSED:
                return
            yield item  # type: ignore[misc]

    async def send_audio_async(self, frame: AudioFrame, *, ct: Optional[object] = None) -> None:
        if frame is None:
            raise ValueError("frame must not be None")
        with self._audio_lock:
            self._outbound_audio.append(frame)

    async def send_dtmf_async(self, digits: str, *, ct: Optional[object] = None) -> None:
        if digits is None:
            raise ValueError("digits must not be None")
        with self._dtmf_lock:
            self._outbound_dtmf.append(digits)

    async def transfer_async(
        self,
        target_number: str,
        mode: TransferMode,
        briefing: Optional[str] = None,
        *,
        ct: Optional[object] = None,
    ) -> None:
        self.trigger_status_change(CallStatus.TRANSFERRED)

    async def hang_up_async(self, *, ct: Optional[object] = None) -> None:
        self.trigger_status_change(CallStatus.ENDED_BY_AGENT)
        self.end_inbound_streams()

    async def dispose_async(self) -> None:
        self.end_inbound_streams()
