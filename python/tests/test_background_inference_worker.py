"""test_background_inference_worker.py

Verifies BackgroundInferenceWorker (port of
CircleAI.Hosting.BackgroundInferenceWorker): host start/stop lifecycle binding
to an IAIService, thermal-driven pause/resume (state >= SERIOUS), idempotent
double-stop, subscribe/unsubscribe of the thermal handler, and dispose.
"""
from __future__ import annotations

import pytest

from circle_ai.hosting import (
    BackgroundInferenceWorker,
    IAIService,
    ThermalState,
    ThermalThrottleService,
)


class _RecordingButler(IAIService):
    """Minimal IAIService that records lifecycle calls. Inference entry points
    are unused by the worker, so they return trivial deterministic values.
    """

    def __init__(self) -> None:
        self.started = 0
        self.stopped = 0
        self.disposed = 0
        self._ready = False

    @property
    def is_ready(self) -> bool:
        return self._ready

    async def start_async(self, ct: object = None) -> None:
        self.started += 1
        self._ready = True

    async def stop_async(self, ct: object = None) -> None:
        self.stopped += 1
        self._ready = False

    async def ask_async(self, question: str, ct: object = None) -> str:
        return "ok"

    async def chat_async(self, messages, options=None, ct=None) -> str:
        return "ok"

    async def stream_async(self, messages, options=None, ct=None):
        if False:  # pragma: no cover - never yields; present for the contract
            yield ""

    async def invoke_tool_async(self, invocation, ct=None):  # pragma: no cover
        raise NotImplementedError

    async def agentic_chat_async(self, prompt, options=None, ct=None) -> str:
        return "ok"

    async def submit_feedback_async(self, signal, ct=None) -> None:
        return None

    async def dispose_async(self) -> None:
        self.disposed += 1


class _ManualThermal(ThermalThrottleService):
    """Thermal service whose state is driven manually via :meth:`set_state`
    (feeds the injected sampler). Avoids the real background poll loop so tests
    are deterministic.
    """

    def __init__(self) -> None:
        self._state_value = ThermalState.NORMAL
        super().__init__(sampler=lambda: self._state_value, poll_interval_seconds=3600.0)
        self.start_monitoring_calls = 0
        self.stop_monitoring_calls = 0

    def set_state(self, state: ThermalState) -> None:
        self._state_value = state
        self.sample_once()  # apply + fire change handlers synchronously

    def start_monitoring(self, ct: object = None) -> None:
        self.start_monitoring_calls += 1
        super().start_monitoring(ct)

    def stop_monitoring(self) -> None:
        self.stop_monitoring_calls += 1
        super().stop_monitoring()


# ── construction ────────────────────────────────────────────────────────────


def test_rejects_none_butler() -> None:
    with pytest.raises(ValueError):
        BackgroundInferenceWorker(None)  # type: ignore[arg-type]


def test_is_paused_false_without_thermal() -> None:
    worker = BackgroundInferenceWorker(_RecordingButler())
    assert worker.is_paused is False


# ── lifecycle ─────────────────────────────────────────────────────────────


async def test_start_starts_butler() -> None:
    butler = _RecordingButler()
    worker = BackgroundInferenceWorker(butler)
    await worker.start_async()
    assert butler.started == 1
    assert butler.is_ready is True


async def test_stop_stops_butler() -> None:
    butler = _RecordingButler()
    worker = BackgroundInferenceWorker(butler)
    await worker.start_async()
    await worker.stop_async()
    assert butler.stopped == 1
    assert butler.is_ready is False


async def test_stop_is_idempotent() -> None:
    butler = _RecordingButler()
    worker = BackgroundInferenceWorker(butler)
    await worker.start_async()
    await worker.stop_async()
    await worker.stop_async()  # double-stop guarded
    assert butler.stopped == 1


async def test_start_begins_thermal_monitoring() -> None:
    butler = _RecordingButler()
    thermal = _ManualThermal()
    worker = BackgroundInferenceWorker(butler, thermal)
    await worker.start_async()
    assert thermal.start_monitoring_calls == 1
    await worker.stop_async()
    assert thermal.stop_monitoring_calls == 1


# ── thermal pause / resume ──────────────────────────────────────────────────


async def test_pauses_on_serious_and_resumes_on_fair() -> None:
    butler = _RecordingButler()
    thermal = _ManualThermal()
    worker = BackgroundInferenceWorker(butler, thermal)
    await worker.start_async()

    assert worker.is_paused is False
    thermal.set_state(ThermalState.SERIOUS)
    assert worker.is_paused is True
    thermal.set_state(ThermalState.FAIR)
    assert worker.is_paused is False

    await worker.stop_async()


async def test_pauses_on_critical() -> None:
    butler = _RecordingButler()
    thermal = _ManualThermal()
    worker = BackgroundInferenceWorker(butler, thermal)
    await worker.start_async()
    thermal.set_state(ThermalState.CRITICAL)
    assert worker.is_paused is True
    await worker.stop_async()


async def test_fair_does_not_pause() -> None:
    butler = _RecordingButler()
    thermal = _ManualThermal()
    worker = BackgroundInferenceWorker(butler, thermal)
    await worker.start_async()
    thermal.set_state(ThermalState.FAIR)  # below the SERIOUS threshold
    assert worker.is_paused is False
    await worker.stop_async()


async def test_unsubscribes_on_stop_so_later_state_change_is_ignored() -> None:
    butler = _RecordingButler()
    thermal = _ManualThermal()
    worker = BackgroundInferenceWorker(butler, thermal)
    await worker.start_async()
    await worker.stop_async()

    # After stop, the worker's handler is detached — a thermal spike must not
    # flip is_paused.
    thermal.set_state(ThermalState.CRITICAL)
    assert worker.is_paused is False


# ── dispose ─────────────────────────────────────────────────────────────────


async def test_dispose_stops_and_disposes_butler() -> None:
    butler = _RecordingButler()
    thermal = _ManualThermal()
    worker = BackgroundInferenceWorker(butler, thermal)
    await worker.start_async()
    await worker.dispose_async()
    assert butler.stopped == 1
    assert butler.disposed == 1
    assert thermal.stop_monitoring_calls == 1


async def test_dispose_is_safe_without_thermal() -> None:
    butler = _RecordingButler()
    worker = BackgroundInferenceWorker(butler)
    await worker.start_async()
    await worker.dispose_async()
    assert butler.disposed == 1
