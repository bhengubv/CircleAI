"""test_hospitality_board.py — CircleAI.Hospitality port.

Covers InMemoryHospitalityBoard (rooms, availability excluding booked + dirty,
checkout housekeeping flip, notes newest-first) and HospitalityDomainContext.
C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timezone
from decimal import Decimal

import pytest

from circle_ai import (
    FrontDeskNote,
    GuestReservation,
    HospitalityDomainContext,
    HotelRoom,
    IHospitalityBoard,
    InMemoryHospitalityBoard,
)


def _d(day: int) -> datetime:
    return datetime(2026, 3, day, tzinfo=timezone.utc)


def test_board_is_ihospitalityboard():
    assert isinstance(InMemoryHospitalityBoard(), IHospitalityBoard)


def test_available_excludes_booked_and_dirty():
    b = InMemoryHospitalityBoard()
    b.add_room(HotelRoom("r1", "std", Decimal("900"), "ZAR", True))
    b.add_room(HotelRoom("r2", "std", Decimal("900"), "ZAR", True))
    b.add_room(HotelRoom("r3", "std", Decimal("900"), "ZAR", False))  # dirty
    b.reserve(GuestReservation("res1", "Kim", "r1", _d(5), _d(8)))
    avail = b.available_on(_d(6))  # r1 booked, r3 dirty -> only r2
    assert {r.room_id for r in avail} == {"r2"}


def test_checkout_marks_room_dirty_when_needed():
    b = InMemoryHospitalityBoard()
    b.add_room(HotelRoom("r1", "std", Decimal("900"), "ZAR", True))
    b.reserve(GuestReservation("res1", "Kim", "r1", _d(5), _d(8)))
    b.check_out("res1", room_needs_cleaning=True)
    assert b.get_room("r1").is_clean is False


def test_checkout_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryHospitalityBoard().check_out("nope", True)


def test_notes_newest_first():
    b = InMemoryHospitalityBoard()
    b.add_note(FrontDeskNote("n1", "res1", "early", datetime(2026, 3, 1, tzinfo=timezone.utc)))
    b.add_note(FrontDeskNote("n2", "res1", "late", datetime(2026, 3, 2, tzinfo=timezone.utc)))
    got = b.notes_for("res1")
    assert [n.note_id for n in got] == ["n2", "n1"]


def test_hospitality_domain_context():
    assert HospitalityDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Hospitality]")
    assert "Liquor_Act" in HospitalityDomainContext.ComplianceFlags
