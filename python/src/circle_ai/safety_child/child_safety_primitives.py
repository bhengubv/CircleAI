# child_safety_primitives.py
#
# Port of CircleAI.Safety.Child ChildSafetyPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory store for the Child Safety vertical:
# trusted-adult ring, geofences, check-in events.
#
# The C# ConcurrentDictionary stores map to plain dicts guarded by a single lock
# that also guards the check-in list. Haversine matches the C# formula exactly
# (R = 6_371_000 m, math.atan2/sqrt/sin/cos over radians). C# OrderBy /
# OrderByDescending are stable, as is Python's sorted().

from __future__ import annotations

import math
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class TrustedAdult:
    """Mirrors ``CircleAI.Safety.Child.TrustedAdult`` — ``record(string AdultId,
    string Name, string Phone, string Relationship, int RingPriority)``.
    """

    adult_id: str
    name: str
    phone: str
    relationship: str
    ring_priority: int


@dataclass(frozen=True, slots=True)
class Geofence:
    """Mirrors ``CircleAI.Safety.Child.Geofence`` — ``record(string FenceId,
    string Name, double CentreLat, double CentreLon, double RadiusMeters)``.
    """

    fence_id: str
    name: str
    centre_lat: float
    centre_lon: float
    radius_meters: float


@dataclass(frozen=True, slots=True)
class CheckIn:
    """Mirrors ``CircleAI.Safety.Child.CheckIn`` — ``record(string ChildId,
    string Status, double? Lat, double? Lon, DateTimeOffset AtUtc)``.
    """

    child_id: str
    status: str
    lat: Optional[float]
    lon: Optional[float]
    at_utc: datetime


class IChildSafetyBoard(ABC):
    """In-memory board for the trusted-adult ring, geofences and check-ins."""

    @abstractmethod
    def add_adult(self, a: TrustedAdult) -> None:
        ...

    @property
    @abstractmethod
    def ring_ordered(self) -> List[TrustedAdult]:
        ...

    @abstractmethod
    def define_geofence(self, g: Geofence) -> None:
        ...

    @abstractmethod
    def get_geofence(self, id: str) -> Optional[Geofence]:
        ...

    @abstractmethod
    def is_inside_any_fence(self, lat: float, lon: float) -> bool:
        ...

    @abstractmethod
    def record_check_in(self, c: CheckIn) -> None:
        ...

    @abstractmethod
    def recent_check_ins(self, child_id: str, limit: int = 20) -> List[CheckIn]:
        ...


class InMemoryChildSafetyBoard(IChildSafetyBoard):
    """Thread-safe in-memory :class:`IChildSafetyBoard`."""

    def __init__(self) -> None:
        self._adults: Dict[str, TrustedAdult] = {}
        self._fences: Dict[str, Geofence] = {}
        self._check_ins: List[CheckIn] = []
        self._lock = threading.Lock()

    def add_adult(self, a: TrustedAdult) -> None:
        if a is None:
            raise ValueError("adult must not be None")
        with self._lock:
            self._adults[a.adult_id] = a

    @property
    def ring_ordered(self) -> List[TrustedAdult]:
        with self._lock:
            return sorted(self._adults.values(), key=lambda x: x.ring_priority)

    def define_geofence(self, g: Geofence) -> None:
        if g is None:
            raise ValueError("geofence must not be None")
        with self._lock:
            self._fences[g.fence_id] = g

    def get_geofence(self, id: str) -> Optional[Geofence]:
        with self._lock:
            return self._fences.get(id)

    def is_inside_any_fence(self, lat: float, lon: float) -> bool:
        with self._lock:
            fences = list(self._fences.values())
        for g in fences:
            if _haversine_meters(g.centre_lat, g.centre_lon, lat, lon) <= g.radius_meters:
                return True
        return False

    def record_check_in(self, c: CheckIn) -> None:
        if c is None:
            raise ValueError("check-in must not be None")
        with self._lock:
            self._check_ins.append(c)

    def recent_check_ins(self, child_id: str, limit: int = 20) -> List[CheckIn]:
        if limit <= 0:
            raise ValueError("limit must be greater than zero")
        with self._lock:
            filtered = [c for c in self._check_ins if c.child_id == child_id]
        ordered = sorted(filtered, key=lambda c: c.at_utc, reverse=True)
        return ordered[:limit]


def _haversine_meters(a_lat: float, a_lon: float, b_lat: float, b_lon: float) -> float:
    R = 6_371_000.0

    def deg_to_rad(d: float) -> float:
        return d * math.pi / 180.0

    d_lat = deg_to_rad(b_lat - a_lat)
    d_lon = deg_to_rad(b_lon - a_lon)
    s1 = math.sin(d_lat / 2)
    s2 = math.sin(d_lon / 2)
    a = s1 * s1 + math.cos(deg_to_rad(a_lat)) * math.cos(deg_to_rad(b_lat)) * s2 * s2
    c = 2 * math.atan2(math.sqrt(a), math.sqrt(1 - a))
    return R * c
