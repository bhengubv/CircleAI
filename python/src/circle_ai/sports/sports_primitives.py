# sports_primitives.py
#
# Port of CircleAI.Sports SportsPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory store for the Sports vertical:
# activities, training sessions, personal bests, weekly volume.
#
# C# ConcurrentDictionary -> plain dict; the activities list is guarded by a
# single lock (mirroring the C# monitor lock). C# DateTimeOffset -> datetime,
# TimeSpan -> timedelta. The "week start" is computed exactly like the C#
# ``now.Date.AddDays(-(int)now.DayOfWeek)`` — i.e. the midnight of the most
# recent Sunday (C# DayOfWeek: Sunday=0 .. Saturday=6).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from enum import IntEnum
from typing import Dict, List, Optional, Sequence


class DistanceKind(IntEnum):
    """Mirrors ``CircleAI.Sports.DistanceKind``. Stable ordinals."""

    RUN = 0
    BIKE = 1
    SWIM = 2
    WALK = 3
    ROW = 4


@dataclass(frozen=True, slots=True)
class Activity:
    """Mirrors ``CircleAI.Sports.Activity``."""

    activity_id: str
    user_id: str
    kind: DistanceKind
    distance_km: float
    duration: timedelta
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class PersonalBest:
    """Mirrors ``CircleAI.Sports.PersonalBest``."""

    user_id: str
    kind: DistanceKind
    distance_km: float
    time: timedelta
    achieved_utc: datetime


@dataclass(frozen=True, slots=True)
class TrainingSession:
    """Mirrors ``CircleAI.Sports.TrainingSession``."""

    session_id: str
    user_id: str
    plan: str
    scheduled_utc: datetime
    completed: bool


def _week_start(now: datetime) -> datetime:
    """Midnight of the most recent Sunday, mirroring C#
    ``now.Date.AddDays(-(int)now.DayOfWeek)`` (Sunday=0).
    """
    midnight = datetime(now.year, now.month, now.day, tzinfo=now.tzinfo)
    # Python weekday(): Monday=0..Sunday=6. C# DayOfWeek: Sunday=0..Saturday=6.
    dow = (now.weekday() + 1) % 7
    return midnight - timedelta(days=dow)


class ISportsBoard(ABC):
    """In-memory board for activities, training sessions and personal bests."""

    @abstractmethod
    def log(self, a: Activity) -> None:
        ...

    @abstractmethod
    def history(self, user_id: str, limit: int = 50) -> List[Activity]:
        ...

    @abstractmethod
    def total_km_this_week(
        self, user_id: str, kind: DistanceKind, now: datetime
    ) -> float:
        ...

    @abstractmethod
    def best(
        self, user_id: str, kind: DistanceKind, distance_km: float
    ) -> Optional[PersonalBest]:
        ...

    @abstractmethod
    def schedule(self, s: TrainingSession) -> None:
        ...

    @abstractmethod
    def complete(self, session_id: str) -> None:
        ...

    @abstractmethod
    def upcoming(self, user_id: str) -> List[TrainingSession]:
        ...


class InMemorySportsBoard(ISportsBoard):
    """Thread-safe in-memory :class:`ISportsBoard`."""

    def __init__(self) -> None:
        self._activities: List[Activity] = []
        self._sessions: Dict[str, TrainingSession] = {}
        self._lock = threading.Lock()

    def log(self, a: Activity) -> None:
        if a is None:
            raise ValueError("activity must not be None")
        with self._lock:
            self._activities.append(a)

    def history(self, user_id: str, limit: int = 50) -> List[Activity]:
        if limit <= 0:
            raise ValueError("limit must be positive")
        with self._lock:
            matches = [a for a in self._activities if a.user_id == user_id]
        matches.sort(key=lambda a: a.at_utc, reverse=True)
        return matches[:limit]

    def total_km_this_week(
        self, user_id: str, kind: DistanceKind, now: datetime
    ) -> float:
        week_start = _week_start(now)
        with self._lock:
            return sum(
                a.distance_km
                for a in self._activities
                if a.user_id == user_id and a.kind == kind and a.at_utc >= week_start
            )

    def best(
        self, user_id: str, kind: DistanceKind, distance_km: float
    ) -> Optional[PersonalBest]:
        with self._lock:
            candidates = [
                a
                for a in self._activities
                if a.user_id == user_id
                and a.kind == kind
                and a.distance_km >= distance_km
            ]
            if not candidates:
                return None
            # OrderBy(Duration).FirstOrDefault() -> the fastest qualifying effort.
            hit = min(candidates, key=lambda a: a.duration)
            return PersonalBest(user_id, kind, distance_km, hit.duration, hit.at_utc)

    def schedule(self, s: TrainingSession) -> None:
        if s is None:
            raise ValueError("training session must not be None")
        with self._lock:
            self._sessions[s.session_id] = s

    def complete(self, session_id: str) -> None:
        with self._lock:
            s = self._sessions.get(session_id)
            if s is None:
                raise RuntimeError(f"Unknown session {session_id}")
            # record `with { Completed = true }`
            self._sessions[session_id] = TrainingSession(
                s.session_id, s.user_id, s.plan, s.scheduled_utc, True
            )

    def upcoming(self, user_id: str) -> List[TrainingSession]:
        now = datetime.now(timezone.utc)
        with self._lock:
            items = [
                s
                for s in self._sessions.values()
                if s.user_id == user_id
                and not s.completed
                and s.scheduled_utc >= now
            ]
        items.sort(key=lambda s: s.scheduled_utc)
        return items
