# faith_primitives.py
#
# Port of CircleAI.Faith FaithPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Faith vertical: services,
# prayer requests, scripture references. C# ConcurrentDictionary -> dict; the
# prayers list is guarded by a single lock. DateTimeOffset -> datetime.
# ServicesBetween is an inclusive time range ordered by start; RecentPrayers
# returns the newest `limit`; Lookup matches tradition/book/chapter/verse exactly
# (ordinal); ByTradition matches tradition case-insensitively.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class FaithService:
    """Mirrors ``CircleAI.Faith.FaithService``."""

    service_id: str
    community_name: str
    title: str
    start_utc: datetime
    location: str


@dataclass(frozen=True, slots=True)
class PrayerRequest:
    """Mirrors ``CircleAI.Faith.PrayerRequest``."""

    request_id: str
    author: str
    body: str
    submitted_utc: datetime
    is_anonymous: bool


@dataclass(frozen=True, slots=True)
class ScriptureReference:
    """Mirrors ``CircleAI.Faith.ScriptureReference``."""

    reference_id: str
    tradition: str
    book: str
    chapter: int
    verse: int
    text: str


class IFaithBoard(ABC):
    """In-memory board for services, prayer requests and scripture."""

    @abstractmethod
    def schedule(self, s: FaithService) -> None:
        ...

    @abstractmethod
    def services_between(self, start: datetime, end: datetime) -> List[FaithService]:
        ...

    @abstractmethod
    def submit_prayer(self, r: PrayerRequest) -> None:
        ...

    @abstractmethod
    def recent_prayers(self, limit: int = 20) -> List[PrayerRequest]:
        ...

    @abstractmethod
    def add_scripture(self, r: ScriptureReference) -> None:
        ...

    @abstractmethod
    def lookup(
        self, tradition: str, book: str, chapter: int, verse: int
    ) -> Optional[ScriptureReference]:
        ...

    @abstractmethod
    def by_tradition(self, tradition: str) -> List[ScriptureReference]:
        ...


class InMemoryFaithBoard(IFaithBoard):
    """Thread-safe in-memory :class:`IFaithBoard`."""

    def __init__(self) -> None:
        self._services: Dict[str, FaithService] = {}
        self._prayers: List[PrayerRequest] = []
        self._scripture: Dict[str, ScriptureReference] = {}
        self._lock = threading.Lock()

    def schedule(self, s: FaithService) -> None:
        if s is None:
            raise ValueError("faith service must not be None")
        with self._lock:
            self._services[s.service_id] = s

    def services_between(self, start: datetime, end: datetime) -> List[FaithService]:
        with self._lock:
            items = [
                s for s in self._services.values() if start <= s.start_utc <= end
            ]
        items.sort(key=lambda s: s.start_utc)
        return items

    def submit_prayer(self, r: PrayerRequest) -> None:
        if r is None:
            raise ValueError("prayer request must not be None")
        with self._lock:
            self._prayers.append(r)

    def recent_prayers(self, limit: int = 20) -> List[PrayerRequest]:
        with self._lock:
            items = list(self._prayers)
        items.sort(key=lambda p: p.submitted_utc, reverse=True)
        return items[:limit]

    def add_scripture(self, r: ScriptureReference) -> None:
        if r is None:
            raise ValueError("scripture reference must not be None")
        with self._lock:
            self._scripture[r.reference_id] = r

    def lookup(
        self, tradition: str, book: str, chapter: int, verse: int
    ) -> Optional[ScriptureReference]:
        with self._lock:
            for r in self._scripture.values():
                if (
                    r.tradition == tradition
                    and r.book == book
                    and r.chapter == chapter
                    and r.verse == verse
                ):
                    return r
            return None

    def by_tradition(self, tradition: str) -> List[ScriptureReference]:
        target = tradition.casefold()
        with self._lock:
            return [
                r
                for r in self._scripture.values()
                if r.tradition.casefold() == target
            ]

    @property
    def service_count(self) -> int:
        """Number of scheduled services (C#: ``ServiceCount``)."""
        with self._lock:
            return len(self._services)

    def remove_service(self, service_id: str) -> bool:
        """Remove a service. Returns True if one was present
        (C#: ``RemoveService``).
        """
        with self._lock:
            return self._services.pop(service_id, None) is not None

    def services_at(self, location: str) -> List[FaithService]:
        """Services at a given location (case-insensitive), earliest first
        (C#: ``ServicesAt``).
        """
        target = location.casefold()
        with self._lock:
            matches = [
                s
                for s in self._services.values()
                if s.location.casefold() == target
            ]
        return sorted(matches, key=lambda s: s.start_utc)

    def prayers_by_author(self, author: str) -> List[PrayerRequest]:
        """A named author's non-anonymous prayer requests (case-insensitive),
        newest-first (C#: ``PrayersByAuthor`` — privacy-aware: anonymous
        requests are excluded).
        """
        target = author.casefold()
        with self._lock:
            matches = [
                p
                for p in self._prayers
                if not p.is_anonymous and p.author.casefold() == target
            ]
        return sorted(matches, key=lambda p: p.submitted_utc, reverse=True)

    def anonymous_prayer_count(self) -> int:
        """Number of prayer requests submitted anonymously
        (C#: ``AnonymousPrayerCount``).
        """
        with self._lock:
            return sum(1 for p in self._prayers if p.is_anonymous)

    def chapter_verses(
        self, tradition: str, book: str, chapter: int
    ) -> List[ScriptureReference]:
        """Every verse of a tradition's book chapter (tradition + book matched
        case-insensitively), ordered by verse (C#: ``ChapterVerses``).
        """
        t_target = tradition.casefold()
        b_target = book.casefold()
        with self._lock:
            matches = [
                r
                for r in self._scripture.values()
                if r.tradition.casefold() == t_target
                and r.book.casefold() == b_target
                and r.chapter == chapter
            ]
        return sorted(matches, key=lambda r: r.verse)
