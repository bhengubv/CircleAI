"""test_travel_board.py — CircleAI.Travel port.

Covers InMemoryTravelBoard (flights/stays, trip cost = flight prices +
nights*rate with min 1 night, upcoming trips, unknown-trip guard) and
TravelDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timezone
from decimal import Decimal

import pytest

from circle_ai import (
    Flight,
    HotelStay,
    ITravelBoard,
    InMemoryTravelBoard,
    TravelDomainContext,
    TravelTrip,
)


def _dt(y, m, d):
    return datetime(y, m, d, tzinfo=timezone.utc)


def test_board_is_itravelboard():
    assert isinstance(InMemoryTravelBoard(), ITravelBoard)


def test_trip_cost_flights_plus_nights():
    b = InMemoryTravelBoard()
    b.add_flight(Flight("f1", "CPT", "JNB", _dt(2026, 5, 1), _dt(2026, 5, 1), "X", "eco", Decimal("1500"), "ZAR"))
    b.add_stay(HotelStay("s1", "Hotel", "JNB", _dt(2026, 5, 1), _dt(2026, 5, 4), Decimal("1000"), "ZAR"))
    b.plan(TravelTrip("t1", "Work", _dt(2026, 5, 1), _dt(2026, 5, 4), ["f1"], ["s1"]))
    # 1500 + 3 nights * 1000 = 4500
    assert b.trip_cost("t1") == Decimal("4500")


def test_trip_cost_min_one_night():
    b = InMemoryTravelBoard()
    b.add_stay(HotelStay("s1", "H", "C", _dt(2026, 5, 1), _dt(2026, 5, 1), Decimal("800"), "ZAR"))
    b.plan(TravelTrip("t1", "Quick", _dt(2026, 5, 1), _dt(2026, 5, 1), [], ["s1"]))
    assert b.trip_cost("t1") == Decimal("800")  # max(1, 0 nights)


def test_trip_cost_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryTravelBoard().trip_cost("nope")


def test_upcoming_trips_ordered():
    b = InMemoryTravelBoard()
    b.plan(TravelTrip("t2", "B", _dt(2026, 6, 1), _dt(2026, 6, 2), [], []))
    b.plan(TravelTrip("t1", "A", _dt(2026, 5, 1), _dt(2026, 5, 2), [], []))
    b.plan(TravelTrip("old", "Old", _dt(2020, 1, 1), _dt(2020, 1, 2), [], []))
    got = b.upcoming_trips(_dt(2026, 1, 1))
    assert [t.trip_id for t in got] == ["t1", "t2"]


def test_travel_domain_context():
    assert TravelDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Travel]")
    assert "Consumer_Protection_Act" in TravelDomainContext.ComplianceFlags
