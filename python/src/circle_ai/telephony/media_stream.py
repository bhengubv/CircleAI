# media_stream.py
#
# Port of CircleAI.Telephony IMediaStream.cs (C# — the EXACT spec).
#
# (3.3.0) Host-supplied media stream abstraction shared across all carriers
# (Twilio, Telnyx, Plivo, etc.). The carrier session reads from / writes to
# this; the ASP.NET host wires the carrier's media-streaming WebSocket against
# it. Keeping this carrier-agnostic lets the carrier packages stay framework-free.
#
# C# IAsyncDisposable -> dispose_async() + async-context-manager support.
# C# event EventHandler<CallStatus> -> add_/remove_status_changed callback pair.

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import AsyncIterator, Optional

from .contracts import StatusChangedHandler
from .primitives import AudioFrame, CallInfo, CallStatus, DtmfEvent


class IMediaStream(ABC):
    """(3.3.0) A live media channel for one call. The carrier host's WebSocket
    handler implements this; the carrier session consumes it."""

    @property
    @abstractmethod
    def call_info(self) -> CallInfo:
        """The carrier call id + metadata captured at connect."""

    @abstractmethod
    def receive_audio_async(self, *, ct: Optional[object] = None) -> AsyncIterator[AudioFrame]:
        """Inbound audio frames from the caller."""

    @abstractmethod
    async def send_audio_async(self, frame: AudioFrame, *, ct: Optional[object] = None) -> None:
        """Outbound audio frames to the caller."""

    @abstractmethod
    def receive_dtmf_async(self, *, ct: Optional[object] = None) -> AsyncIterator[DtmfEvent]:
        """Inbound DTMF events."""

    @abstractmethod
    async def end_async(self, *, ct: Optional[object] = None) -> None:
        """Mark the call ended from our side. Closes the WebSocket."""

    @abstractmethod
    def add_status_changed(self, handler: StatusChangedHandler) -> None:
        """Fires when the carrier reports the call status changed (C# ``+=``)."""

    @abstractmethod
    def remove_status_changed(self, handler: StatusChangedHandler) -> None:
        """Unsubscribe a status-changed handler (C# ``-=``)."""

    @property
    @abstractmethod
    def current_status(self) -> CallStatus:
        """The current lifecycle state."""

    # --- IAsyncDisposable ---------------------------------------------------

    @abstractmethod
    async def dispose_async(self) -> None:
        """Release the stream (C# ``IAsyncDisposable.DisposeAsync``)."""

    async def __aenter__(self) -> "IMediaStream":
        return self

    async def __aexit__(self, *exc_info: object) -> None:
        await self.dispose_async()
