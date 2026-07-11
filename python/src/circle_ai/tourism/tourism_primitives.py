# tourism_primitives.py
#
# Port of CircleAI.Tourism TourismPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Tourism vertical:
# attractions, itineraries, bookings. C# ConcurrentDictionary -> dict; the
# bookings list is guarded by a single lock and exposed via the read-only
# ``bookings`` property (snapshot copy). City / tag lookups are case-insensitive
# and ordered by attraction name; blank city / tag raises.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta
from decimal import Decimal
from typing import Dict, List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class Attraction:
    """Mirrors ``CircleAI.Tourism.Attraction``."""

    attraction_id: str
    name: str
    city: str
    country: str
    lat: float
    lon: float
    tags: Sequence[str]


@dataclass(frozen=True, slots=True)
class ItineraryItem:
    """Mirrors ``CircleAI.Tourism.ItineraryItem`` — ``TimeSpan StartLocal/EndLocal``."""

    day_index: int
    start_local: timedelta
    end_local: timedelta
    attraction_id: str
    note: Optional[str]


@dataclass(frozen=True, slots=True)
class Itinerary:
    """Mirrors ``CircleAI.Tourism.Itinerary``."""

    itinerary_id: str
    title: str
    items: Sequence[ItineraryItem]


@dataclass(frozen=True, slots=True)
class TourismBooking:
    """Mirrors ``CircleAI.Tourism.TourismBooking`` — ``decimal TotalPrice``."""

    booking_id: str
    itinerary_id: str
    start_date: datetime
    travelers: int
    total_price: Decimal
    currency: str


class ITourismBoard(ABC):
    """In-memory board for attractions, itineraries and bookings."""

    @abstractmethod
    def add(self, a: Attraction) -> None:
        ...

    @abstractmethod
    def attractions_in_city(self, city: str) -> List[Attraction]:
        ...

    @abstractmethod
    def by_tag(self, tag: str) -> List[Attraction]:
        ...

    @abstractmethod
    def plan(self, i: Itinerary) -> None:
        ...

    @abstractmethod
    def get_itinerary(self, id: str) -> Optional[Itinerary]:
        ...

    @abstractmethod
    def book(self, b: TourismBooking) -> None:
        ...

    @property
    @abstractmethod
    def bookings(self) -> List[TourismBooking]:
        ...


class InMemoryTourismBoard(ITourismBoard):
    """Thread-safe in-memory :class:`ITourismBoard`."""

    def __init__(self) -> None:
        self._attractions: Dict[str, Attraction] = {}
        self._itineraries: Dict[str, Itinerary] = {}
        self._bookings: List[TourismBooking] = []
        self._lock = threading.Lock()

    def add(self, a: Attraction) -> None:
        if a is None:
            raise ValueError("attraction must not be None")
        with self._lock:
            self._attractions[a.attraction_id] = a

    def attractions_in_city(self, city: str) -> List[Attraction]:
        if city is None or city.strip() == "":
            raise ValueError("city required")
        target = city.casefold()
        with self._lock:
            items = [
                a for a in self._attractions.values() if a.city.casefold() == target
            ]
        items.sort(key=lambda a: a.name)
        return items

    def by_tag(self, tag: str) -> List[Attraction]:
        if tag is None or tag.strip() == "":
            raise ValueError("tag required")
        target = tag.casefold()
        with self._lock:
            items = [
                a
                for a in self._attractions.values()
                if any(t.casefold() == target for t in a.tags)
            ]
        items.sort(key=lambda a: a.name)
        return items

    def plan(self, i: Itinerary) -> None:
        if i is None:
            raise ValueError("itinerary must not be None")
        with self._lock:
            self._itineraries[i.itinerary_id] = i

    def get_itinerary(self, id: str) -> Optional[Itinerary]:
        with self._lock:
            return self._itineraries.get(id)

    def book(self, b: TourismBooking) -> None:
        if b is None:
            raise ValueError("booking must not be None")
        with self._lock:
            self._bookings.append(b)

    @property
    def bookings(self) -> List[TourismBooking]:
        with self._lock:
            return list(self._bookings)
