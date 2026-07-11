# personal_health_primitives.py
#
# Port of CircleAI.Personal.Health PersonalHealthPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for personal health: vitals (BP,
# glucose, weight, ...), allergies, medications, last-reading helpers. Privacy:
# instances are user-scoped and never written to a shared store.
#
# The C# enum VitalKind maps to an IntEnum with the C# declaration order as its
# stable ordinals. The vitals List<> is guarded by a single lock; the allergy /
# medication ConcurrentDictionaries map to plain dicts. Vital Value is a double.
# ActiveMedications is ordered by Name (OrderBy is stable, as is Python sorted).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, replace
from datetime import datetime
from enum import IntEnum
from typing import Dict, List, Optional


class VitalKind(IntEnum):
    """Mirrors ``CircleAI.Personal.Health.VitalKind``. Ordinals are the C#
    declaration order and are stable across languages.
    """

    BLOOD_PRESSURE_SYSTOLIC = 0
    BLOOD_PRESSURE_DIASTOLIC = 1
    GLUCOSE_MG_DL = 2
    WEIGHT_KG = 3
    HEART_RATE_BPM = 4
    TEMPERATURE_C = 5
    OXYGEN_PCT = 6
    STEPS_COUNT = 7


@dataclass(frozen=True, slots=True)
class VitalReading:
    """Mirrors ``CircleAI.Personal.Health.VitalReading`` — ``record(VitalKind Kind,
    double Value, DateTimeOffset AtUtc, string? Note)``.
    """

    kind: VitalKind
    value: float
    at_utc: datetime
    note: Optional[str]


@dataclass(frozen=True, slots=True)
class Allergy:
    """Mirrors ``CircleAI.Personal.Health.Allergy`` —
    ``record(string AllergyId, string Substance, string Severity)``.
    """

    allergy_id: str
    substance: str
    severity: str


@dataclass(frozen=True, slots=True)
class Medication:
    """Mirrors ``CircleAI.Personal.Health.Medication`` — ``record(string MedId,
    string Name, string Dose, string Frequency, DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc)``.
    """

    med_id: str
    name: str
    dose: str
    frequency: str
    started_at_utc: datetime
    ended_at_utc: Optional[datetime]


class IPersonalHealthBoard(ABC):
    """In-memory board for vitals, allergies and medications."""

    @abstractmethod
    def record(self, v: VitalReading) -> None:
        ...

    @abstractmethod
    def read_since(self, kind: VitalKind, since: datetime) -> List[VitalReading]:
        ...

    @abstractmethod
    def latest(self, kind: VitalKind) -> Optional[VitalReading]:
        ...

    @abstractmethod
    def add_allergy(self, a: Allergy) -> None:
        ...

    @property
    @abstractmethod
    def allergies(self) -> List[Allergy]:
        ...

    @abstractmethod
    def add_medication(self, m: Medication) -> None:
        ...

    @abstractmethod
    def end_medication(self, med_id: str, ended_at_utc: datetime) -> None:
        ...

    @abstractmethod
    def active_medications(self) -> List[Medication]:
        ...


class InMemoryPersonalHealthBoard(IPersonalHealthBoard):
    """Thread-safe in-memory :class:`IPersonalHealthBoard`."""

    def __init__(self) -> None:
        self._vitals: List[VitalReading] = []
        self._allergies: Dict[str, Allergy] = {}
        self._meds: Dict[str, Medication] = {}
        self._lock = threading.Lock()

    def record(self, v: VitalReading) -> None:
        if v is None:
            raise ValueError("vital reading must not be None")
        with self._lock:
            self._vitals.append(v)

    def read_since(self, kind: VitalKind, since: datetime) -> List[VitalReading]:
        with self._lock:
            rows = [v for v in self._vitals if v.kind == kind and v.at_utc >= since]
        return sorted(rows, key=lambda v: v.at_utc)

    def latest(self, kind: VitalKind) -> Optional[VitalReading]:
        with self._lock:
            rows = [v for v in self._vitals if v.kind == kind]
        if not rows:
            return None
        return sorted(rows, key=lambda v: v.at_utc, reverse=True)[0]

    def add_allergy(self, a: Allergy) -> None:
        if a is None:
            raise ValueError("allergy must not be None")
        with self._lock:
            self._allergies[a.allergy_id] = a

    @property
    def allergies(self) -> List[Allergy]:
        with self._lock:
            return list(self._allergies.values())

    def add_medication(self, m: Medication) -> None:
        if m is None:
            raise ValueError("medication must not be None")
        with self._lock:
            self._meds[m.med_id] = m

    def end_medication(self, med_id: str, ended_at_utc: datetime) -> None:
        with self._lock:
            m = self._meds.get(med_id)
            if m is None:
                raise RuntimeError(f"Unknown medication {med_id}")
            self._meds[med_id] = replace(m, ended_at_utc=ended_at_utc)

    def active_medications(self) -> List[Medication]:
        with self._lock:
            rows = [m for m in self._meds.values() if m.ended_at_utc is None]
        return sorted(rows, key=lambda m: m.name)
