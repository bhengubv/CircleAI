"""Predictive warmup — port of CircleAI.Hosting.Warmup.

(RT-07) Local-only request-timeline learner + background pre-warm controller.
The predictor records arrival times and forecasts whether a request spike is
imminent; the controller polls it and pre-warms the generator ahead of a
predicted spike. All learning is in-process — no telemetry, no upload.

Ports:
  * record ``ArrivalForecast``,
  * interface ``IRequestPredictor``,
  * class ``HistogramRequestPredictor`` (24h-of-day EWMA histogram),
  * options ``PredictiveWarmupOptions``,
  * controller ``PredictiveWarmupController``.

All arithmetic uses Python ``float`` (== C# ``double``); no ``struct.pack``
sites here since the C# uses ``double`` throughout, not ``float``.
"""
from __future__ import annotations

import asyncio
import datetime as _dt
import math
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Callable, Optional

__all__ = [
    "ArrivalForecast",
    "IRequestPredictor",
    "HistogramRequestPredictor",
    "PredictiveWarmupOptions",
    "PredictiveWarmupController",
]

_UTC = _dt.timezone.utc
_MINUTES_PER_DAY = 24 * 60
_WARM_CONFIDENCE = 1.0
_MIN_SAMPLES_FOR_FULL_CONFIDENCE = 25


def _as_utc(dt: _dt.datetime) -> _dt.datetime:
    if dt.tzinfo is None:
        return dt.replace(tzinfo=_UTC)
    return dt.astimezone(_UTC)


@dataclass(frozen=True, slots=True)
class ArrivalForecast:
    """(RT-07) Forecast of inbound requests over a window. Mirrors
    ``ArrivalForecast``.
    """

    probability_of_arrival: float
    """0.0 .. 1.0 — chance of at least one request inside the window."""

    expected_count: float
    """Best estimate of how many arrivals to expect."""

    confidence: float
    """0.0 .. 1.0 — how trustworthy the estimate is given the sample size."""


class IRequestPredictor(ABC):
    """(RT-07) Local-only predictor that learns request arrival timing and
    forecasts whether a spike is coming. Mirrors ``IRequestPredictor``.
    """

    @abstractmethod
    def record_arrival(self, utc: _dt.datetime) -> None:
        """Record one arrival at ``utc``."""
        ...

    @abstractmethod
    def predict(self, utc_now: _dt.datetime, forecast_window: _dt.timedelta) -> ArrivalForecast:
        """Forecast arrivals in ``forecast_window`` starting at ``utc_now``."""
        ...

    @property
    @abstractmethod
    def observed_arrivals(self) -> int:
        """Total arrivals observed since construction."""
        ...


class HistogramRequestPredictor(IRequestPredictor):
    """(RT-07) Default :class:`IRequestPredictor` — keeps a histogram of
    per-minute arrival rates over a rolling window of recent days, then
    forecasts the next-window rate from that histogram. Mirrors
    ``HistogramRequestPredictor``. Thread-safe.
    """

    __slots__ = ("_history_days", "_per_minute_rate", "_per_minute_count", "_gate", "_observed")

    def __init__(self, history_days: int = 7) -> None:
        if history_days <= 0:
            raise ValueError("history_days must be positive")
        self._history_days = history_days
        self._per_minute_rate = [0.0] * _MINUTES_PER_DAY
        self._per_minute_count = [0] * _MINUTES_PER_DAY
        self._gate = threading.RLock()
        self._observed = 0

    @property
    def observed_arrivals(self) -> int:
        with self._gate:
            return self._observed

    def record_arrival(self, utc: _dt.datetime) -> None:
        u = _as_utc(utc)
        minute = (u.hour * 60) + u.minute
        with self._gate:
            self._per_minute_count[minute] += 1
            cnt = self._per_minute_count[minute]
            # EWMA over the last `history_days` observations at this slot.
            # alpha shrinks as cnt grows, so early samples dominate less.
            alpha = 2.0 / (min(cnt, self._history_days) + 1)
            self._per_minute_rate[minute] = (alpha * 1.0) + (
                (1 - alpha) * self._per_minute_rate[minute]
            )
            self._observed += 1

    def predict(self, utc_now: _dt.datetime, forecast_window: _dt.timedelta) -> ArrivalForecast:
        if forecast_window <= _dt.timedelta(0):
            return ArrivalForecast(0.0, 0.0, 0.0)

        with self._gate:
            observed = self._observed
            if observed == 0:
                return ArrivalForecast(0.0, 0.0, 0.0)

            u = _as_utc(utc_now)
            minute = (u.hour * 60) + u.minute
            minutes = max(1, math.ceil(forecast_window.total_seconds() / 60.0))
            expected = 0.0
            covered_samples = 0
            for i in range(minutes):
                idx = (minute + i) % _MINUTES_PER_DAY
                expected += self._per_minute_rate[idx]
                covered_samples += self._per_minute_count[idx]

        # Poisson tail: P(>=1 arrival) = 1 - exp(-lambda).
        probability = 1.0 - math.exp(-expected)
        # Confidence rises as the per-minute slots accumulate samples.
        confidence = min(
            _WARM_CONFIDENCE,
            covered_samples / (_MIN_SAMPLES_FOR_FULL_CONFIDENCE * minutes),
        )
        return ArrivalForecast(probability, expected, confidence)

    def reset_for_tests(self) -> None:
        """Test-only — wipe state. Mirrors ``ResetForTests``."""
        with self._gate:
            for i in range(_MINUTES_PER_DAY):
                self._per_minute_rate[i] = 0.0
                self._per_minute_count[i] = 0
            self._observed = 0


