"""test_child_safety_board.py — CircleAI.Safety.Child port.

Covers the domain records, InMemoryChildSafetyBoard (trusted-adult ring ordering,
geofence upsert + Haversine containment, check-in recording/limit) and the static
SafetyChildDomainContext. C# is the exact spec.
"""
from __future__ import annotations

import math
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    CheckIn,
    Geofence,
    InMemoryChildSafetyBoard,
    SafetyChildDomainContext,
    TrustedAdult,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(mins: int) -> datetime:
    return _T0 + timedelta(minutes=mins)


def _haversine(a_lat, a_lon, b_lat, b_lon):
    R = 6_371_000.0
    d_lat = math.radians(b_lat - a_lat)
    d_lon = math.radians(b_lon - a_lon)
    s1 = math.sin(d_lat / 2)
    s2 = math.sin(d_lon / 2)
    a = s1 * s1 + math.cos(math.radians(a_lat)) * math.cos(math.radians(b_lat)) * s2 * s2
    return R * 2 * math.atan2(math.sqrt(a), math.sqrt(1 - a))


def test_records_are_frozen():
    a = TrustedAdult("a1", "Alice", "111", "mother", 1)
    with pytest.raises(Exception):
        a.name = "x"  # type: ignore[misc]


def test_ring_ordered_by_priority():
    board = InMemoryChildSafetyBoard()
    board.add_adult(TrustedAdult("a", "A", "1", "r", 3))
    board.add_adult(TrustedAdult("b", "B", "2", "r", 1))
    board.add_adult(TrustedAdult("c", "C", "3", "r", 2))
    assert [a.adult_id for a in board.ring_ordered] == ["b", "c", "a"]


def test_add_adult_upserts_by_id():
    board = InMemoryChildSafetyBoard()
    board.add_adult(TrustedAdult("a", "Old", "1", "r", 5))
    board.add_adult(TrustedAdult("a", "New", "1", "r", 1))
    ring = board.ring_ordered
    assert len(ring) == 1
    assert ring[0].name == "New"
    assert ring[0].ring_priority == 1


def test_add_adult_none_raises():
    board = InMemoryChildSafetyBoard()
    with pytest.raises(ValueError):
        board.add_adult(None)  # type: ignore[arg-type]


def test_geofence_define_get_and_upsert():
    board = InMemoryChildSafetyBoard()
    assert board.get_geofence("home") is None
    board.define_geofence(Geofence("home", "Home", -26.2, 28.0, 100.0))
    got = board.get_geofence("home")
    assert got is not None and got.name == "Home"
    # Upsert by id.
    board.define_geofence(Geofence("home", "Home2", -26.2, 28.0, 250.0))
    assert board.get_geofence("home").radius_meters == pytest.approx(250.0)


def test_define_geofence_none_raises():
    board = InMemoryChildSafetyBoard()
    with pytest.raises(ValueError):
        board.define_geofence(None)  # type: ignore[arg-type]


def test_is_inside_any_fence_centre():
    board = InMemoryChildSafetyBoard()
    board.define_geofence(Geofence("f", "F", -26.2041, 28.0473, 50.0))
    assert board.is_inside_any_fence(-26.2041, 28.0473) is True  # exact centre, dist 0


def test_is_inside_any_fence_boundary():
    board = InMemoryChildSafetyBoard()
    centre_lat, centre_lon = -26.2041, 28.0473
    board.define_geofence(Geofence("f", "F", centre_lat, centre_lon, 200.0))
    # A point ~150 m north (0.00135 deg lat ~= 150 m) is inside a 200 m fence.
    near_lat = centre_lat + 0.00135
    assert _haversine(centre_lat, centre_lon, near_lat, centre_lon) < 200.0
    assert board.is_inside_any_fence(near_lat, centre_lon) is True
    # A point ~2 km away is outside.
    far_lat = centre_lat + 0.02
    assert _haversine(centre_lat, centre_lon, far_lat, centre_lon) > 200.0
    assert board.is_inside_any_fence(far_lat, centre_lon) is False


def test_is_inside_any_fence_no_fences():
    board = InMemoryChildSafetyBoard()
    assert board.is_inside_any_fence(0.0, 0.0) is False


def test_record_check_in_none_raises():
    board = InMemoryChildSafetyBoard()
    with pytest.raises(ValueError):
        board.record_check_in(None)  # type: ignore[arg-type]


def test_recent_check_ins_filters_by_child_and_orders_desc():
    board = InMemoryChildSafetyBoard()
    board.record_check_in(CheckIn("kid", "home", None, None, _at(0)))
    board.record_check_in(CheckIn("kid", "school", None, None, _at(10)))
    board.record_check_in(CheckIn("other", "park", None, None, _at(5)))
    board.record_check_in(CheckIn("kid", "park", None, None, _at(5)))
    recent = board.recent_check_ins("kid")
    assert [c.status for c in recent] == ["school", "park", "home"]  # 10, 5, 0
    assert all(c.child_id == "kid" for c in recent)


def test_recent_check_ins_limit():
    board = InMemoryChildSafetyBoard()
    for m in range(5):
        board.record_check_in(CheckIn("kid", f"s{m}", None, None, _at(m)))
    recent = board.recent_check_ins("kid", limit=2)
    assert len(recent) == 2
    assert [c.status for c in recent] == ["s4", "s3"]  # newest first


def test_recent_check_ins_bad_limit_raises():
    board = InMemoryChildSafetyBoard()
    with pytest.raises(ValueError):
        board.recent_check_ins("kid", limit=0)
    with pytest.raises(ValueError):
        board.recent_check_ins("kid", limit=-3)


def test_recent_check_ins_default_limit_is_20():
    board = InMemoryChildSafetyBoard()
    for m in range(25):
        board.record_check_in(CheckIn("kid", f"s{m}", None, None, _at(m)))
    assert len(board.recent_check_ins("kid")) == 20


# ── SafetyChildDomainContext ──────────────────────────────────────────────────

def test_child_domain_context():
    assert SafetyChildDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Safety.Child]")
    assert "116" in SafetyChildDomainContext.SystemPromptSnippet
    assert list(SafetyChildDomainContext.ComplianceFlags) == [
        "Childrens_Act_38_2005",
        "POPIA_Children",
        "Films_Publications_Act",
        "Cybercrimes_Act",
        "Emergency_116",
    ]
    assert list(SafetyChildDomainContext.SuggestedTools) == [
        "parental_controls",
        "web_search",
        "document_editor",
        "reporting_tools",
    ]
