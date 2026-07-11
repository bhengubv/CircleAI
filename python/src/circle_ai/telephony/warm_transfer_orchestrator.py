# warm_transfer_orchestrator.py
#
# Port of CircleAI.Telephony WarmTransferOrchestrator.cs (C# — the EXACT spec).
#
# (3.3.0) Warm call transfer: park caller, dial target, speak the briefing to
# target via TTS, then bridge by issuing a cold transfer of the caller leg to
# the target. The AI's bridge-leg call ends once the caller is connected.
#
# C# delegate BriefingSynthesiser (ValueTask<ReadOnlyMemory<byte>>(string,
# CancellationToken)) -> an async Callable returning bytes; this alias is the
# canonical TTS seam reused across handoff / preamble / filler / progress. C#
# ILogger -> stdlib logging. C# Uri -> str.

from __future__ import annotations

import logging
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import timedelta
from typing import Awaitable, Callable, Optional

from .contracts import ICallSession
from .primitives import AudioFrame, CallMediaFormat, TransferMode

_logger = logging.getLogger("CircleAI.Telephony.WarmTransferOrchestrator")

# (3.3.0) Synthesise the briefing text to PCM-16 mono. C# ``delegate
# ValueTask<ReadOnlyMemory<byte>> BriefingSynthesiser(string text,
# CancellationToken ct)``. Returns raw PCM bytes (empty bytes == C# empty memory).
BriefingSynthesiser = Callable[[str, Optional[object]], Awaitable[bytes]]


@dataclass(frozen=True, slots=True)
class WarmTransferRequest:
    """(3.3.0) One warm-transfer request.

    ``source_session``: the active call we want to transfer.
    ``target_number``: E.164 number of the person we're transferring to.
    ``briefing_text``: what the AI should say to the target before the bridge.
    ``bridge_stream_url``: WSS endpoint the carrier will hand the target leg to.
    """

    source_session: ICallSession
    target_number: str
    briefing_text: str
    bridge_stream_url: str


@dataclass(frozen=True, slots=True)
class WarmTransferResult:
    """(3.3.0) Outcome of a warm transfer.

    Mirrors ``record(bool Succeeded, string? FailureReason, ICallSession? BridgeSession)``.
    """

    succeeded: bool
    failure_reason: Optional[str]
    bridge_session: Optional[ICallSession]


class IWarmTransferOrchestrator(ABC):
    """(3.3.0) Park caller, dial target, brief, bridge."""

    @abstractmethod
    async def execute_async(
        self, request: WarmTransferRequest, *, ct: Optional[object] = None
    ) -> WarmTransferResult:
        ...


class DefaultWarmTransferOrchestrator(IWarmTransferOrchestrator):
    """(3.3.0) Carrier-agnostic warm-transfer driver."""

    def __init__(
        self,
        carrier: "object",  # ITelephonyCarrier — kept loose to avoid an import cycle
        briefing_tts: BriefingSynthesiser,
        logger: Optional[logging.Logger] = None,
    ) -> None:
        if carrier is None:
            raise ValueError("carrier must not be None")
        if briefing_tts is None:
            raise ValueError("briefing_tts must not be None")
        self._carrier = carrier
        self._briefing_tts = briefing_tts
        self._logger = logger if logger is not None else _logger

    async def execute_async(
        self, request: WarmTransferRequest, *, ct: Optional[object] = None
    ) -> WarmTransferResult:
        if request is None:
            raise ValueError("request must not be None")
        if request.source_session is None:
            return WarmTransferResult(False, "SourceSession is required", None)
        if not request.target_number or request.target_number.isspace():
            return WarmTransferResult(False, "TargetNumber is required", None)

        # 1) Dial target on a fresh leg.
        try:
            bridge_leg = await self._carrier.dial_async(
                request.source_session.info.to,
                request.target_number,
                request.bridge_stream_url,
                ct=ct,
            )
        except Exception as ex:
            self._logger.warning("Warm-transfer dial to %s failed: %s", request.target_number, ex)
            return WarmTransferResult(False, f"Failed to dial target: {ex}", None)

        # 2) Speak briefing to target.
        try:
            briefing_audio = await self._briefing_tts(request.briefing_text, ct)
            if briefing_audio:
                await bridge_leg.send_audio_async(
                    AudioFrame(briefing_audio, CallMediaFormat.PCM24000, timedelta(0)), ct=ct
                )
        except Exception as ex:
            self._logger.warning("Warm-transfer briefing failed; hanging up bridge leg: %s", ex)
            await bridge_leg.hang_up_async(ct=ct)
            return WarmTransferResult(False, f"Failed to brief target: {ex}", None)

        # 3) Hand caller off to target — this is the bridge moment.
        try:
            await request.source_session.transfer_async(
                request.target_number, TransferMode.COLD, briefing=None, ct=ct
            )
        except Exception as ex:
            self._logger.warning("Warm-transfer bridge step failed: %s", ex)
            await bridge_leg.hang_up_async(ct=ct)
            return WarmTransferResult(False, f"Failed to bridge caller: {ex}", None)

        # 4) AI leg ends; caller and target stay connected.
        await bridge_leg.hang_up_async(ct=ct)
        return WarmTransferResult(True, None, bridge_leg)
