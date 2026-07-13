# beauty_primitives.py
#
# Port of CircleAI.Beauty BeautyPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Beauty vertical:
# treatments (priced), appointments, skin profiles. C# ConcurrentDictionary ->
# dict; the appointment list is guarded by a single lock. C# ``decimal Price``
# -> :class:`decimal.Decimal`. RecommendFor returns treatments whose Name
# contains one of the client's concerns (case-insensitive); empty list when the
# client has no saved profile.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Dict, List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class Treatment:
    """Mirrors ``CircleAI.Beauty.Treatment`` — ``decimal Price``."""

    treatment_id: str
    name: str
    duration_minutes: int
    price: Decimal
    currency: str


@dataclass(frozen=True, slots=True)
class Appointment:
    """Mirrors ``CircleAI.Beauty.Appointment`` — ``string? Notes``."""

    appt_id: str
    client_name: str
    treatment_id: str
    at_utc: datetime
    notes: Optional[str]


@dataclass(frozen=True, slots=True)
class SkinProfile:
    """Mirrors ``CircleAI.Beauty.SkinProfile``."""

    client_name: str
    skin_type: str
    concerns: Sequence[str]


class IBeautyBoard(ABC):
    """In-memory board for treatments, appointments and skin profiles."""

    @abstractmethod
    def add_treatment(self, t: Treatment) -> None:
        ...

    @abstractmethod
    def get_treatment(self, id: str) -> Optional[Treatment]:
        ...

    @abstractmethod
    def book(self, a: Appointment) -> None:
        ...

    @abstractmethod
    def appointments_between(
        self, start: datetime, end: datetime
    ) -> List[Appointment]:
        ...

    @abstractmethod
    def save_profile(self, p: SkinProfile) -> None:
        ...

    @abstractmethod
    def get_profile(self, client_name: str) -> Optional[SkinProfile]:
        ...

    @abstractmethod
    def recommend_for(self, client_name: str) -> List[Treatment]:
        ...


class InMemoryBeautyBoard(IBeautyBoard):
    """Thread-safe in-memory :class:`IBeautyBoard`."""

    def __init__(self) -> None:
        self._treatments: Dict[str, Treatment] = {}
        self._appts: List[Appointment] = []
        self._profiles: Dict[str, SkinProfile] = {}
        self._lock = threading.Lock()

    def add_treatment(self, t: Treatment) -> None:
        if t is None:
            raise ValueError("treatment must not be None")
        with self._lock:
            self._treatments[t.treatment_id] = t

    def get_treatment(self, id: str) -> Optional[Treatment]:
        with self._lock:
            return self._treatments.get(id)

    def book(self, a: Appointment) -> None:
        if a is None:
            raise ValueError("appointment must not be None")
        with self._lock:
            self._appts.append(a)

    def appointments_between(
        self, start: datetime, end: datetime
    ) -> List[Appointment]:
        with self._lock:
            items = [a for a in self._appts if start <= a.at_utc <= end]
        items.sort(key=lambda a: a.at_utc)
        return items

    def save_profile(self, p: SkinProfile) -> None:
        if p is None:
            raise ValueError("skin profile must not be None")
        with self._lock:
            self._profiles[p.client_name] = p

    def get_profile(self, client_name: str) -> Optional[SkinProfile]:
        with self._lock:
            return self._profiles.get(client_name)

    def recommend_for(self, client_name: str) -> List[Treatment]:
        with self._lock:
            p = self._profiles.get(client_name)
            if p is None:
                return []
            concerns = [c.casefold() for c in p.concerns]
            return [
                t
                for t in self._treatments.values()
                if any(c in t.name.casefold() for c in concerns)
            ]

    @property
    def treatment_count(self) -> int:
        """Number of treatments on the menu (C#: ``TreatmentCount``)."""
        with self._lock:
            return len(self._treatments)

    def cancel_appointment(self, appt_id: str) -> bool:
        """Cancel every appointment matching ``appt_id`` (ordinal). Returns True
        if at least one was removed (C#: ``CancelAppointment``).
        """
        with self._lock:
            before = len(self._appts)
            self._appts = [a for a in self._appts if a.appt_id != appt_id]
            return len(self._appts) < before

    def appointments_for_client(self, client_name: str) -> List[Appointment]:
        """A client's appointments (case-insensitive), earliest first
        (C#: ``AppointmentsForClient``).
        """
        target = client_name.casefold()
        with self._lock:
            items = [
                a for a in self._appts if a.client_name.casefold() == target
            ]
        items.sort(key=lambda a: a.at_utc)
        return items

    def treatments_under(self, max_price: Decimal) -> List[Treatment]:
        """Treatments priced at or below ``max_price``, cheapest first
        (C#: ``TreatmentsUnder``).
        """
        with self._lock:
            matches = [
                t for t in self._treatments.values() if t.price <= max_price
            ]
        return sorted(matches, key=lambda t: t.price)

    def next_appointment_for(
        self, client_name: str, now: datetime
    ) -> Optional[Appointment]:
        """A client's next appointment at or after ``now`` (case-insensitive),
        or None (C#: ``NextAppointmentFor``).
        """
        target = client_name.casefold()
        with self._lock:
            upcoming = [
                a
                for a in self._appts
                if a.client_name.casefold() == target and a.at_utc >= now
            ]
        if not upcoming:
            return None
        return min(upcoming, key=lambda a: a.at_utc)

    def scheduled_revenue_between(
        self, start: datetime, end: datetime
    ) -> Decimal:
        """Total priced revenue of appointments in the inclusive [start, end]
        window whose treatment is known (C#: ``ScheduledRevenueBetween``).
        """
        total = Decimal(0)
        with self._lock:
            for a in self._appts:
                if start <= a.at_utc <= end:
                    t = self._treatments.get(a.treatment_id)
                    if t is not None:
                        total += t.price
        return total
