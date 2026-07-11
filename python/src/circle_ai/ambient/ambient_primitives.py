# ambient_primitives.py
#
# Port of CircleAI.Ambient AmbientPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Ambient vertical:
# environmental readings (temp / humidity / lux / noise) and per-location
# comfort preferences. C# ConcurrentDictionary -> dict; the readings list is
# guarded by a single lock. DateTimeOffset -> datetime. Latest returns a
# device's newest reading; History returns the newest `limit`. IsComfortable is
# True only when a preference AND a latest reading both exist and the reading is
# within tolerance: |temp - target| <= 2, |humidity - target| <= 10, and
# noise <= max.
#
# NOTE: the C# ``CircleAI.Ambient`` assembly has no ``AmbientDomainContext`` —
# only ``AmbientPrimitives`` (ported here) and ``AmbientCompanionMonitor`` (an
# ICompanionSession/host decorator, intentionally not ported).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class AmbientReading:
    """Mirrors ``CircleAI.Ambient.AmbientReading``."""

    device_id: str
    temperature_c: float
    humidity: float
    lux_light: float
    db_noise: float
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class AmbientPreference:
    """Mirrors ``CircleAI.Ambient.AmbientPreference``."""

    location: str
    target_temp_c: float
    target_humidity: float
    max_noise_db: float


class IAmbientBoard(ABC):
    """In-memory board for ambient readings and comfort preferences."""

    @abstractmethod
    def record(self, r: AmbientReading) -> None:
        ...

    @abstractmethod
    def latest(self, device_id: str) -> Optional[AmbientReading]:
        ...

    @abstractmethod
    def history(self, device_id: str, limit: int = 50) -> List[AmbientReading]:
        ...

    @abstractmethod
    def set_preference(self, p: AmbientPreference) -> None:
        ...

    @abstractmethod
    def get_preference(self, location: str) -> Optional[AmbientPreference]:
        ...

    @abstractmethod
    def is_comfortable(self, device_id: str, location: str) -> bool:
        ...


class InMemoryAmbientBoard(IAmbientBoard):
    """Thread-safe in-memory :class:`IAmbientBoard`."""

    def __init__(self) -> None:
        self._readings: List[AmbientReading] = []
        self._prefs: Dict[str, AmbientPreference] = {}
        self._lock = threading.Lock()

    def record(self, r: AmbientReading) -> None:
        if r is None:
            raise ValueError("ambient reading must not be None")
        with self._lock:
            self._readings.append(r)

    def _latest_unlocked(self, device_id: str) -> Optional[AmbientReading]:
        matches = [r for r in self._readings if r.device_id == device_id]
        if not matches:
            return None
        return max(matches, key=lambda r: r.at_utc)

    def latest(self, device_id: str) -> Optional[AmbientReading]:
        with self._lock:
            return self._latest_unlocked(device_id)

    def history(self, device_id: str, limit: int = 50) -> List[AmbientReading]:
        with self._lock:
            items = [r for r in self._readings if r.device_id == device_id]
        items.sort(key=lambda r: r.at_utc, reverse=True)
        return items[:limit]

    def set_preference(self, p: AmbientPreference) -> None:
        if p is None:
            raise ValueError("ambient preference must not be None")
        with self._lock:
            self._prefs[p.location] = p

    def get_preference(self, location: str) -> Optional[AmbientPreference]:
        with self._lock:
            return self._prefs.get(location)

    def is_comfortable(self, device_id: str, location: str) -> bool:
        with self._lock:
            pref = self._prefs.get(location)
            last = self._latest_unlocked(device_id)
            if pref is None or last is None:
                return False
            return (
                abs(last.temperature_c - pref.target_temp_c) <= 2
                and abs(last.humidity - pref.target_humidity) <= 10
                and last.db_noise <= pref.max_noise_db
            )
