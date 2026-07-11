# contracts.py
#
# Port of CircleAI.Telephony Contracts.cs (C# — the EXACT spec).
#
# (3.3.0) The CircleAI.Telephony contract surface — carrier-agnostic. Any
# consumer (txtMe, Panik, salon receptionist) talks to this; the real Twilio /
# Telnyx / Plivo adapters ship as sibling packages.
#
# C# ValueTask<T>            -> async def -> T
# C# IAsyncEnumerable<T>     -> AsyncIterator[T] (async generator)
# C# IAsyncDisposable        -> dispose_async()  (+ async context manager)
# C# IDisposable             -> the IDisposable ABC (dispose() + with-support)
# C# interface              -> abc.ABC with @abstractmethod
# C# event EventHandler<T>   -> add_/remove_ callback pair taking Callable[[obj, T], None]
# C# CancellationToken ct    -> keyword-only ct: Optional[object] = None (cooperative; unused by null impls)

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import AsyncIterator, Callable, List, Optional

from .primitives import (
    AudioFrame,
    CallInfo,
    CallMediaFormat,
    CallStatus,
    DtmfEvent,
    ProvisionedNumber,
    TransferMode,
)

# C# event EventHandler<CallStatus> handler signature: (object? sender, CallStatus e).
StatusChangedHandler = Callable[[object, CallStatus], None]


class OutboundDialOptions:
    """(3.3.0) Optional knobs for an outbound dial.

    Mirrors ``CircleAI.Telephony.OutboundDialOptions`` — a ``sealed record``
    with ``init`` properties and C# defaults. Kept a plain class (not a frozen
    dataclass) so ``follow_me_numbers`` can default to ``None`` while the other
    scalar defaults match the C# ``init`` defaults exactly.
    """

    __slots__ = (
        "detect_answering_machine",
        "ring_timeout_seconds",
        "caller_id_override",
        "follow_me_numbers",
    )

    def __init__(
        self,
        detect_answering_machine: bool = False,
        ring_timeout_seconds: int = 30,
        caller_id_override: Optional[str] = None,
        follow_me_numbers: Optional[List[str]] = None,
    ) -> None:
        #: If true, detect voicemail and surface ``CallStatus.VOICEMAIL``.
        self.detect_answering_machine = detect_answering_machine
        #: How long to ring before treating it as no-answer. Default 30 s.
        self.ring_timeout_seconds = ring_timeout_seconds
        #: Optional caller-id override (must be a number you own).
        self.caller_id_override = caller_id_override
        #: Optional E.164 numbers to also dial if the primary doesn't answer.
        self.follow_me_numbers = follow_me_numbers


class ITelephonyCarrier(ABC):
    """(3.3.0) Carrier integration — the place where CircleAI talks to a
    phone-network operator (Twilio, Telnyx, Plivo, or a SIP gateway).

    Inbound: carrier delivers a call to us -> carrier emits :class:`ICallSession`
    via the host's webhook plumbing. Outbound: caller asks us to dial -> we call
    :meth:`dial_async`.
    """

    @property
    @abstractmethod
    def carrier_id(self) -> str:
        """Stable carrier id — "twilio" / "telnyx" / "plivo" / "null"."""

    @property
    @abstractmethod
    def is_configured(self) -> bool:
        """True when the carrier has the credentials + base addresses it needs."""

    @abstractmethod
    async def provision_number_async(
        self,
        country_code: str,
        area_code: Optional[str] = None,
        *,
        ct: Optional[object] = None,
    ) -> ProvisionedNumber:
        """Buy a new phone number from this carrier for the given country code
        (ISO 3166-1 alpha-2, e.g. "ZA"). Caller chooses one of the offered area
        codes via ``area_code``; pass ``None`` for "any"."""

    @abstractmethod
    async def configure_inbound_webhook_async(
        self,
        phone_number: str,
        inbound_webhook: str,
        *,
        ct: Optional[object] = None,
    ) -> None:
        """Configure a number we already own to route inbound calls to our
        host-provided WebSocket endpoint."""

    @abstractmethod
    async def dial_async(
        self,
        from_number: str,
        to_number: str,
        stream_url: str,
        options: Optional[OutboundDialOptions] = None,
        *,
        ct: Optional[object] = None,
    ) -> "ICallSession":
        """Place an outbound call. ``stream_url`` is where the carrier should
        stream the live media (WebSocket URL on our host). Returns a session the
        caller can attach an agent to."""

    @abstractmethod
    async def list_numbers_async(self, *, ct: Optional[object] = None) -> List[ProvisionedNumber]:
        """List the numbers we own on this carrier."""


