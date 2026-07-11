# parenting_primitives.py
#
# Port of CircleAI.Parenting ParentingPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory store for the Parenting vertical:
# children, milestones, school-day routines.
#
# C# ConcurrentDictionary stores map to plain dicts; the per-child milestone
# lists are guarded by a single lock (mirroring the C# monitor lock). C#
# DateTimeOffset -> datetime, DateTime DateOfBirth -> datetime, TimeSpan ->
# timedelta. C# System.DayOfWeek is Sunday=0..Saturday=6 — DayOfWeek is an
# IntEnum with those exact ordinals, and the routine key is "{childId}/{Name}"
# (e.g. "kid/Monday") matching the C# `$"{childId}/{d}"` enum ToString(). `Children`
# orders by Name (ordinal). MilestonesFor returns newest-first, or empty for an
# unknown child. AgeAsOf on an unknown child raises RuntimeError.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta
from enum import IntEnum
from typing import Dict, List, Optional, Sequence


class DayOfWeek(IntEnum):
    """Mirrors ``System.DayOfWeek`` (Sunday=0..Saturday=6). Its ``name`` matches
    the C# enum ToString() used to build routine keys.
    """

    Sunday = 0
    Monday = 1
    Tuesday = 2
    Wednesday = 3
    Thursday = 4
    Friday = 5
    Saturday = 6


@dataclass(frozen=True, slots=True)
class Child:
    """Mirrors ``CircleAI.Parenting.Child`` — ``record(string ChildId,
    string Name, DateTime DateOfBirth, string? Gender)``.
    """

    child_id: str
    name: str
    date_of_birth: datetime
    gender: Optional[str]


@dataclass(frozen=True, slots=True)
class Milestone:
    """Mirrors ``CircleAI.Parenting.Milestone`` — ``record(string MilestoneId,
    string ChildId, string Category, string Description, DateTimeOffset AchievedAtUtc)``.
    """

    milestone_id: str
    child_id: str
    category: str
    description: str
    achieved_at_utc: datetime


@dataclass(frozen=True, slots=True)
class RoutineEntry:
    """Mirrors ``CircleAI.Parenting.RoutineEntry`` — ``record(string Time,
    string Activity)``.
    """

    time: str
    activity: str


@dataclass(frozen=True, slots=True)
class Routine:
    """Mirrors ``CircleAI.Parenting.Routine`` — ``record(string ChildId,
    DayOfWeek DayOfWeek, IReadOnlyList<RoutineEntry> Entries)``.
    """

    child_id: str
    day_of_week: DayOfWeek
    entries: Sequence[RoutineEntry]


class IParentingBoard(ABC):
    """In-memory board for children, milestones and routines."""

    @abstractmethod
    def add_child(self, c: Child) -> None:
        ...

    @abstractmethod
    def get_child(self, id: str) -> Optional[Child]:
        ...

    @property
    @abstractmethod
    def children(self) -> List[Child]:
        ...

    @abstractmethod
    def record_milestone(self, m: Milestone) -> None:
        ...

    @abstractmethod
    def milestones_for(self, child_id: str) -> List[Milestone]:
        ...

    @abstractmethod
    def set_routine(self, r: Routine) -> None:
        ...

    @abstractmethod
    def get_routine(self, child_id: str, dow: DayOfWeek) -> Optional[Routine]:
        ...

    @abstractmethod
    def age_as_of(self, child_id: str, at: datetime) -> timedelta:
        ...


class InMemoryParentingBoard(IParentingBoard):
    """Thread-safe in-memory :class:`IParentingBoard`."""

    def __init__(self) -> None:
        self._children: Dict[str, Child] = {}
        self._milestones: Dict[str, List[Milestone]] = {}
        self._routines: Dict[str, Routine] = {}
        self._lock = threading.Lock()

    def add_child(self, c: Child) -> None:
        if c is None:
            raise ValueError("child must not be None")
        with self._lock:
            self._children[c.child_id] = c

    def get_child(self, id: str) -> Optional[Child]:
        with self._lock:
            return self._children.get(id)

    @property
    def children(self) -> List[Child]:
        with self._lock:
            return sorted(self._children.values(), key=lambda c: c.name)

    def record_milestone(self, m: Milestone) -> None:
        if m is None:
            raise ValueError("milestone must not be None")
        if m.child_id is None or not m.child_id.strip():
            raise ValueError("ChildId required")
        with self._lock:
            self._milestones.setdefault(m.child_id, []).append(m)

    def milestones_for(self, child_id: str) -> List[Milestone]:
        with self._lock:
            lst = self._milestones.get(child_id)
            if lst is None:
                return []
            return sorted(lst, key=lambda m: m.achieved_at_utc, reverse=True)

    def set_routine(self, r: Routine) -> None:
        if r is None:
            raise ValueError("routine must not be None")
        with self._lock:
            self._routines[self._key(r.child_id, r.day_of_week)] = r

    def get_routine(self, child_id: str, dow: DayOfWeek) -> Optional[Routine]:
        with self._lock:
            return self._routines.get(self._key(child_id, dow))

    def age_as_of(self, child_id: str, at: datetime) -> timedelta:
        with self._lock:
            c = self._children.get(child_id)
        if c is None:
            raise RuntimeError(f"Unknown child {child_id}")
        return at - c.date_of_birth

    @staticmethod
    def _key(child_id: str, d: DayOfWeek) -> str:
        return f"{child_id}/{d.name}"
