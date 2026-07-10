# anomaly_event_dispatcher.py
#
# Port of CircleAI.Security.IAnomalyEventDispatcher +
# DefaultAnomalyEventDispatcher + AnomalyDispatchOutcome + AnomalyDispatchResult
# (C# — the EXACT spec).
#
# Safe-by-default composer around ISecurityWatchdog.
#
# The bare ISecurityWatchdog.on_anomaly_detected_async path requires the caller
# to verify the signal (origin trust, schema, threshold gate) and dedupe (by id,
# by composite hash) themselves. The dispatcher folds verify -> dedup -> invoke
# into one call so a production consumer cannot accidentally accept an unverified
# or replayed signal. No exception is thrown on rejection so the caller can
# branch on the outcome without try/except.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from enum import IntEnum
from typing import Optional, Set
from uuid import UUID

from .anomaly_signal import AnomalySignal
from .security_checkpoint import SecurityCheckpoint
from .security_response import SecurityResponse
from .security_watchdog import ISecurityWatchdog


class AnomalyDispatchOutcome(IntEnum):
    """Outcome of a :meth:`IAnomalyEventDispatcher.verify_and_dispatch_async`
    call.
    """

    # Signal accepted; watchdog was invoked.
    DISPATCHED = 0
    # Signal id was already seen — deduped silently.
    DUPLICATE = 1
    # Confidence was below the configured threshold — ignored.
    BELOW_THRESHOLD = 2
    # Signal failed the origin/signature verification step.
    UNVERIFIED = 3
    # Cancellation token tripped before dispatch.
    CANCELLED = 4


@dataclass(frozen=True, slots=True)
class AnomalyDispatchResult:
    """Result of a dispatch attempt.

    :param outcome: What the dispatcher did with the signal.
    :param response: The watchdog response, when :attr:`outcome` is
        :attr:`AnomalyDispatchOutcome.DISPATCHED`. ``None`` otherwise.
    """

    outcome: AnomalyDispatchOutcome
    response: Optional[SecurityResponse]


class IAnomalyEventDispatcher(ABC):
    """Verify, dedup, and dispatch an :class:`AnomalySignal` in a single call.

    Returns an :class:`AnomalyDispatchResult` describing what happened — no
    exception is thrown on rejection so the caller can branch on the outcome
    without try/except.
    """

    @abstractmethod
    async def verify_and_dispatch_async(
        self,
        signal: AnomalySignal,
        checkpoint: Optional[SecurityCheckpoint] = None,
        ct: Optional[object] = None,
    ) -> AnomalyDispatchResult:
        """Run the verification pipeline configured on this dispatcher (origin
        trust, optional signature check, confidence threshold) and, when all
        gates pass, hand the signal to the wrapped :class:`ISecurityWatchdog`.
        """
        ...


def _is_cancelled(ct: Optional[object]) -> bool:
    """Best-effort cancellation probe. Accepts anything exposing a boolean
    ``is_cancellation_requested`` / ``cancelled`` / ``is_set()`` — mirrors the
    C# ``ct.IsCancellationRequested`` gate without binding to one token type.
    """
    if ct is None:
        return False
    for attr in ("is_cancellation_requested", "cancelled"):
        val = getattr(ct, attr, None)
        if isinstance(val, bool):
            return val
    is_set = getattr(ct, "is_set", None)
    if callable(is_set):
        try:
            return bool(is_set())
        except Exception:
            return False
    return False


class DefaultAnomalyEventDispatcher(IAnomalyEventDispatcher):
    """Default in-process dispatcher. Threshold-gated, id-deduped, no signature
    verification (configure your own by composing this with a
    signature-verifying wrapper when running over an untrusted transport).
    """

    def __init__(
        self, watchdog: ISecurityWatchdog, minimum_confidence: float = 0.30
    ) -> None:
        """Create the dispatcher.

        :param watchdog: The watchdog to forward verified signals to.
        :param minimum_confidence: Drop signals whose
            :attr:`AnomalySignal.confidence` is below this value. Default 0.30 —
            matches the default watchdog rotation threshold so signals that
            would have been no-ops aren't even dispatched.
        """
        if watchdog is None:
            raise ValueError("watchdog must not be None")
        self._watchdog = watchdog
        self._minimum_confidence = max(0.0, min(1.0, minimum_confidence))
        self._seen: Set[UUID] = set()
        self._seen_lock = threading.Lock()

    async def verify_and_dispatch_async(
        self,
        signal: AnomalySignal,
        checkpoint: Optional[SecurityCheckpoint] = None,
        ct: Optional[object] = None,
    ) -> AnomalyDispatchResult:
        if signal is None:
            raise ValueError("signal must not be None")

        if _is_cancelled(ct):
            return AnomalyDispatchResult(AnomalyDispatchOutcome.CANCELLED, None)

        if signal.confidence < self._minimum_confidence:
            return AnomalyDispatchResult(AnomalyDispatchOutcome.BELOW_THRESHOLD, None)

        # Atomic first-add dedup — mirrors ConcurrentDictionary.TryAdd.
        with self._seen_lock:
            if signal.id in self._seen:
                return AnomalyDispatchResult(AnomalyDispatchOutcome.DUPLICATE, None)
            self._seen.add(signal.id)

        response = await self._watchdog.on_anomaly_detected_async(
            signal, checkpoint, ct
        )
        return AnomalyDispatchResult(AnomalyDispatchOutcome.DISPATCHED, response)
