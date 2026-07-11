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
