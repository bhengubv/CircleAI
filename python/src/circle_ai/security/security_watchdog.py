# security_watchdog.py
#
# Port of CircleAI.Security.ISecurityWatchdog + DefaultSecurityWatchdog
# (C# — the EXACT spec).
#
# The central contract for the CircleAI local runtime immune system.
#
# Detection sites (companion pipeline, biometric verifier, agent patch gate)
# call on_anomaly_detected_async when they observe something suspicious.
# The watchdog implementation decides the response:
#   key rotation, session revocation, mesh isolation, or state rollback.
#
# The SDK ships DefaultSecurityWatchdog as the out-of-box implementation.
# Host applications can substitute their own (e.g. one that also pages the
# ops-security agent).
#
# NOTE (from the C# CircleAIVerificationStatus): the signal stream uses an
# in-process unbounded channel. Single-process correct. NOT multi-replica safe
# — signals emitted on replica A do not reach stream subscribers on replica B.

from __future__ import annotations

import asyncio
from abc import ABC, abstractmethod
from typing import AsyncIterator, List, Optional

from .anomaly_signal import AnomalySignal
from .security_checkpoint import SecurityCheckpoint
from .security_response import SecurityResponse, SecurityResponseKind
from .threat_vector import ThreatVector

# Graduated-response thresholds — match the C# constants exactly.
_ROTATION_THRESHOLD = 0.30
_COMPOSITE_THRESHOLD = 0.60


class ISecurityWatchdog(ABC):
    """Central contract for the CircleAI local runtime immune system.

    Receives :class:`AnomalySignal` instances from detection sites and returns
    the :class:`SecurityResponse` describing protective action taken.
    """

    @abstractmethod
    async def on_anomaly_detected_async(
        self,
        signal: AnomalySignal,
        checkpoint: Optional[SecurityCheckpoint] = None,
        ct: Optional[object] = None,
    ) -> SecurityResponse:
        """Called by any detection site when a local runtime anomaly is
        observed. The watchdog evaluates ``signal`` and applies the appropriate
        protective response.
        """
        ...

    @abstractmethod
    def stream_signals_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[AnomalySignal]:
        """Return a live stream of every :class:`AnomalySignal` observed since
        the watchdog started. Completes when ``ct`` is cancelled.
        """
        ...


class _SignalChannel:
    """Minimal unbounded signal channel mirroring the C# ``Channel``
    ``.CreateUnbounded<AnomalySignal>`` in ``DefaultSecurityWatchdog``.

    ``write`` NEVER blocks and buffers even with no reader attached; a
    subscriber that attaches later still receives signals written before it
    started reading (unbounded retention). Concurrent readers are competing
    consumers, matching ``ReadAllAsync``.
    """

    def __init__(self) -> None:
        self._queue: "asyncio.Queue[AnomalySignal]" = asyncio.Queue()

    def write(self, signal: AnomalySignal) -> None:
        self._queue.put_nowait(signal)

    async def read_all_async(self, ct: Optional[object] = None):
        while True:
            signal = await self._queue.get()
            yield signal


class DefaultSecurityWatchdog(ISecurityWatchdog):
    """Default in-process watchdog. Applies graduated responses based on
    :class:`ThreatVector` and confidence level:

    * Confidence < 0.30 -> ``NO_ACTION``
    * Confidence 0.30-0.60 -> ``KEY_ROTATION``
    * Confidence > 0.60 + confusion/pivot/escalation -> ``COMPOSITE``
      (rotation + mesh signal)
    * Any verified checkpoint on a high-severity vector -> ``STATE_ROLLBACK``
      added to the composite

    Host applications can replace this with a watchdog that also invokes
    ops-security agents.
    """

    def __init__(self) -> None:
        self._signals = _SignalChannel()

    @property
    def component_name(self) -> str:
        return "DefaultSecurityWatchdog"

    async def on_anomaly_detected_async(
        self,
        signal: AnomalySignal,
        checkpoint: Optional[SecurityCheckpoint] = None,
        ct: Optional[object] = None,
    ) -> SecurityResponse:
        if signal is None:
            raise ValueError("signal must not be None")

        # Broadcast to any stream subscribers.
        self._signals.write(signal)

        # ── Graduated response policy ────────────────────────────────────────

        if signal.confidence < _ROTATION_THRESHOLD:
            return SecurityResponse.no_action(
                signal.id,
                f"Confidence {signal.confidence:.0%} below rotation threshold "
                f"— monitoring only.",
            )

        # High-severity vectors always warrant rollback if we have a checkpoint.
        is_high_severity = signal.vector in (
            ThreatVector.CONTROL_FLOW_DRIFT,
            ThreatVector.PRIVILEGE_ESCALATION,
            ThreatVector.NETWORK_PIVOT,
            ThreatVector.STATE_CORRUPTION,
        )

        if signal.confidence > _COMPOSITE_THRESHOLD:
            actions: List[SecurityResponseKind] = [
                SecurityResponseKind.KEY_ROTATION,
                SecurityResponseKind.MESH_ISOLATION_SIGNAL,
            ]

            restored: Optional[SecurityCheckpoint] = None
            if checkpoint is not None and is_high_severity and checkpoint.verify():
                actions.append(SecurityResponseKind.STATE_ROLLBACK)
                restored = checkpoint

            return SecurityResponse.composite(
                signal.id,
                actions,
                f"Composite response for {signal.vector.name} "
                f"(confidence {signal.confidence:.0%}) in {signal.affected_module}.",
                restored,
            )

        # Mid-range confidence: rotate keys only.
        return SecurityResponse.for_key_rotation(
            signal.id,
            f"Key rotation triggered for {signal.vector.name} "
            f"(confidence {signal.confidence:.0%}) in {signal.affected_module}.",
        )

    async def stream_signals_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[AnomalySignal]:
        async for signal in self._signals.read_all_async(ct):
            yield signal
