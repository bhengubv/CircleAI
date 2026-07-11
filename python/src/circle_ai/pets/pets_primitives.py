# pets_primitives.py
#
# Port of CircleAI.Pets PetsPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory store for the Pets vertical: pets,
# vaccinations, weight history, vet appointments.
#
# C# ConcurrentDictionary stores map to plain dicts; the vaccination and weight
# lists are guarded by a single lock (mirroring the C# monitor lock). C#
# DateTimeOffset -> datetime, DateTime DateOfBirth -> datetime. `Pets` orders by
# Name (ordinal). VaccinationsFor returns newest-first; WeightHistory returns
# oldest-first. UpcomingAppointments returns appointments at/after "now"
# (DateTimeOffset.UtcNow), earliest first — timezone-aware UTC to compare with
# the timezone-aware AtUtc values.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class Pet:
    """Mirrors ``CircleAI.Pets.Pet`` — ``record(string PetId, string Name,
    string Species, string? Breed, DateTime DateOfBirth)``.
    """

    pet_id: str
    name: str
    species: str
    breed: Optional[str]
    date_of_birth: datetime


@dataclass(frozen=True, slots=True)
class Vaccination:
    """Mirrors ``CircleAI.Pets.Vaccination`` — ``record(string PetId,
    string Vaccine, DateTimeOffset AdministeredUtc, DateTimeOffset? BoosterDueUtc)``.
    """

    pet_id: str
    vaccine: str
    administered_utc: datetime
    booster_due_utc: Optional[datetime]


@dataclass(frozen=True, slots=True)
class WeightSample:
    """Mirrors ``CircleAI.Pets.WeightSample`` — ``record(string PetId,
    double WeightKg, DateTimeOffset AtUtc)``.
    """

    pet_id: str
    weight_kg: float
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class VetAppointment:
    """Mirrors ``CircleAI.Pets.VetAppointment`` — ``record(string ApptId,
    string PetId, string Reason, DateTimeOffset AtUtc, string Vet)``.
    """

    appt_id: str
    pet_id: str
    reason: str
    at_utc: datetime
    vet: str


class IPetsBoard(ABC):
    """In-memory board for pets, vaccinations, weights and appointments."""

    @abstractmethod
    def add(self, p: Pet) -> None:
        ...

    @abstractmethod
    def get_pet(self, id: str) -> Optional[Pet]:
        ...

    @property
    @abstractmethod
    def pets(self) -> List[Pet]:
        ...

    @abstractmethod
    def record_vaccination(self, v: Vaccination) -> None:
        ...

    @abstractmethod
    def vaccinations_for(self, pet_id: str) -> List[Vaccination]:
        ...

    @abstractmethod
    def record_weight(self, s: WeightSample) -> None:
        ...

    @abstractmethod
    def weight_history(self, pet_id: str) -> List[WeightSample]:
        ...

    @abstractmethod
    def schedule(self, a: VetAppointment) -> None:
        ...

    @abstractmethod
    def upcoming_appointments(self) -> List[VetAppointment]:
        ...


class InMemoryPetsBoard(IPetsBoard):
    """Thread-safe in-memory :class:`IPetsBoard`."""

    def __init__(self) -> None:
        self._pets: Dict[str, Pet] = {}
        self._vax: List[Vaccination] = []
        self._weights: List[WeightSample] = []
        self._appts: Dict[str, VetAppointment] = {}
        self._lock = threading.Lock()

    def add(self, p: Pet) -> None:
        if p is None:
            raise ValueError("pet must not be None")
        with self._lock:
            self._pets[p.pet_id] = p

    def get_pet(self, id: str) -> Optional[Pet]:
        with self._lock:
            return self._pets.get(id)

    @property
    def pets(self) -> List[Pet]:
        with self._lock:
            return sorted(self._pets.values(), key=lambda p: p.name)

    def record_vaccination(self, v: Vaccination) -> None:
        if v is None:
            raise ValueError("vaccination must not be None")
        with self._lock:
            self._vax.append(v)

    def vaccinations_for(self, pet_id: str) -> List[Vaccination]:
        with self._lock:
            matches = [v for v in self._vax if v.pet_id == pet_id]
        return sorted(matches, key=lambda v: v.administered_utc, reverse=True)

    def record_weight(self, s: WeightSample) -> None:
        if s is None:
            raise ValueError("weight sample must not be None")
        with self._lock:
            self._weights.append(s)

    def weight_history(self, pet_id: str) -> List[WeightSample]:
        with self._lock:
            matches = [w for w in self._weights if w.pet_id == pet_id]
        return sorted(matches, key=lambda w: w.at_utc)

    def schedule(self, a: VetAppointment) -> None:
        if a is None:
            raise ValueError("appointment must not be None")
        with self._lock:
            self._appts[a.appt_id] = a

    def upcoming_appointments(self) -> List[VetAppointment]:
        now = datetime.now(timezone.utc)
        with self._lock:
            rows = [a for a in self._appts.values() if a.at_utc >= now]
        return sorted(rows, key=lambda a: a.at_utc)
