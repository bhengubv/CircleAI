# real_estate_primitives.py
#
# Port of CircleAI.RealEstate RealEstatePrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory store for the RealEstate vertical:
# properties, listings, valuations, viewings + a suburb-average comparable.
#
# C# ConcurrentDictionary stores map to plain dicts; the valuation/viewing lists
# are guarded by a single lock (mirroring the C# monitor lock). C# decimal money
# maps to decimal.Decimal, DateTimeOffset -> datetime. PropertyKind is an IntEnum
# with the C# ordinals (Apartment=0..Land=4). ActiveInSuburb returns active
# listings whose property is in the given suburb (case-insensitive), newest
# first; a blank suburb raises. SuburbAverage returns None on an empty suburb
# (mirroring the C# nullable decimal?).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from enum import IntEnum
from typing import Dict, List, Optional


class PropertyKind(IntEnum):
    """Mirrors ``CircleAI.RealEstate.PropertyKind`` (ordinals preserved)."""

    Apartment = 0
    House = 1
    Townhouse = 2
    Commercial = 3
    Land = 4


@dataclass(frozen=True, slots=True)
class Property:
    """Mirrors ``CircleAI.RealEstate.Property`` — ``record(string PropertyId,
    string Suburb, PropertyKind Kind, int Beds, int Baths, double FloorAreaM2)``.
    """

    property_id: str
    suburb: str
    kind: PropertyKind
    beds: int
    baths: int
    floor_area_m2: float


@dataclass(frozen=True, slots=True)
class Listing:
    """Mirrors ``CircleAI.RealEstate.Listing`` — ``record(string ListingId,
    string PropertyId, decimal AskingPrice, string Currency,
    DateTimeOffset ListedUtc, bool IsActive)``.
    """

    listing_id: str
    property_id: str
    asking_price: Decimal
    currency: str
    listed_utc: datetime
    is_active: bool


@dataclass(frozen=True, slots=True)
class Valuation:
    """Mirrors ``CircleAI.RealEstate.Valuation`` — ``record(string PropertyId,
    decimal EstimatedValue, string Source, DateTimeOffset AtUtc)``.
    """

    property_id: str
    estimated_value: Decimal
    source: str
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class Viewing:
    """Mirrors ``CircleAI.RealEstate.Viewing`` — ``record(string ViewingId,
    string ListingId, string AttendeeName, DateTimeOffset AtUtc)``.
    """

    viewing_id: str
    listing_id: str
    attendee_name: str
    at_utc: datetime


class IRealEstateBoard(ABC):
    """In-memory board for properties, listings, valuations and viewings."""

    @abstractmethod
    def register_property(self, p: Property) -> None:
        ...

    @abstractmethod
    def list(self, l: Listing) -> None:
        ...

    @abstractmethod
    def close(self, listing_id: str) -> None:
        ...

    @abstractmethod
    def value(self, v: Valuation) -> None:
        ...

    @abstractmethod
    def schedule_viewing(self, v: Viewing) -> None:
        ...

    @abstractmethod
    def active_in_suburb(self, suburb: str) -> List[Listing]:
        ...

    @abstractmethod
    def suburb_average(self, suburb: str) -> Optional[Decimal]:
        ...


class InMemoryRealEstateBoard(IRealEstateBoard):
    """Thread-safe in-memory :class:`IRealEstateBoard`."""

    def __init__(self) -> None:
        self._props: Dict[str, Property] = {}
        self._listings: Dict[str, Listing] = {}
        self._vals: List[Valuation] = []
        self._viewings: List[Viewing] = []
        self._lock = threading.Lock()

    def register_property(self, p: Property) -> None:
        if p is None:
            raise ValueError("property must not be None")
        with self._lock:
            self._props[p.property_id] = p

    def list(self, l: Listing) -> None:
        if l is None:
            raise ValueError("listing must not be None")
        with self._lock:
            self._listings[l.listing_id] = l

    def close(self, listing_id: str) -> None:
        with self._lock:
            l = self._listings.get(listing_id)
            if l is None:
                raise RuntimeError(f"Unknown listing {listing_id}")
            self._listings[listing_id] = Listing(
                l.listing_id, l.property_id, l.asking_price, l.currency, l.listed_utc, False
            )

    def value(self, v: Valuation) -> None:
        if v is None:
            raise ValueError("valuation must not be None")
        with self._lock:
            self._vals.append(v)

    def schedule_viewing(self, v: Viewing) -> None:
        if v is None:
            raise ValueError("viewing must not be None")
        with self._lock:
            self._viewings.append(v)

    def active_in_suburb(self, suburb: str) -> List[Listing]:
        if suburb is None or not suburb.strip():
            raise ValueError("suburb required")
        with self._lock:
            rows = [
                l
                for l in self._listings.values()
                if l.is_active
                and l.property_id in self._props
                and self._props[l.property_id].suburb.casefold() == suburb.casefold()
            ]
        return sorted(rows, key=lambda l: l.listed_utc, reverse=True)

    def suburb_average(self, suburb: str) -> Optional[Decimal]:
        rows = self.active_in_suburb(suburb)
        if len(rows) == 0:
            return None
        return sum((l.asking_price for l in rows), Decimal(0)) / len(rows)