class ICallSession(ABC):
    """(3.3.0) Live call session. The agent talks to this — it doesn't know or
    care which carrier is on the other side. Audio in / audio out / hang up /
    transfer / DTMF.

    C# ``IAsyncDisposable`` -> :meth:`dispose_async` plus async-context-manager
    support (``async with session:``).
    """

    @property
    @abstractmethod
    def info(self) -> CallInfo:
        """Stable carrier-supplied info captured at call start."""

    @property
    @abstractmethod
    def status(self) -> CallStatus:
        """Current lifecycle status (Active / EndedByCaller / Transferred / ...)."""

    @abstractmethod
    def receive_audio_async(self, *, ct: Optional[object] = None) -> AsyncIterator[AudioFrame]:
        """Audio frames arriving from the caller. Cancel to stop receiving."""

    @abstractmethod
    async def send_audio_async(self, frame: AudioFrame, *, ct: Optional[object] = None) -> None:
        """Send an audio frame to the caller."""

    @abstractmethod
    def receive_dtmf_async(self, *, ct: Optional[object] = None) -> AsyncIterator[DtmfEvent]:
        """DTMF tones the caller is pressing."""

    @abstractmethod
    async def send_dtmf_async(self, digits: str, *, ct: Optional[object] = None) -> None:
        """Send DTMF tones from the AI side (for navigating other people's menus)."""

    @abstractmethod
    async def transfer_async(
        self,
        target_number: str,
        mode: TransferMode,
        briefing: Optional[str] = None,
        *,
        ct: Optional[object] = None,
    ) -> None:
        """Transfer the call to ``target_number``. Cold = drop and forget. Warm =
        park the caller, dial the human, brief them, bridge both."""

    @abstractmethod
    async def hang_up_async(self, *, ct: Optional[object] = None) -> None:
        """End the call from our side."""

    @abstractmethod
    def add_status_changed(self, handler: StatusChangedHandler) -> None:
        """Subscribe to lifecycle status changes. Mirrors the C# ``StatusChanged``
        event's ``+=``."""

    @abstractmethod
    def remove_status_changed(self, handler: StatusChangedHandler) -> None:
        """Unsubscribe a handler. Mirrors the C# ``StatusChanged`` event's ``-=``."""

    # --- IAsyncDisposable ---------------------------------------------------

    @abstractmethod
    async def dispose_async(self) -> None:
        """Release the session (C# ``IAsyncDisposable.DisposeAsync``)."""

    async def __aenter__(self) -> "ICallSession":
        return self

    async def __aexit__(self, *exc_info: object) -> None:
        await self.dispose_async()


class IInboundCallDispatcher(ABC):
    """(3.3.0) Inbound webhook dispatcher — the carrier-provided HTTP handler
    (host wires this into ASP.NET routing) calls into the dispatcher to
    materialise an :class:`ICallSession` the agent can attach to."""

    @property
    @abstractmethod
    def carrier_id(self) -> str:
        """Stable id of the carrier feeding inbound calls into this dispatcher."""

    @abstractmethod
    def subscribe(self, handler: Callable[["ICallSession"], "object"]) -> "object":
        """Subscribe to inbound call sessions. Each new call yields a session the
        consumer attaches their agent to. ``handler`` returns an awaitable
        (C# ``Func<ICallSession, ValueTask>``); returns an :class:`IDisposable`."""
