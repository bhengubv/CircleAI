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
from typing import Dict, List, Optional, Tuple


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

    @property
    def open_issue_count(self) -> int:
        """Number of issues not yet resolved (C#: ``OpenIssueCount``)."""
        return len(self.open_issues())

    def issues_by_category(self, category: str) -> List[CivicIssue]:
        """Issues in a given category (case-insensitive), newest-first
        (C#: ``IssuesByCategory``).
        """
        target = category.casefold()
        with self._lock:
            matches = [
                i
                for i in self._issues.values()
                if i.category.casefold() == target
            ]
        return sorted(matches, key=lambda i: i.reported_utc, reverse=True)

    def remove_rep(self, rep_id: str) -> bool:
        """Remove a representative. Returns True if one was present
        (C#: ``RemoveRep``).
        """
        with self._lock:
            return self._reps.pop(rep_id, None) is not None

    def reps_for_office(self, office: str) -> List[Representative]:
        """Representatives holding a given office (case-insensitive), ordered by
        name (case-insensitive) (C#: ``RepsForOffice``).
        """
        target = office.casefold()
        with self._lock:
            matches = [
                r for r in self._reps.values() if r.office.casefold() == target
            ]
        return sorted(matches, key=lambda r: r.name.casefold())

    def events_for_audience(self, audience: str) -> List[CivicEvent]:
        """Events for a given audience (case-insensitive), earliest first
        (C#: ``EventsForAudience``).
        """
        target = audience.casefold()
        with self._lock:
            matches = [
                e
                for e in self._events.values()
                if e.audience.casefold() == target
            ]
        return sorted(matches, key=lambda e: e.at_utc)

    def open_issue_breakdown(self) -> List[Tuple[str, int]]:
        """Open-issue counts grouped by category (case-insensitive), highest
        first (C#: ``OpenIssueBreakdown`` — ``(Category, Count)`` pairs; ties
        keep first-seen order/casing).
        """
        counts: Dict[str, List] = {}  # casefold -> [display, count]
        for i in self.open_issues():
            key = i.category.casefold()
            agg = counts.get(key)
            if agg is None:
                counts[key] = [i.category, 1]
            else:
                agg[1] += 1
        ranked = sorted(counts.values(), key=lambda a: a[1], reverse=True)
        return [(display, count) for display, count in ranked]
