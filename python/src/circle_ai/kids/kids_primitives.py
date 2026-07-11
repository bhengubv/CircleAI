# kids_primitives.py
#
# Port of CircleAI.Kids KidsPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Kids vertical: content
# (age-banded), daily time limits, time logs. C# ConcurrentDictionary -> dict;
# the logs list is guarded by a single lock. TimeSpan -> timedelta,
# DateTimeOffset -> datetime. ContentFor filters by age band, ordered by title.
# UsedToday sums a kid's same-(UTC-)date logs of a kind. OverLimit compares
# UsedToday against the ScreenLimit / ReadingLimit cap (an unknown kind uses
# TimeSpan.MaxValue -> never over); returns False when the kid has no limits.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta
from enum import IntEnum
from typing import Dict, List, Optional, Sequence


class AgeAppropriateness(IntEnum):
    """Mirrors ``CircleAI.Kids.AgeAppropriateness``. Stable ordinals."""

    TODDLER = 0
    PRESCHOOL = 1
    EARLY_PRIMARY = 2
    LATE_PRIMARY = 3
    PRE_TEEN = 4
    TEEN = 5


#: C# ``TimeSpan.MaxValue`` — the cap for an unrecognised time-log kind.
_TIMESPAN_MAX = timedelta.max


@dataclass(frozen=True, slots=True)
class KidsContent:
    """Mirrors ``CircleAI.Kids.KidsContent``."""

    content_id: str
    title: str
    age_band: AgeAppropriateness
    kind: str
    tags: Sequence[str]


@dataclass(frozen=True, slots=True)
class DailyTime:
    """Mirrors ``CircleAI.Kids.DailyTime`` — ``TimeSpan ScreenLimit/ReadingLimit``."""

    kid_name: str
    screen_limit: timedelta
    reading_limit: timedelta


@dataclass(frozen=True, slots=True)
class TimeLog:
    """Mirrors ``CircleAI.Kids.TimeLog``."""

    kid_name: str
    kind: str
    duration: timedelta
    at_utc: datetime


class IKidsBoard(ABC):
    """In-memory board for kids' content, time limits and time logs."""

    @abstractmethod
    def add_content(self, c: KidsContent) -> None:
        ...

    @abstractmethod
    def content_for(self, band: AgeAppropriateness) -> List[KidsContent]:
        ...

    @abstractmethod
    def set_limits(self, d: DailyTime) -> None:
        ...

    @abstractmethod
    def limits_for(self, kid_name: str) -> Optional[DailyTime]:
        ...

    @abstractmethod
    def record_time(self, t: TimeLog) -> None:
        ...

    @abstractmethod
    def used_today(self, kid_name: str, kind: str, now: datetime) -> timedelta:
        ...

    @abstractmethod
    def over_limit(self, kid_name: str, kind: str, now: datetime) -> bool:
        ...


class InMemoryKidsBoard(IKidsBoard):
    """Thread-safe in-memory :class:`IKidsBoard`."""

    def __init__(self) -> None:
        self._content: Dict[str, KidsContent] = {}
        self._limits: Dict[str, DailyTime] = {}
        self._logs: List[TimeLog] = []
        self._lock = threading.Lock()

    def add_content(self, c: KidsContent) -> None:
        if c is None:
            raise ValueError("kids content must not be None")
        with self._lock:
            self._content[c.content_id] = c

    def content_for(self, band: AgeAppropriateness) -> List[KidsContent]:
        with self._lock:
            items = [c for c in self._content.values() if c.age_band == band]
        items.sort(key=lambda c: c.title)
        return items

    def set_limits(self, d: DailyTime) -> None:
        if d is None:
            raise ValueError("daily time must not be None")
        with self._lock:
            self._limits[d.kid_name] = d

    def limits_for(self, kid_name: str) -> Optional[DailyTime]:
        with self._lock:
            return self._limits.get(kid_name)

    def record_time(self, t: TimeLog) -> None:
        if t is None:
            raise ValueError("time log must not be None")
        with self._lock:
            self._logs.append(t)

    def _used_today_unlocked(
        self, kid_name: str, kind: str, now: datetime
    ) -> timedelta:
        total = timedelta()
        for l in self._logs:
            if (
                l.kid_name == kid_name
                and l.kind == kind
                and l.at_utc.date() == now.date()
            ):
                total += l.duration
        return total

    def used_today(self, kid_name: str, kind: str, now: datetime) -> timedelta:
        with self._lock:
            return self._used_today_unlocked(kid_name, kind, now)

    def over_limit(self, kid_name: str, kind: str, now: datetime) -> bool:
        with self._lock:
            limits = self._limits.get(kid_name)
            if limits is None:
                return False
            used = self._used_today_unlocked(kid_name, kind, now)
            if kind.casefold() == "screen":
                cap = limits.screen_limit
            elif kind.casefold() == "reading":
                cap = limits.reading_limit
            else:
                cap = _TIMESPAN_MAX
            return used > cap
