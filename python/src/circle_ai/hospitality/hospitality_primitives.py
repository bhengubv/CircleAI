# hospitality_primitives.py
#
# Port of CircleAI.Hospitality HospitalityPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Hospitality vertical:
# hotel rooms, guest reservations, front-desk notes. C# ConcurrentDictionary ->
# dict; the notes list is guarded by a single lock. ``decimal NightlyRate`` ->
# Decimal, DateTime -> datetime, DateTimeOffset -> datetime. AvailableOn returns
# clean, un-booked rooms for the date; CheckOut optionally flips a room to dirty.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class HotelRoom:
    """Mirrors ``CircleAI.Hospitality.HotelRoom`` — ``decimal NightlyRate``."""

    room_id: str
    type: str
    nightly_rate: Decimal
    currency: str
    is_clean: bool


@dataclass(frozen=True, slots=True)
class GuestReservation:
    """Mirrors ``CircleAI.Hospitality.GuestReservation``."""

    reservation_id: str
    guest_name: str
    room_id: str
    check_in: datetime
    check_out: datetime


@dataclass(frozen=True, slots=True)
class FrontDeskNote:
    """Mirrors ``CircleAI.Hospitality.FrontDeskNote``."""

    note_id: str
    reservation_id: str
    body: str
    at_utc: datetime


class IHospitalityBoard(ABC):
    """In-memory board for rooms, reservations and front-desk notes."""

    @abstractmethod
    def add_room(self, r: HotelRoom) -> None:
        ...

    @abstractmethod
    def get_room(self, id: str) -> Optional[HotelRoom]:
        ...

    @abstractmethod
    def available_on(self, date: datetime) -> List[HotelRoom]:
        ...

    @abstractmethod
    def reserve(self, r: GuestReservation) -> None:
        ...

    @abstractmethod
    def check_out(self, reservation_id: str, room_needs_cleaning: bool) -> None:
        ...

    @abstractmethod
    def get_reservation(self, id: str) -> Optional[GuestReservation]:
        ...

    @abstractmethod
    def add_note(self, n: FrontDeskNote) -> None:
        ...

    @abstractmethod
    def notes_for(self, reservation_id: str) -> List[FrontDeskNote]:
        ...


class InMemoryHospitalityBoard(IHospitalityBoard):
    """Thread-safe in-memory :class:`IHospitalityBoard`."""

    def __init__(self) -> None:
        self._rooms: Dict[str, HotelRoom] = {}
        self._res: Dict[str, GuestReservation] = {}
        self._notes: List[FrontDeskNote] = []
        self._lock = threading.Lock()

    def add_room(self, r: HotelRoom) -> None:
        if r is None:
            raise ValueError("hotel room must not be None")
        with self._lock:
            self._rooms[r.room_id] = r

    def get_room(self, id: str) -> Optional[HotelRoom]:
        with self._lock:
            return self._rooms.get(id)

    def available_on(self, date: datetime) -> List[HotelRoom]:
        with self._lock:
            booked = {
                r.room_id
                for r in self._res.values()
                if r.check_in <= date and r.check_out > date
            }
            return [
                r
                for r in self._rooms.values()
                if r.room_id not in booked and r.is_clean
            ]

    def reserve(self, r: GuestReservation) -> None:
        if r is None:
            raise ValueError("reservation must not be None")
        with self._lock:
            self._res[r.reservation_id] = r

    def check_out(self, reservation_id: str, room_needs_cleaning: bool) -> None:
        with self._lock:
            r = self._res.get(reservation_id)
            if r is None:
                raise RuntimeError(f"Unknown reservation {reservation_id}")
            if room_needs_cleaning:
                room = self._rooms.get(r.room_id)
                if room is not None:
                    self._rooms[r.room_id] = HotelRoom(
                        room.room_id,
                        room.type,
                        room.nightly_rate,
                        room.currency,
                        False,
                    )

    def get_reservation(self, id: str) -> Optional[GuestReservation]:
        with self._lock:
            return self._res.get(id)

    def add_note(self, n: FrontDeskNote) -> None:
        if n is None:
            raise ValueError("front desk note must not be None")
        with self._lock:
            self._notes.append(n)

    def notes_for(self, reservation_id: str) -> List[FrontDeskNote]:
        with self._lock:
            items = [n for n in self._notes if n.reservation_id == reservation_id]
        items.sort(key=lambda n: n.at_utc, reverse=True)
        return items
