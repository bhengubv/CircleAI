# civic_primitives.py
#
# Port of CircleAI.Civic CivicPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Civic vertical: reported
# issues, representatives, civic events. C# ConcurrentDictionary -> dict.
# DateTimeOffset -> datetime, ``string? District`` -> Optional[str]. OpenIssues
# excludes status "Resolved" (case-insensitive); RepsForDistrict compares
# district case-insensitively (a null rep District never matches);
# UpcomingEvents filters to events at/after UTC now, ordered by time.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class CivicIssue:
    """Mirrors ``CircleAI.Civic.CivicIssue``."""

    issue_id: str
    category: str
    description: str
    lat: float
    lon: float
    reported_utc: datetime
    status: str


@dataclass(frozen=True, slots=True)
class Representative:
    """Mirrors ``CircleAI.Civic.Representative`` — ``string? District``."""

    rep_id: str
    name: str
    office: str
    contact_email: str
    district: Optional[str]


@dataclass(frozen=True, slots=True)
class CivicEvent:
    """Mirrors ``CircleAI.Civic.CivicEvent``."""

    event_id: str
    title: str
    at_utc: datetime
    location: str
    audience: str


class ICivicBoard(ABC):
    """In-memory board for civic issues, representatives and events."""

    @abstractmethod
    def report(self, i: CivicIssue) -> None:
        ...

    @abstractmethod
    def resolve(self, issue_id: str, status: str) -> None:
        ...

    @abstractmethod
    def open_issues(self) -> List[CivicIssue]:
        ...

    @abstractmethod
    def add_rep(self, r: Representative) -> None:
        ...

    @abstractmethod
    def reps_for_district(self, district: str) -> List[Representative]:
        ...

    @abstractmethod
    def schedule(self, e: CivicEvent) -> None:
        ...

    @abstractmethod
    def upcoming_events(self) -> List[CivicEvent]:
        ...


class InMemoryCivicBoard(ICivicBoard):
    """Thread-safe in-memory :class:`ICivicBoard`."""

    def __init__(self) -> None:
        self._issues: Dict[str, CivicIssue] = {}
        self._reps: Dict[str, Representative] = {}
        self._events: Dict[str, CivicEvent] = {}
        self._lock = threading.Lock()

    def report(self, i: CivicIssue) -> None:
        if i is None:
            raise ValueError("civic issue must not be None")
        with self._lock:
            self._issues[i.issue_id] = i

    def resolve(self, issue_id: str, status: str) -> None:
        with self._lock:
            i = self._issues.get(issue_id)
            if i is None:
                raise RuntimeError(f"Unknown issue {issue_id}")
            self._issues[issue_id] = CivicIssue(
                i.issue_id,
                i.category,
                i.description,
                i.lat,
                i.lon,
                i.reported_utc,
                status,
            )

    def open_issues(self) -> List[CivicIssue]:
        with self._lock:
            return [
                i
                for i in self._issues.values()
                if i.status.casefold() != "resolved"
            ]

    def add_rep(self, r: Representative) -> None:
        if r is None:
            raise ValueError("representative must not be None")
        with self._lock:
            self._reps[r.rep_id] = r

    def reps_for_district(self, district: str) -> List[Representative]:
        target = district.casefold() if district is not None else None
        with self._lock:
            return [
                r
                for r in self._reps.values()
                if r.district is not None and r.district.casefold() == target
            ]

    def schedule(self, e: CivicEvent) -> None:
        if e is None:
            raise ValueError("civic event must not be None")
        with self._lock:
            self._events[e.event_id] = e

    def upcoming_events(self) -> List[CivicEvent]:
        now = datetime.now(timezone.utc)
        with self._lock:
            items = [e for e in self._events.values() if e.at_utc >= now]
        items.sort(key=lambda e: e.at_utc)
        return items
