# primitives.py
#
# Port of CircleAI.Telephony Primitives.cs (C# — the EXACT spec).
#
# (3.3.0) Shared value types for the telephony surface. Direction + call
# lifecycle states + media format negotiation primitives, kept minimal so a
# real-world inbound or outbound call needs nothing else in scope.
#
# C# enums map to enum.IntEnum (stable ordinals mirror the C# member order).
# C# records map to frozen slotted dataclasses. C# decimal (exact money) maps
# to decimal.Decimal; ReadOnlyMemory<byte> maps to bytes; TimeSpan ->
# datetime.timedelta; DateTimeOffset -> datetime.datetime.

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta
from decimal import Decimal
from enum import IntEnum
from typing import Optional


class CallDirection(IntEnum):
    """(3.3.0) Call direction. Mirrors ``CircleAI.Telephony.CallDirection``."""

    INBOUND = 0
    OUTBOUND = 1


class CallStatus(IntEnum):
    """(3.3.0) Call lifecycle states. Mirrors ``CircleAI.Telephony.CallStatus``."""

    #: Carrier accepted the dial but the other end has not picked up yet.
    RINGING = 0
    #: Both sides connected; media flowing.
    ACTIVE = 1
    #: Caller hung up.
    ENDED_BY_CALLER = 2
    #: Callee hung up.
    ENDED_BY_CALLEE = 3
    #: AI agent (us) ended the call.
    ENDED_BY_AGENT = 4
    #: Carrier-detected voicemail / answering machine on outbound dial.
    VOICEMAIL = 5
    #: Call did not connect (busy, no answer, network).
    FAILED = 6
    #: Call transferred to a human or a different agent.
    TRANSFERRED = 7


class CallMediaFormat(IntEnum):
    """(3.3.0) Audio wire formats supported across carriers.

    Mirrors ``CircleAI.Telephony.CallMediaFormat``.
    """

    #: µ-law 8 kHz mono — Twilio default, Plivo default, fallback Telnyx.
    MULAW8000 = 0
    #: A-law 8 kHz mono — some European carriers.
    ALAW8000 = 1
    #: Linear PCM 16-bit 16 kHz mono — Telnyx negotiated path.
    PCM16000 = 2
    #: Linear PCM 16-bit 24 kHz mono — high-quality WebRTC, OpenAI Realtime.
    PCM24000 = 3


class TransferMode(IntEnum):
    """(3.3.0) Transfer mode the AI requests from the carrier."""

    #: Drop the caller into the new line and hang up — fast, no context handover.
    COLD = 0
    #: Park caller, dial human, brief human verbally, then bridge both.
    WARM = 1


@dataclass(frozen=True, slots=True)
class CallInfo:
    """(3.3.0) Information about one call. Captured once at call start, immutable.

    Mirrors ``CircleAI.Telephony.CallInfo`` — ``record(string CallId,
    CallDirection Direction, string From, string To, string CarrierId,
    CallMediaFormat MediaFormat, DateTimeOffset StartedAtUtc)``.
    """

    call_id: str
    direction: CallDirection
    from_: str
    to: str
    carrier_id: str
    media_format: CallMediaFormat
    started_at_utc: datetime


@dataclass(frozen=True, slots=True)
class CallSnapshot:
    """(3.3.0) A snapshot of a call's current state. Returned by lifecycle queries.

    Mirrors ``CircleAI.Telephony.CallSnapshot``. ``cost_so_far`` is the
    per-second cost so far (carrier minutes + any LLM/STT/TTS attached).
    ``transfer_target`` is the E.164 number we transferred to, when
    ``CallStatus.TRANSFERRED``.
    """

    info: CallInfo
    status: CallStatus
    duration: timedelta
    cost_so_far: Decimal
    transfer_target: Optional[str] = None


@dataclass(frozen=True, slots=True)
class AudioFrame:
    """(3.3.0) Audio chunk flowing from caller -> AI or AI -> caller.

    Mirrors ``CircleAI.Telephony.AudioFrame`` — ``record(ReadOnlyMemory<byte>
    Pcm, CallMediaFormat Format, TimeSpan Offset)``. C# ReadOnlyMemory<byte>
    maps to ``bytes``.
    """

    pcm: bytes
    format: CallMediaFormat
    offset: timedelta


@dataclass(frozen=True, slots=True)
class DtmfEvent:
    """(3.3.0) DTMF tone from the caller.

    Mirrors ``CircleAI.Telephony.DtmfEvent`` — ``record(char Digit, TimeSpan
    Duration, TimeSpan Offset)``. C# ``char`` maps to a one-character ``str``.
    """

    digit: str
    duration: timedelta
    offset: timedelta


@dataclass(frozen=True, slots=True)
class ProvisionedNumber:
    """(3.3.0) Result of a number-provisioning request.

    Mirrors ``CircleAI.Telephony.ProvisionedNumber`` — ``record(string
    PhoneNumber, string CarrierId, DateTimeOffset ProvisionedAtUtc, decimal
    MonthlyRecurringCost)``.
    """

    phone_number: str
    carrier_id: str
    provisioned_at_utc: datetime
    monthly_recurring_cost: Decimal
