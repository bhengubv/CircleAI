"""test_tourism_board.py — CircleAI.Tourism port.

Covers InMemoryTourismBoard (attractions by city/tag ordered by name, blank
guards, itineraries, bookings snapshot) and TourismDomainContext. C# is the
exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone
from decimal import Decimal

import pytest

from circle_ai import (
    Attraction,
    ITourismBoard,
    InMemoryTourismBoard,
    Itinerary,
    ItineraryItem,
    TourismBooking,
    TourismDomainContext,
)


def _attr(aid: str, name: str, city: str, tags):
    return Attraction(aid, name, city, "ZA", -33.9, 18.4, tags)


def test_board_is_itourismboard():
    assert isinstance(InMemoryTourismBoard(), ITourismBoard)


def test_attractions_in_city_case_insensitive_ordered():
    b = InMemoryTourismBoard()
    b.add(_attr("a2", "Zoo", "Cape Town", ["family"]))
    b.add(_attr("a1", "Aquarium", "cape town", ["family"]))
    b.add(_attr("o", "Museum", "Durban", ["history"]))
    got = b.attractions_in_city("Cape Town")
    assert [a.attraction_id for a in got] == ["a1", "a2"]  # by name


def test_by_tag_case_insensitive():
    b = InMemoryTourismBoard()
    b.add(_attr("a1", "Beach", "CT", ["Nature"]))
    b.add(_attr("a2", "Trail", "CT", ["nature"]))
    b.add(_attr("a3", "Mall", "CT", ["shopping"]))
    assert {a.attraction_id for a in b.by_tag("NATURE")} == {"a1", "a2"}


def test_blank_city_and_tag_raise():
    b = InMemoryTourismBoard()
    with pytest.raises(ValueError):
        b.attractions_in_city("  ")
    with pytest.raises(ValueError):
        b.by_tag("")


def test_itinerary_and_bookings_snapshot():
    b = InMemoryTourismBoard()
    it = Itinerary("i1", "Trip", [ItineraryItem(0, timedelta(hours=9), timedelta(hours=11), "a1", None)])
    b.plan(it)
    assert b.get_itinerary("i1").title == "Trip"
    b.book(TourismBooking("bk1", "i1", datetime(2026, 5, 1, tzinfo=timezone.utc), 2, Decimal("5000"), "ZAR"))
    snap = b.bookings
    assert [bk.booking_id for bk in snap] == ["bk1"]
    # snapshot is a copy — mutating it doesn't affect the board
    snap.clear()
    assert len(b.bookings) == 1


def test_tourism_domain_context():
    assert TourismDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Tourism]")
    assert "SATSA" in TourismDomainContext.ComplianceFlags
