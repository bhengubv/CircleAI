"""test_real_estate_board.py — CircleAI.RealEstate port.

Covers the PropertyKind enum, domain records, InMemoryRealEstateBoard (property
register, list/close, case-insensitive newest-first active-in-suburb, suburb
average incl. None on empty, blank-suburb guard) and the static
RealEstateDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone
from decimal import Decimal

import pytest

from circle_ai import (
    IRealEstateBoard,
    InMemoryRealEstateBoard,
    Listing,
    Property,
    PropertyKind,
    RealEstateDomainContext,
    Valuation,
    Viewing,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(days: int) -> datetime:
    return _T0 + timedelta(days=days)


def test_property_kind_ordinals():
    assert (
        PropertyKind.Apartment,
        PropertyKind.House,
        PropertyKind.Townhouse,
        PropertyKind.Commercial,
        PropertyKind.Land,
    ) == (0, 1, 2, 3, 4)


def test_board_is_irealestateboard():
    assert isinstance(InMemoryRealEstateBoard(), IRealEstateBoard)


def _seed(board: InMemoryRealEstateBoard) -> None:
    board.register_property(Property("p1", "Sandton", PropertyKind.House, 3, 2, 200.0))
    board.register_property(Property("p2", "Sandton", PropertyKind.Apartment, 2, 1, 90.0))
    board.register_property(Property("p3", "Rosebank", PropertyKind.Apartment, 1, 1, 50.0))


def test_active_in_suburb_newest_first_case_insensitive():
    board = InMemoryRealEstateBoard()
    _seed(board)
    board.list(Listing("l1", "p1", Decimal("2000000"), "ZAR", _at(1), True))
    board.list(Listing("l2", "p2", Decimal("1000000"), "ZAR", _at(3), True))
    board.list(Listing("l3", "p3", Decimal("800000"), "ZAR", _at(2), True))
    got = board.active_in_suburb("sandton")  # case-insensitive
    assert [l.listing_id for l in got] == ["l2", "l1"]  # newest ListedUtc first


def test_close_deactivates_listing():
    board = InMemoryRealEstateBoard()
    _seed(board)
    board.list(Listing("l1", "p1", Decimal("2000000"), "ZAR", _at(1), True))
    board.close("l1")
    assert board.active_in_suburb("Sandton") == []


def test_close_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryRealEstateBoard().close("nope")


def test_suburb_average_and_empty_none():
    board = InMemoryRealEstateBoard()
    _seed(board)
    board.list(Listing("l1", "p1", Decimal("2000000"), "ZAR", _at(1), True))
    board.list(Listing("l2", "p2", Decimal("1000000"), "ZAR", _at(3), True))
    assert board.suburb_average("Sandton") == Decimal("1500000")
    assert board.suburb_average("Nowhere") is None


def test_active_in_suburb_blank_raises():
    with pytest.raises(ValueError):
        InMemoryRealEstateBoard().active_in_suburb("  ")


def test_value_and_schedule_viewing_none_guards():
    board = InMemoryRealEstateBoard()
    with pytest.raises(ValueError):
        board.value(None)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        board.schedule_viewing(None)  # type: ignore[arg-type]
    # smoke: recording valid ones does not raise
    board.value(Valuation("p1", Decimal("123"), "AVM", _at(0)))
    board.schedule_viewing(Viewing("v1", "l1", "Ann", _at(0)))


def test_real_estate_domain_context():
    assert RealEstateDomainContext.SystemPromptSnippet.startswith("[DOMAIN: RealEstate]")
    assert list(RealEstateDomainContext.ComplianceFlags) == [
        "Alienation_of_Land_Act",
        "Rental_Housing_Act",
        "PPRA",
        "FICA",
        "POPIA",
    ]
    assert list(RealEstateDomainContext.SuggestedTools) == [
        "property_listings",
        "document_editor",
        "map",
        "analytics",
    ]
