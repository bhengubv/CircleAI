# healthcare_primitives.py
#
# Port of CircleAI.Healthcare HealthcarePrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Healthcare vertical:
# patients, appointments, prescriptions.
#
# The C# ConcurrentDictionary stores map to plain dicts guarded by a single lock.
# C# OrderBy / OrderByDescending are stable, as is Python's sorted().

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, replace
from datetime import datetime
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class Patient:
    """Mirrors ``CircleAI.Healthcare.Patient`` —
    ``record(string PatientId, string Name, DateTime DateOfBirth)``.
    """

    patient_id: str
    name: str
    date_of_birth: datetime


@dataclass(frozen=True, slots=True)
class HealthAppointment:
    """Mirrors ``CircleAI.Healthcare.HealthAppointment`` — ``record(string ApptId,
    string PatientId, string Provider, DateTimeOffset AtUtc, string Status)``.
    """

    appt_id: str
    patient_id: str
    provider: str
    at_utc: datetime
    status: str


@dataclass(frozen=True, slots=True)
class Prescription:
    """Mirrors ``CircleAI.Healthcare.Prescription`` — ``record(string RxId,
    string PatientId, string MedicationName, string Dose, string Frequency,
    DateTimeOffset PrescribedUtc)``.
    """

    rx_id: str
    patient_id: str
    medication_name: str
    dose: str
    frequency: str
    prescribed_utc: datetime


class IHealthcareBoard(ABC):
    """In-memory board for patients, appointments and prescriptions."""

    @abstractmethod
    def register(self, p: Patient) -> None:
        ...

    @abstractmethod
    def get_patient(self, id: str) -> Optional[Patient]:
        ...

    @abstractmethod
    def schedule(self, a: HealthAppointment) -> None:
        ...

    @abstractmethod
    def update_status(self, appt_id: str, status: str) -> None:
        ...

    @abstractmethod
    def appointments_for(self, patient_id: str) -> List[HealthAppointment]:
        ...

    @abstractmethod
    def prescribe(self, r: Prescription) -> None:
        ...

    @abstractmethod
    def prescriptions_for(self, patient_id: str) -> List[Prescription]:
        ...


class InMemoryHealthcareBoard(IHealthcareBoard):
    """Thread-safe in-memory :class:`IHealthcareBoard`."""

    def __init__(self) -> None:
        self._patients: Dict[str, Patient] = {}
        self._appts: Dict[str, HealthAppointment] = {}
        self._rx: Dict[str, Prescription] = {}
        self._lock = threading.Lock()

    def register(self, p: Patient) -> None:
        if p is None:
            raise ValueError("patient must not be None")
        with self._lock:
            self._patients[p.patient_id] = p

    def get_patient(self, id: str) -> Optional[Patient]:
        with self._lock:
            return self._patients.get(id)

    def schedule(self, a: HealthAppointment) -> None:
        if a is None:
            raise ValueError("appointment must not be None")
        with self._lock:
            self._appts[a.appt_id] = a

    def update_status(self, appt_id: str, status: str) -> None:
        with self._lock:
            a = self._appts.get(appt_id)
            if a is None:
                raise RuntimeError(f"Unknown appointment {appt_id}")
            self._appts[appt_id] = replace(a, status=status)

    def appointments_for(self, patient_id: str) -> List[HealthAppointment]:
        with self._lock:
            rows = [a for a in self._appts.values() if a.patient_id == patient_id]
        return sorted(rows, key=lambda a: a.at_utc)

    def prescribe(self, r: Prescription) -> None:
        if r is None:
            raise ValueError("prescription must not be None")
        with self._lock:
            self._rx[r.rx_id] = r

    def prescriptions_for(self, patient_id: str) -> List[Prescription]:
        with self._lock:
            rows = [p for p in self._rx.values() if p.patient_id == patient_id]
        return sorted(rows, key=lambda p: p.prescribed_utc, reverse=True)
