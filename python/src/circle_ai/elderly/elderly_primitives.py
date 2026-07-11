# elderly_primitives.py
#
# Port of CircleAI.Elderly ElderlyPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Elderly-care vertical:
# care plans (keyed by resident name), medication reminders, wellbeing check-ins.
#
# C# ConcurrentDictionary stores map to plain dicts; the check-in list is guarded
# by a single lock (mirroring the C# monitor lock). C# DateTimeOffset -> datetime,
# TimeSpan DailyAt -> timedelta. ActiveRemindersFor returns the resident's active
# reminders. LatestCheckIn returns the newest check-in (or None). MissedCheckIn is
# True when there is no check-in or the latest predates `since`. Deactivating an
# unknown reminder raises RuntimeError.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, replace
from datetime import datetime, timedelta
from typing import Dict, List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class CarePlan:
    """Mirrors ``CircleAI.Elderly.CarePlan`` — ``record(string PlanId,
    string ResidentName, IReadOnlyList<string> MedicalConditions,
    IReadOnlyList<string> Allergies, string CarerNotes)``.
    """

    plan_id: str
    resident_name: str
    medical_conditions: Sequence[str]
    allergies: Sequence[str]
    carer_notes: str


@dataclass(frozen=True, slots=True)
class MedReminder:
    """Mirrors ``CircleAI.Elderly.MedReminder`` — ``record(string ReminderId,
    string ResidentName, string Medication, TimeSpan DailyAt, bool Active)``.
    """

    reminder_id: str
    resident_name: str
    medication: str
    daily_at: timedelta
    active: bool


@dataclass(frozen=True, slots=True)
class CheckIn:
    """Mirrors ``CircleAI.Elderly.CheckIn`` — ``record(string CheckInId,
    string ResidentName, DateTimeOffset AtUtc, string Status, string? Note)``.
    """

    check_in_id: str
    resident_name: str
    at_utc: datetime
    status: str
    note: Optional[str]


class IElderlyCareBoard(ABC):
    """In-memory board for care plans, med reminders and check-ins."""

    @abstractmethod
    def set_plan(self, p: CarePlan) -> None:
        ...

    @abstractmethod
    def get_plan(self, resident: str) -> Optional[CarePlan]:
        ...

    @abstractmethod
    def add_reminder(self, r: MedReminder) -> None:
        ...

    @abstractmethod
    def deactivate_reminder(self, reminder_id: str) -> None:
        ...

    @abstractmethod
    def active_reminders_for(self, resident: str) -> List[MedReminder]:
        ...

    @abstractmethod
    def record_check_in(self, c: CheckIn) -> None:
        ...

    @abstractmethod
    def latest_check_in(self, resident: str) -> Optional[CheckIn]:
        ...

    @abstractmethod
    def missed_check_in(self, resident: str, since: datetime) -> bool:
        ...


class InMemoryElderlyCareBoard(IElderlyCareBoard):
    """Thread-safe in-memory :class:`IElderlyCareBoard`."""

    def __init__(self) -> None:
        self._plans: Dict[str, CarePlan] = {}
        self._reminders: Dict[str, MedReminder] = {}
        self._check_ins: List[CheckIn] = []
        self._lock = threading.Lock()

    def set_plan(self, p: CarePlan) -> None:
        if p is None:
            raise ValueError("care plan must not be None")
        with self._lock:
            self._plans[p.resident_name] = p

    def get_plan(self, resident: str) -> Optional[CarePlan]:
        with self._lock:
            return self._plans.get(resident)

    def add_reminder(self, r: MedReminder) -> None:
        if r is None:
            raise ValueError("reminder must not be None")
        with self._lock:
            self._reminders[r.reminder_id] = r

    def deactivate_reminder(self, reminder_id: str) -> None:
        with self._lock:
            r = self._reminders.get(reminder_id)
            if r is None:
                raise RuntimeError(f"Unknown reminder {reminder_id}")
            self._reminders[reminder_id] = replace(r, active=False)

    def active_reminders_for(self, resident: str) -> List[MedReminder]:
        with self._lock:
            return [
                r
                for r in self._reminders.values()
                if r.resident_name == resident and r.active
            ]

    def record_check_in(self, c: CheckIn) -> None:
        if c is None:
            raise ValueError("check-in must not be None")
        with self._lock:
            self._check_ins.append(c)

    def latest_check_in(self, resident: str) -> Optional[CheckIn]:
        with self._lock:
            matches = [c for c in self._check_ins if c.resident_name == resident]
        if not matches:
            return None
        return max(matches, key=lambda c: c.at_utc)

    def missed_check_in(self, resident: str, since: datetime) -> bool:
        latest = self.latest_check_in(resident)
        return latest is None or latest.at_utc < since
