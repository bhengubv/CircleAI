"""test_anomaly_dispatcher.py — DefaultAnomalyEventDispatcher verify/dedup/dispatch.

Folds threshold gate + id dedup + watchdog invocation into one call, returning
an AnomalyDispatchResult instead of throwing on rejection.
"""
from __future__ import annotations

import asyncio

import pytest

from circle_ai.security import (
    AnomalyDispatchOutcome,
    AnomalySignal,
    DefaultAnomalyEventDispatcher,
    DefaultSecurityWatchdog,
    ISecurityWatchdog,
    SecurityResponse,
    ThreatVector,
)


class _RecordingWatchdog(ISecurityWatchdog):
    """Counts invocations; returns a trivial no-action response."""

    def __init__(self) -> None:
        self.calls = 0

    async def on_anomaly_detected_async(self, signal, checkpoint=None, ct=None):
        self.calls += 1
        return SecurityResponse.no_action(signal.id, "recorded")

    async def stream_signals_async(self, ct=None):  # pragma: no cover - unused
        if False:
            yield None


async def test_dispatched_invokes_watchdog_and_returns_response():
    wd = _RecordingWatchdog()
    disp = DefaultAnomalyEventDispatcher(wd, minimum_confidence=0.30)
    sig = AnomalySignal.create(ThreatVector.MEMORY_ANOMALY, 0.5, "M", "d")
    res = await disp.verify_and_dispatch_async(sig)
    assert res.outcome == AnomalyDispatchOutcome.DISPATCHED
    assert res.response is not None
    assert wd.calls == 1


async def test_below_threshold_not_dispatched():
    wd = _RecordingWatchdog()
    disp = DefaultAnomalyEventDispatcher(wd, minimum_confidence=0.30)
    sig = AnomalySignal.create(ThreatVector.MEMORY_ANOMALY, 0.29, "M", "d")
    res = await disp.verify_and_dispatch_async(sig)
    assert res.outcome == AnomalyDispatchOutcome.BELOW_THRESHOLD
    assert res.response is None
    assert wd.calls == 0


async def test_duplicate_id_deduped():
    wd = _RecordingWatchdog()
    disp = DefaultAnomalyEventDispatcher(wd)
    sig = AnomalySignal.create(ThreatVector.MEMORY_ANOMALY, 0.5, "M", "d")
    first = await disp.verify_and_dispatch_async(sig)
    second = await disp.verify_and_dispatch_async(sig)
    assert first.outcome == AnomalyDispatchOutcome.DISPATCHED
    assert second.outcome == AnomalyDispatchOutcome.DUPLICATE
    assert second.response is None
    assert wd.calls == 1  # watchdog only called once


async def test_cancelled_before_dispatch():
    wd = _RecordingWatchdog()
    disp = DefaultAnomalyEventDispatcher(wd)
    sig = AnomalySignal.create(ThreatVector.MEMORY_ANOMALY, 0.9, "M", "d")

    class _Cancelled:
        is_cancellation_requested = True

    res = await disp.verify_and_dispatch_async(sig, ct=_Cancelled())
    assert res.outcome == AnomalyDispatchOutcome.CANCELLED
    assert wd.calls == 0


async def test_cancelled_via_event_token():
    wd = _RecordingWatchdog()
    disp = DefaultAnomalyEventDispatcher(wd)
    sig = AnomalySignal.create(ThreatVector.MEMORY_ANOMALY, 0.9, "M", "d")
    ev = asyncio.Event()
    ev.set()
    res = await disp.verify_and_dispatch_async(sig, ct=ev)
    assert res.outcome == AnomalyDispatchOutcome.CANCELLED


async def test_minimum_confidence_clamped():
    wd = _RecordingWatchdog()
    # Out-of-range value is clamped to [0,1]; 5.0 -> 1.0 so only conf==1.0 passes.
    disp = DefaultAnomalyEventDispatcher(wd, minimum_confidence=5.0)
    sig = AnomalySignal.create(ThreatVector.MEMORY_ANOMALY, 0.99, "M", "d")
    res = await disp.verify_and_dispatch_async(sig)
    assert res.outcome == AnomalyDispatchOutcome.BELOW_THRESHOLD


async def test_none_watchdog_rejected():
    with pytest.raises(ValueError):
        DefaultAnomalyEventDispatcher(None)  # type: ignore[arg-type]


async def test_end_to_end_with_default_watchdog():
    wd = DefaultSecurityWatchdog()
    disp = DefaultAnomalyEventDispatcher(wd)
    sig = AnomalySignal.create(ThreatVector.NETWORK_PIVOT, 0.9, "M", "d")
    res = await disp.verify_and_dispatch_async(sig)
    assert res.outcome == AnomalyDispatchOutcome.DISPATCHED
    # Real watchdog produced a composite for a high-confidence pivot.
    from circle_ai.security import SecurityResponseKind

    assert res.response.kind == SecurityResponseKind.COMPOSITE
