# safety_primitives.py
#
# Port of CircleAI.Safety SafetyPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory store for the Safety vertical:
# incidents, hazards, emergency contacts, severity-routing.
#
# C# OrderByDescending is a STABLE sort; Python's sorted() is also stable, so
# ties preserve insertion order in both. The C# ConcurrentDictionary for hazards
# maps to a plain dict guarded by the same lock as the list state.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import Dict, List, Optional


class IncidentSeverity(IntEnum):
    """Severity of a logged safety incident.

    Mirrors ``CircleAI.Safety.IncidentSeverity``. Ordinals are the C#
    declaration order and drive severity routing (``AtOrAboveSeverity``).
    """

    INFO = 0
    WARNING = 1
    CRITICAL = 2
    EMERGENCY = 3


@dataclass(frozen=True, slots=True)
class Incident:
    """Mirrors ``CircleAI.Safety.Incident`` — ``record(string IncidentId,
    IncidentSeverity Severity, string Description, double? Latitude,
    double? Longitude, DateTimeOffset AtUtc)``.
    """

    incident_id: str
    severity: IncidentSeverity
    description: str
    latitude: Optional[float]
    longitude: Optional[float]
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class Hazard:
    """Mirrors ``CircleAI.Safety.Hazard`` — ``record(string HazardId,
    string Description, string Category, DateTimeOffset NotedUtc)``.
    """

    hazard_id: str
    description: str
    category: str
    noted_utc: datetime


@dataclass(frozen=True, slots=True)
class EmergencyContact:
    """Mirrors ``CircleAI.Safety.EmergencyContact`` — ``record(string ContactId,
    string Name, string Phone, string Relationship)``.
    """

    contact_id: str
    name: str
    phone: str
    relationship: str


class ISafetyBoard(ABC):
    """In-memory board for incidents, hazards and emergency contacts."""

    @abstractmethod
    def log(self, i: Incident) -> None:
        ...

    @property
    @abstractmethod
    def active(self) -> List[Incident]:
        ...

    @abstractmethod
    def at_or_above_severity(self, minimum: IncidentSeverity) -> List[Incident]:
        ...

    @abstractmethod
    def note_hazard(self, h: Hazard) -> None:
        ...

    @property
    @abstractmethod
    def hazards(self) -> List[Hazard]:
        ...

    @abstractmethod
    def add_contact(self, c: EmergencyContact) -> None:
        ...

    @property
    @abstractmethod
    def first_contact(self) -> Optional[EmergencyContact]:
        ...

    @property
    @abstractmethod
    def contacts(self) -> List[EmergencyContact]:
        ...


class InMemorySafetyBoard(ISafetyBoard):
    """Thread-safe in-memory :class:`ISafetyBoard`."""

    def __init__(self) -> None:
        self._incidents: List[Incident] = []
        self._hazards: Dict[str, Hazard] = {}
        self._contacts: List[EmergencyContact] = []
        self._lock = threading.Lock()

    def log(self, i: Incident) -> None:
        if i is None:
            raise ValueError("incident must not be None")
        with self._lock:
            self._incidents.append(i)

    @property
    def active(self) -> List[Incident]:
        with self._lock:
            return sorted(self._incidents, key=lambda x: x.at_utc, reverse=True)

    def at_or_above_severity(self, minimum: IncidentSeverity) -> List[Incident]:
        with self._lock:
            filtered = [x for x in self._incidents if int(x.severity) >= int(minimum)]
            return sorted(filtered, key=lambda x: x.at_utc, reverse=True)

    def note_hazard(self, h: Hazard) -> None:
        if h is None:
            raise ValueError("hazard must not be None")
        # C# uses a ConcurrentDictionary indexer (thread-safe upsert); guard here.
        with self._lock:
            self._hazards[h.hazard_id] = h

    @property
    def hazards(self) -> List[Hazard]:
        with self._lock:
            return sorted(self._hazards.values(), key=lambda x: x.noted_utc, reverse=True)

    def add_contact(self, c: EmergencyContact) -> None:
        if c is None:
            raise ValueError("contact must not be None")
        with self._lock:
            self._contacts.append(c)

    @property
    def first_contact(self) -> Optional[EmergencyContact]:
        with self._lock:
            return self._contacts[0] if self._contacts else None

    @property
    def contacts(self) -> List[EmergencyContact]:
        with self._lock:
            return list(self._contacts)

    @property
    def incident_count(self) -> int:
        """Total number of incidents logged since this board was created
        (C#: ``IncidentCount``).
        """
        with self._lock:
            return len(self._incidents)

    def remove_contact(self, contact_id: str) -> bool:
        """Remove the first emergency contact matching ``contact_id`` (ordinal).
        Returns True if one was removed (C#: ``RemoveContact``).
        """
        if not contact_id:
            return False
        with self._lock:
            for idx, c in enumerate(self._contacts):
                if c.contact_id == contact_id:
                    del self._contacts[idx]
                    return True
            return False

    def hazards_by_category(self, category: str) -> List[Hazard]:
        """Hazards filed under ``category`` (case-insensitive), newest-first
        (C#: ``HazardsByCategory``). Empty ``category`` yields nothing.
        """
        if not category:
            return []
        target = category.casefold()
        with self._lock:
            filtered = [
                h for h in self._hazards.values() if h.category.casefold() == target
            ]
        return sorted(filtered, key=lambda h: h.noted_utc, reverse=True)

    def contacts_by_relationship(
        self, relationship: str
    ) -> List[EmergencyContact]:
        """Emergency contacts with a given ``relationship`` (e.g. "spouse",
        "doctor"), case-insensitive (C#: ``ContactsByRelationship``). Empty
        ``relationship`` yields nothing.
        """
        if not relationship:
            return []
        target = relationship.casefold()
        with self._lock:
            return [
                c
                for c in self._contacts
                if c.relationship.casefold() == target
            ]
