# travel_primitives.py
#
# Port of CircleAI.Travel TravelPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Travel vertical: flights,
# hotel stays, trips, trip-cost totals. C# ConcurrentDictionary -> dict.
# ``decimal Price/NightlyRate`` -> Decimal, DateTimeOffset/DateTime -> datetime.
#
# C# has two overloads ``Add(Flight)`` and ``Add(HotelStay)``. Python has no
# ad-hoc overloading, so the board exposes :meth:`add_flight` and
# :meth:`add_stay`. TripCost sums each flight's price plus each stay's
# ``NightlyRate * max(1, nights)`` (nights = (CheckOut - CheckIn).Days).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Dict, List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class Flight:
    """Mirrors ``CircleAI.Travel.Flight`` — ``decimal Price``."""

    flight_id: str
    from_: str
    to: str
    depart_utc: datetime
    arrive_utc: datetime
    carrier: str
    cabin: str
    price: Decimal
    currency: str


@dataclass(frozen=True, slots=True)
class HotelStay:
    """Mirrors ``CircleAI.Travel.HotelStay`` — ``decimal NightlyRate``."""

    stay_id: str
    hotel: str
    city: str
    check_in: datetime
    check_out: datetime
    nightly_rate: Decimal
    currency: str


@dataclass(frozen=True, slots=True)
class TravelTrip:
    """Mirrors ``CircleAI.Travel.TravelTrip``."""

    trip_id: str
    name: str
    start_date: datetime
    end_date: datetime
    flight_ids: Sequence[str]
    stay_ids: Sequence[str]


class ITravelBoard(ABC):
    """In-memory board for flights, hotel stays and trips."""

    @abstractmethod
    def add_flight(self, f: Flight) -> None:
        ...

    @abstractmethod
    def add_stay(self, s: HotelStay) -> None:
        ...

    @abstractmethod
    def plan(self, t: TravelTrip) -> None:
        ...

    @abstractmethod
    def get_trip(self, id: str) -> Optional[TravelTrip]:
        ...

    @abstractmethod
    def get_flight(self, id: str) -> Optional[Flight]:
        ...

    @abstractmethod
    def get_stay(self, id: str) -> Optional[HotelStay]:
        ...

    @abstractmethod
    def trip_cost(self, trip_id: str) -> Decimal:
        ...

    @abstractmethod
    def upcoming_trips(self, now: datetime) -> List[TravelTrip]:
        ...


class InMemoryTravelBoard(ITravelBoard):
    """Thread-safe in-memory :class:`ITravelBoard`."""

    def __init__(self) -> None:
        self._flights: Dict[str, Flight] = {}
        self._stays: Dict[str, HotelStay] = {}
        self._trips: Dict[str, TravelTrip] = {}
        self._lock = threading.Lock()

    def add_flight(self, f: Flight) -> None:
        if f is None:
            raise ValueError("flight must not be None")
        with self._lock:
            self._flights[f.flight_id] = f

    def add_stay(self, s: HotelStay) -> None:
        if s is None:
            raise ValueError("hotel stay must not be None")
        with self._lock:
            self._stays[s.stay_id] = s

    def plan(self, t: TravelTrip) -> None:
        if t is None:
            raise ValueError("trip must not be None")
        with self._lock:
            self._trips[t.trip_id] = t

    def get_trip(self, id: str) -> Optional[TravelTrip]:
        with self._lock:
            return self._trips.get(id)

    def get_flight(self, id: str) -> Optional[Flight]:
        with self._lock:
            return self._flights.get(id)

    def get_stay(self, id: str) -> Optional[HotelStay]:
        with self._lock:
            return self._stays.get(id)

    def trip_cost(self, trip_id: str) -> Decimal:
        with self._lock:
            t = self._trips.get(trip_id)
            if t is None:
                raise RuntimeError(f"Unknown trip {trip_id}")
            total = Decimal(0)
            for fid in t.flight_ids:
                f = self._flights.get(fid)
                if f is not None:
                    total += f.price
            for sid in t.stay_ids:
                s = self._stays.get(sid)
                if s is not None:
                    nights = max(1, (s.check_out - s.check_in).days)
                    total += s.nightly_rate * nights
            return total

    def upcoming_trips(self, now: datetime) -> List[TravelTrip]:
        with self._lock:
            items = [t for t in self._trips.values() if t.start_date >= now]
        items.sort(key=lambda t: t.start_date)
        return items