@dataclass
class PredictiveWarmupOptions:
    """(RT-07) Configuration for :class:`PredictiveWarmupController`. Mirrors
    ``PredictiveWarmupOptions`` (mutable, with the same defaults).
    """

    enabled: bool = False
    """When False (default), the controller does not pre-warm — opt-in."""

    poll_interval: _dt.timedelta = _dt.timedelta(seconds=30)
    """How often the controller asks the predictor about the upcoming window."""

    forecast_window: _dt.timedelta = _dt.timedelta(seconds=60)
    """How far ahead to forecast."""

    warmup_threshold: float = 0.5
    """Pre-warm when forecast probability × confidence is at/above this."""

    min_time_between_warmups: _dt.timedelta = _dt.timedelta(minutes=5)
    """Minimum delay between consecutive pre-warm calls."""


class PredictiveWarmupController:
    """(RT-07) Async background loop that polls an :class:`IRequestPredictor`
    and triggers :meth:`IAIService.prewarm_async` before predicted spikes.
    Mirrors ``PredictiveWarmupController``.

    ``clock`` is injectable for deterministic tests. Prefer calling
    :meth:`tick_async` directly rather than driving the loop.
    """

    __slots__ = (
        "_service",
        "_predictor",
        "_options",
        "_clock",
        "_loop_task",
        "_stopping",
        "_last_warmup",
        "_disposed",
    )

    def __init__(
        self,
        service,
        predictor: IRequestPredictor,
        options: PredictiveWarmupOptions,
        clock: Optional[Callable[[], _dt.datetime]] = None,
    ) -> None:
        if service is None:
            raise ValueError("service is required")
        if predictor is None:
            raise ValueError("predictor is required")
        if options is None:
            raise ValueError("options is required")
        self._service = service
        self._predictor = predictor
        self._options = options
        self._clock = clock or (lambda: _dt.datetime.now(_UTC))
        self._loop_task: Optional[asyncio.Task] = None
        self._stopping: Optional[asyncio.Event] = None
        self._last_warmup = _dt.datetime.min.replace(tzinfo=_UTC)
        self._disposed = False

    async def start_async(self, ct: object = None) -> None:
        """Begin polling on a background loop. No-op when disabled or already
        running. Mirrors ``StartAsync``.
        """
        if self._disposed:
            raise RuntimeError("PredictiveWarmupController is disposed")
        if not self._options.enabled or self._loop_task is not None:
            return
        self._stopping = asyncio.Event()
        self._loop_task = asyncio.ensure_future(self._run_loop_async(self._stopping))

    def notify_arrival(self) -> None:
        """Record a request arrival on the underlying predictor at "now".
        Mirrors ``NotifyArrival``.
        """
        self._predictor.record_arrival(self._clock())

    async def tick_async(self, ct: object = None) -> bool:
        """Run one prediction + decide-and-maybe-warm cycle. Returns ``True``
        when warmup was triggered. Mirrors ``TickAsync``.
        """
        now = self._clock()
        forecast = self._predictor.predict(now, self._options.forecast_window)
        score = forecast.probability_of_arrival * forecast.confidence
        if score < self._options.warmup_threshold:
            return False
        if now - self._last_warmup < self._options.min_time_between_warmups:
            return False

        try:
            self._last_warmup = now
            await self._service.prewarm_async()
            return True
        except Exception:  # noqa: BLE001 - warmup failure is non-fatal
            return False

    async def _run_loop_async(self, stopping: asyncio.Event) -> None:
        interval = self._options.poll_interval.total_seconds()
        try:
            await self.tick_async()
            while not stopping.is_set():
                try:
                    await asyncio.wait_for(stopping.wait(), timeout=interval)
                except asyncio.TimeoutError:
                    await self.tick_async()
                    continue
                break
        except Exception:  # noqa: BLE001 - loop crash is swallowed, per C#
            pass

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        if self._stopping is not None:
            self._stopping.set()
        if self._loop_task is not None:
            try:
                await self._loop_task
            except asyncio.CancelledError:  # pragma: no cover
                pass
