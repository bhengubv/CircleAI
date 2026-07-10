"""IThermalThrottleService + ThermalThrottleService — ports of the
CircleAI.Hosting thermal-throttle types.

Cross-platform thermal state monitor. The C# implementation polls OS-level
temperature APIs (Android PowerManager, iOS NSProcessInfo, Windows WMI, Linux
sysfs) on a 10-second background timer. Python has no portable thermal API, so
the platform sampler is injected as a callable (``sampler``) — defaulting to
always-``UNKNOWN``, which is exactly the C# behaviour on an unsupported
platform. The state-machine + change-event + pause logic is preserved
faithfully and is fully testable by driving the injected sampler.

Ports: enum ``ThermalState`` (stable ordinals), interface
``IThermalThrottleService``, class ``ThermalThrottleService``.
"""
from __future__ import annotations

import asyncio
import threading
from abc import ABC, abstractmethod
from enum import IntEnum
from typing import Callable, List, Optional

__all__ = ["ThermalState", "IThermalThrottleService", "ThermalThrottleService"]

# Poll interval in seconds — matches the C# 10-second cadence.
_POLL_INTERVAL_SECONDS = 10.0

# StateChanged callback: receives the new ThermalState.
ThermalStateHandler = Callable[["ThermalState"], None]

# Platform sampler: returns the current ThermalState. Must not raise (the
# service wraps it in try/except → Unknown, mirroring SampleThermalState).
ThermalSampler = Callable[[], "ThermalState"]


class ThermalState(IntEnum):
    """Coarse thermal state, ordered coolest→hottest so numeric comparisons
    (e.g. ``>= ThermalState.SERIOUS``) are meaningful. Mirrors ``ThermalState``
    with the same ordinals.
    """

    UNKNOWN = 0
    """State could not be determined (API unavailable or error)."""

    NORMAL = 1
    """Device is within normal operating temperature."""

    FAIR = 2
    """Device is slightly warm; performance may be lightly throttled."""

    SERIOUS = 3
    """Device is hot; OS may have begun throttling CPU/GPU."""

    CRITICAL = 4
    """Device is critically hot; aggressive throttling or shutdown imminent."""


class IThermalThrottleService(ABC):
    """Polls platform thermal APIs and exposes the current temperature state.
    Mirrors ``IThermalThrottleService``.
    """

    @property
    @abstractmethod
    def current_state(self) -> ThermalState:
        """Most-recently sampled thermal state."""
        ...

    @property
    @abstractmethod
    def should_pause_inference(self) -> bool:
        """``True`` when ``current_state`` is SERIOUS or CRITICAL."""
        ...

    @abstractmethod
    def add_state_changed_handler(self, handler: ThermalStateHandler) -> None:
        """Subscribe to state-change notifications."""
        ...

    @abstractmethod
    def start_monitoring(self, ct: object = None) -> None:
        """Start the background polling loop. Safe to call multiple times."""
        ...

    @abstractmethod
    def stop_monitoring(self) -> None:
        """Stop the polling loop. The current state is retained."""
        ...

    @abstractmethod
    def dispose(self) -> None:
        """Stop monitoring and release resources."""
        ...


class ThermalThrottleService(IThermalThrottleService):
    """Cross-platform thermal state poller. Detects device temperature via the
    injected ``sampler`` and surfaces it as a :class:`ThermalState`. Mirrors
    ``ThermalThrottleService``.

    ``poll_interval_seconds`` is injectable for deterministic tests. Prefer
    calling :meth:`sample_once` directly in tests rather than driving the loop.
    """

    __slots__ = (
        "_sampler",
        "_poll_interval",
        "_current_state",
        "_handlers",
        "_gate",
        "_loop_task",
        "_stopping",
        "_disposed",
        "_running",
    )

    def __init__(
        self,
        sampler: Optional[ThermalSampler] = None,
        poll_interval_seconds: float = _POLL_INTERVAL_SECONDS,
    ) -> None:
        # Default sampler returns Unknown — mirrors the C# unsupported-platform path.
        self._sampler: ThermalSampler = sampler or (lambda: ThermalState.UNKNOWN)
        self._poll_interval = poll_interval_seconds
        self._current_state = ThermalState.UNKNOWN
        self._handlers: List[ThermalStateHandler] = []
        self._gate = threading.RLock()
        self._loop_task: Optional[asyncio.Task] = None
        self._stopping: Optional[asyncio.Event] = None
        self._disposed = False
        self._running = False

    @property
    def current_state(self) -> ThermalState:
        with self._gate:
            return self._current_state

    @property
    def should_pause_inference(self) -> bool:
        return self.current_state >= ThermalState.SERIOUS

    def add_state_changed_handler(self, handler: ThermalStateHandler) -> None:
        if handler is None:
            raise ValueError("handler is required")
        with self._gate:
            self._handlers.append(handler)

    def remove_state_changed_handler(self, handler: ThermalStateHandler) -> None:
        with self._gate:
            try:
                self._handlers.remove(handler)
            except ValueError:
                pass

    def start_monitoring(self, ct: object = None) -> None:
        if self._disposed:
            raise RuntimeError("ThermalThrottleService is disposed")
        # Ensure only one polling loop runs at a time.
        with self._gate:
            if self._running:
                return
            self._running = True
        self._stopping = asyncio.Event()
        # Sample immediately so callers get a valid state before the first tick.
        self.sample_once()
        self._loop_task = asyncio.ensure_future(self._poll_loop_async(self._stopping))

    def stop_monitoring(self) -> None:
        if self._stopping is not None:
            self._stopping.set()
        with self._gate:
            self._running = False

    def sample_once(self) -> ThermalState:
        """Sample the platform once and apply the new state (firing the change
        event on transition). Public for deterministic tests; the loop calls
        this on each tick. Mirrors ``ApplyNewState(SampleThermalState())``.
        """
        self._apply_new_state(self._sample_thermal_state())
        return self.current_state

    async def _poll_loop_async(self, stopping: asyncio.Event) -> None:
        # Already sampled once in start_monitoring; loop handles subsequent ticks.
        while not stopping.is_set():
            try:
                await asyncio.wait_for(stopping.wait(), timeout=self._poll_interval)
            except asyncio.TimeoutError:
                self.sample_once()
                continue
            break

    def _sample_thermal_state(self) -> ThermalState:
        try:
            return self._sampler()
        except Exception:  # noqa: BLE001 - failing sampler → Unknown, per C#
            return ThermalState.UNKNOWN

    def _apply_new_state(self, new_state: ThermalState) -> None:
        with self._gate:
            previous = self._current_state
            self._current_state = new_state
            handlers = list(self._handlers)

        if previous != new_state:
            for handler in handlers:
                try:
                    handler(new_state)
                except Exception:  # noqa: BLE001 - handler errors are isolated
                    pass

    def dispose(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        self.stop_monitoring()
