"""ITriggerCondition + ProactiveContext + ScheduleTrigger + IdleTrigger —
ports of the CircleAI.Hosting trigger types.

Proactive reasoning trigger conditions. Each condition evaluates a
:class:`ProactiveContext` snapshot and signals when B! should initiate a
check-in. Mirrors ``ITriggerCondition``, ``ProactiveContext``,
``ScheduleTrigger`` and ``IdleTrigger``.
"""
from __future__ import annotations

import datetime as _dt
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Optional, Sequence

from ..memory.affect_state import AffectState
from ..memory.goal import Goal

__all__ = [
    "ProactiveContext",
    "ITriggerCondition",
    "ScheduleTrigger",
    "IdleTrigger",
]

_UTC = _dt.timezone.utc


@dataclass(frozen=True, slots=True)
class ProactiveContext:
    """Context snapshot passed to trigger conditions. Mirrors ``ProactiveContext``.

    ``time_since_last_interaction`` is a :class:`datetime.timedelta`.
    """

    user_id: str
    now_utc: _dt.datetime
    time_since_last_interaction: _dt.timedelta
    affect_state: Optional[AffectState]
    active_goals: Sequence[Goal]


class ITriggerCondition(ABC):
    """A condition that, when true, signals B! should check in proactively.
    Mirrors ``ITriggerCondition``.
    """

    @property
    @abstractmethod
    def name(self) -> str:
        """Stable name used for logging and deduplication."""
        ...

    @abstractmethod
    async def is_met_async(self, context: ProactiveContext, ct: object = None) -> bool:
        """Return ``True`` when the condition is currently met."""
        ...


class ScheduleTrigger(ITriggerCondition):
    """Fires at a specific time of day. Active for a 5-minute window starting at
    ``trigger_time`` and fires at most once per calendar day. Mirrors
    ``ScheduleTrigger``.

    ``trigger_time`` is a :class:`datetime.time` (the C# ``TimeOnly``). The
    comparison is done against ``context.now_utc`` interpreted as local time —
    matching the C# which reads ``NowUtc.LocalDateTime``. Since this port does
    not carry a system-timezone dependency, "local" here means the wall-clock
    of ``now_utc`` in its own timezone (naive treated as-is).
    """

    __slots__ = ("_trigger_time", "_name", "_last_fire_date")

    def __init__(self, trigger_time: _dt.time, name: str = "schedule") -> None:
        self._trigger_time = trigger_time
        self._name = name
        self._last_fire_date: Optional[_dt.date] = None

    @property
    def trigger_time(self) -> _dt.time:
        """Time of day at which this trigger fires."""
        return self._trigger_time

    @property
    def name(self) -> str:
        return self._name

    async def is_met_async(self, context: ProactiveContext, ct: object = None) -> bool:
        if context is None:
            raise ValueError("context is required")

        local_now = context.now_utc
        local_date = local_now.date()
        local_time = local_now.time()

        # Already fired today — don't fire again.
        if self._last_fire_date is not None and self._last_fire_date == local_date:
            return False

        window_start = self._trigger_time
        window_end = _add_minutes_time(self._trigger_time, 5)

        if window_end >= window_start:
            # Normal case — window doesn't wrap midnight.
            in_window = window_start <= local_time < window_end
        else:
            # Window wraps midnight (e.g. 23:58 + 5 min = 00:03).
            in_window = local_time >= window_start or local_time < window_end

        if not in_window:
            return False

        # In the window — mark fired for today and return true.
        self._last_fire_date = local_date
        return True


class IdleTrigger(ITriggerCondition):
    """Fires when ``time_since_last_interaction`` exceeds ``idle_threshold``.
    Useful for a warm check-in after the user has been away. Mirrors
    ``IdleTrigger``.
    """

    __slots__ = ("_idle_threshold",)

    def __init__(self, idle_threshold: Optional[_dt.timedelta] = None) -> None:
        self._idle_threshold = (
            idle_threshold if idle_threshold is not None else _dt.timedelta(hours=4)
        )

    @property
    def idle_threshold(self) -> _dt.timedelta:
        """Idle threshold used by this trigger."""
        return self._idle_threshold

    @property
    def name(self) -> str:
        return "idle"

    async def is_met_async(self, context: ProactiveContext, ct: object = None) -> bool:
        if context is None:
            raise ValueError("context is required")
        return context.time_since_last_interaction > self._idle_threshold


def _add_minutes_time(t: _dt.time, minutes: int) -> _dt.time:
    """Add minutes to a naive time-of-day, wrapping at midnight. Mirrors
    ``TimeOnly.AddMinutes`` (which wraps within a 24h day).
    """
    total = (t.hour * 60 + t.minute + minutes) % (24 * 60)
    return _dt.time(hour=total // 60, minute=total % 60, second=t.second, microsecond=t.microsecond)
