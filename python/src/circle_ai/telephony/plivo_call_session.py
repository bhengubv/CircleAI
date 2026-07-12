# plivo_call_session.py
#
# Port of CircleAI.Telephony.Plivo/PlivoCallSession.cs (C# — the EXACT spec).
#
# (3.3.0) ICallSession backed by a host-supplied IMediaStream wired to Plivo's
# Audio Streaming WebSocket. Structurally identical to TelnyxCallSession — cold
# transfer routes through the carrier's transfer_call_async (Plivo replays the
# answer XML), hang-up through end_call_async (Plivo DELETE Call).

from __future__ import annotations

from typing import AsyncIterator, List, Optional

from . import dtmf_tone_generator as DtmfToneGenerator
from .carriers_http import sample_rate_for_format
from .contracts import ICallSession, StatusChangedHandler
from .dtmf_sendable import IDtmfSendable
from .media_stream import IMediaStream
from .primitives import AudioFrame, CallInfo, CallStatus, DtmfEvent, TransferMode
from .warm_transfer_orchestrator import (
    BriefingSynthesiser,
    DefaultWarmTransferOrchestrator,
    WarmTransferRequest,
)


class PlivoCallSession(ICallSession):
    """(3.3.0) :class:`ICallSession` wrapping a Plivo media stream.

    Mirrors ``CircleAI.Telephony.Plivo.PlivoCallSession``.
    """

    def __init__(
        self,
        media: IMediaStream,
        carrier: "object",  # PlivoCarrier — loose to avoid an import cycle
        briefing_tts: Optional[BriefingSynthesiser] = None,
        bridge_stream_url: Optional[str] = None,
    ) -> None:
        if media is None:
            raise ValueError("media must not be None")
        if carrier is None:
            raise ValueError("carrier must not be None")
        self._media = media
        self._carrier = carrier
        self._briefing_tts = briefing_tts
        self._bridge_stream_url = bridge_stream_url
        self._status = CallStatus.RINGING
        self._handlers: List[StatusChangedHandler] = []
        self._media.add_status_changed(self._on_media_status_changed)

    @property
    def info(self) -> CallInfo:
        return self._media.call_info

    @property
    def status(self) -> CallStatus:
        if self._media.current_status == CallStatus.RINGING and self._status != CallStatus.RINGING:
            return self._status
        return self._media.current_status

    def receive_audio_async(self, *, ct: Optional[object] = None) -> AsyncIterator[AudioFrame]:
        return self._media.receive_audio_async(ct=ct)

    async def send_audio_async(self, frame: AudioFrame, *, ct: Optional[object] = None) -> None:
        await self._media.send_audio_async(frame, ct=ct)

    def receive_dtmf_async(self, *, ct: Optional[object] = None) -> AsyncIterator[DtmfEvent]:
        return self._media.receive_dtmf_async(ct=ct)

    async def send_dtmf_async(self, digits: str, *, ct: Optional[object] = None) -> None:
        if not digits:
            return
        if isinstance(self._media, IDtmfSendable):
            await self._media.send_dtmf_async(digits, ct=ct)
            return
        sample_rate = sample_rate_for_format(self.info.media_format)
        await DtmfToneGenerator.send_through_session_async(self, digits, sample_rate, ct=ct)

    async def transfer_async(
        self,
        target_number: str,
        mode: TransferMode,
        briefing: Optional[str] = None,
        *,
        ct: Optional[object] = None,
    ) -> None:
        if mode == TransferMode.WARM:
            if (
                self._briefing_tts is not None
                and self._bridge_stream_url is not None
                and briefing is not None
                and not briefing.isspace()
            ):
                orchestrator = DefaultWarmTransferOrchestrator(self._carrier, self._briefing_tts)
                result = await orchestrator.execute_async(
                    WarmTransferRequest(self, target_number, briefing, self._bridge_stream_url),
                    ct=ct,
                )
                if not result.succeeded:
                    raise RuntimeError(f"Warm transfer failed: {result.failure_reason}")
                return

        await self._carrier.transfer_call_async(self.info.call_id, target_number, ct=ct)
        self._set_status(CallStatus.TRANSFERRED)

    async def hang_up_async(self, *, ct: Optional[object] = None) -> None:
        self._set_status(CallStatus.ENDED_BY_AGENT)
        try:
            await self._media.end_async(ct=ct)
        except Exception:
            pass
        await self._carrier.end_call_async(self.info.call_id, ct=ct)

    def add_status_changed(self, handler: StatusChangedHandler) -> None:
        self._handlers.append(handler)

    def remove_status_changed(self, handler: StatusChangedHandler) -> None:
        try:
            self._handlers.remove(handler)
        except ValueError:
            pass

    async def dispose_async(self) -> None:
        self._media.remove_status_changed(self._on_media_status_changed)
        await self._media.dispose_async()

    def _on_media_status_changed(self, sender: object, status: CallStatus) -> None:
        self._set_status(status)

    def _set_status(self, status: CallStatus) -> None:
        if self._status == status:
            return
        self._status = status
        for handler in list(self._handlers):
            handler(self, status)


class PlivoPendingMediaStream(IMediaStream):
    """(3.3.0) Pending stream returned while the host's WebSocket attaches.

    Mirrors the C# ``PlivoPendingMediaStream``.
    """

    def __init__(self, info: CallInfo) -> None:
        self._info = info
        self._status = CallStatus.RINGING
        self._handlers: List[StatusChangedHandler] = []

    @property
    def call_info(self) -> CallInfo:
        return self._info

    @property
    def current_status(self) -> CallStatus:
        return self._status

    async def receive_audio_async(self, *, ct: Optional[object] = None) -> AsyncIterator[AudioFrame]:
        return
        yield  # pragma: no cover

    async def send_audio_async(self, frame: AudioFrame, *, ct: Optional[object] = None) -> None:
        raise RuntimeError(
            "Cannot send audio before the host's WebSocket has attached its IMediaStream."
        )

    async def receive_dtmf_async(self, *, ct: Optional[object] = None) -> AsyncIterator[DtmfEvent]:
        return
        yield  # pragma: no cover

    async def end_async(self, *, ct: Optional[object] = None) -> None:
        self._status = CallStatus.ENDED_BY_AGENT
        for handler in list(self._handlers):
            handler(self, self._status)

    def add_status_changed(self, handler: StatusChangedHandler) -> None:
        self._handlers.append(handler)

    def remove_status_changed(self, handler: StatusChangedHandler) -> None:
        try:
            self._handlers.remove(handler)
        except ValueError:
            pass

    async def dispose_async(self) -> None:
        return None


__all__ = ["PlivoCallSession", "PlivoPendingMediaStream"]
