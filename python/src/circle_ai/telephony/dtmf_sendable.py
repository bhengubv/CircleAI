# dtmf_sendable.py
#
# Port of CircleAI.Telephony IDtmfSendable.cs (C# — the EXACT spec).
#
# (3.3.0) Optional sister interface a host can layer on its IMediaStream
# implementation to support carrier-native out-of-band DTMF (e.g. Twilio's mark
# control frame, Telnyx Call Control send_dtmf, Plivo Audio Streaming control
# event). When the media stream doesn't implement this, the session falls back
# to in-band tones via DtmfToneGenerator.

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Optional


class IDtmfSendable(ABC):
    """(3.3.0) Optional carrier-native out-of-band DTMF sender."""

    @abstractmethod
    async def send_dtmf_async(self, digits: str, *, ct: Optional[object] = None) -> None:
        ...
