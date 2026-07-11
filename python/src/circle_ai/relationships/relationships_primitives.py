# relationships_primitives.py
#
# Port of CircleAI.Relationships RelationshipsPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory CRM-lite for personal relationships:
# contacts, important dates, a last-contact tracker. C# ConcurrentDictionary ->
# dict; the touchpoint-events list is guarded by a single lock. DateTime ->
# datetime, DateTimeOffset? -> Optional[datetime]. UpcomingThisMonth returns
# dates whose month == current UTC month, ordered by day. LastContact is the
# newest touchpoint for a contact (or None). NotContactedSince returns contacts
# whose last touchpoint is None or before the cutoff.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class PersonContact:
    """Mirrors ``CircleAI.Relationships.PersonContact`` — ``string? Notes``."""

    contact_id: str
    name: str
    relationship: str
    notes: Optional[str]


@dataclass(frozen=True, slots=True)
class ImportantDate:
    """Mirrors ``CircleAI.Relationships.ImportantDate``."""

    date_id: str
    contact_id: str
    kind: str
    date: datetime


@dataclass(frozen=True, slots=True)
class ContactEvent:
    """Mirrors ``CircleAI.Relationships.ContactEvent`` — ``string? Note``."""

    contact_id: str
    kind: str
    at_utc: datetime
    note: Optional[str]


class IRelationshipsBoard(ABC):
    """In-memory board for personal contacts, important dates and touchpoints."""

    @abstractmethod
    def add_contact(self, c: PersonContact) -> None:
        ...

    @abstractmethod
    def get_contact(self, id: str) -> Optional[PersonContact]:
        ...

    @property
    @abstractmethod
    def contacts(self) -> List[PersonContact]:
        ...

    @abstractmethod
    def add_important_date(self, d: ImportantDate) -> None:
        ...

    @abstractmethod
    def upcoming_this_month(self) -> List[ImportantDate]:
        ...

    @abstractmethod
    def record_touchpoint(self, e: ContactEvent) -> None:
        ...

    @abstractmethod
    def last_contact(self, contact_id: str) -> Optional[datetime]:
        ...

    @abstractmethod
    def not_contacted_since(self, cutoff: datetime) -> List[PersonContact]:
        ...


class InMemoryRelationshipsBoard(IRelationshipsBoard):
    """Thread-safe in-memory :class:`IRelationshipsBoard`."""

    def __init__(self) -> None:
        self._contacts: Dict[str, PersonContact] = {}
        self._dates: Dict[str, ImportantDate] = {}
        self._events: List[ContactEvent] = []
        self._lock = threading.Lock()

    def add_contact(self, c: PersonContact) -> None:
        if c is None:
            raise ValueError("contact must not be None")
        with self._lock:
            self._contacts[c.contact_id] = c

    def get_contact(self, id: str) -> Optional[PersonContact]:
        with self._lock:
            return self._contacts.get(id)

    @property
    def contacts(self) -> List[PersonContact]:
        with self._lock:
            items = list(self._contacts.values())
        items.sort(key=lambda c: c.name)
        return items

    def add_important_date(self, d: ImportantDate) -> None:
        if d is None:
            raise ValueError("important date must not be None")
        with self._lock:
            self._dates[d.date_id] = d

    def upcoming_this_month(self) -> List[ImportantDate]:
        now = datetime.now(timezone.utc)
        with self._lock:
            items = [d for d in self._dates.values() if d.date.month == now.month]
        items.sort(key=lambda d: d.date.day)
        return items

    def record_touchpoint(self, e: ContactEvent) -> None:
        if e is None:
            raise ValueError("contact event must not be None")
        with self._lock:
            self._events.append(e)

    def _last_contact_unlocked(self, contact_id: str) -> Optional[datetime]:
        matches = [e for e in self._events if e.contact_id == contact_id]
        if not matches:
            return None
        return max(matches, key=lambda e: e.at_utc).at_utc

    def last_contact(self, contact_id: str) -> Optional[datetime]:
        with self._lock:
            return self._last_contact_unlocked(contact_id)

    def not_contacted_since(self, cutoff: datetime) -> List[PersonContact]:
        with self._lock:
            result: List[PersonContact] = []
            for c in self._contacts.values():
                last = self._last_contact_unlocked(c.contact_id)
                if last is None or last < cutoff:
                    result.append(c)
            return result
