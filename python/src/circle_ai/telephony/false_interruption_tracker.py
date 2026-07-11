# false_interruption_tracker.py
#
# Port of CircleAI.Telephony FalseInterruptionTracker.cs (C# — the EXACT spec).
#
# (3.3.0) Counts how often the barge-in controller paused and then resumed
# (false alarm) versus cancelled (real interruption). High false-alarm rates
# suggest the VAD threshold is too sensitive.
#
# C# Interlocked long counters -> ints guarded by a lock (thread-safe). The rate
# is a float: false_alarms / total_pauses, or 0.0 when no pauses.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass

from .barge_in_controller import BargeInState, BargeInTransition


@dataclass(frozen=True, slots=True)
class InterruptionStats:
    """(3.3.0) Counters for false-interruption monitoring.

    Mirrors ``record(long TotalPauseEvents, long ConfirmedBargeIns, long
    FalseAlarms, float FalseAlarmRate)``.
    """

    total_pause_events: int
    confirmed_barge_ins: int
    false_alarms: int
    false_alarm_rate: float


class IFalseInterruptionTracker(ABC):
    """(3.3.0) Tracks barge-in transitions and surfaces a false-alarm rate."""

    @abstractmethod
    def record(self, transition: BargeInTransition) -> None:
        """Record one transition emitted by :class:`BargeInController`."""

    @abstractmethod
    def get_stats(self) -> InterruptionStats:
        """Current cumulative stats."""

    @abstractmethod
    def reset(self) -> None:
        """Reset all counters."""


class InMemoryFalseInterruptionTracker(IFalseInterruptionTracker):
    """(3.3.0) Default in-memory tracker. Thread-safe."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._total_pauses = 0
        self._confirmed = 0
        self._false_alarms = 0

    def record(self, transition: BargeInTransition) -> None:
        if transition is None:
            raise ValueError("transition must not be None")
        with self._lock:
            if transition.to_state == BargeInState.PAUSED:
                self._total_pauses += 1
            elif transition.to_state == BargeInState.CANCELLED:
                self._confirmed += 1
            elif transition.to_state == BargeInState.RESUMED:
                self._false_alarms += 1

    def get_stats(self) -> InterruptionStats:
        with self._lock:
            total_pauses = self._total_pauses
            confirmed = self._confirmed
            false_alarms = self._false_alarms
        rate = (float(false_alarms) / total_pauses) if total_pauses > 0 else 0.0
        return InterruptionStats(total_pauses, confirmed, false_alarms, rate)

    def reset(self) -> None:
        with self._lock:
            self._total_pauses = 0
            self._confirmed = 0
            self._false_alarms = 0
