"""test_faith_board.py — CircleAI.Faith port.

Covers InMemoryFaithBoard (services in range, recent prayers newest-first,
scripture exact lookup + by-tradition case-insensitive) and FaithDomainContext.
C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

from circle_ai import (
    FaithDomainContext,
    FaithService,
    IFaithBoard,
    InMemoryFaithBoard,
    PrayerRequest,
    ScriptureReference,
)


def _at(mins: int) -> datetime:
    return datetime(2026, 1, 1, tzinfo=timezone.utc) + timedelta(minutes=mins)


def test_board_is_ifaithboard():
    assert isinstance(InMemoryFaithBoard(), IFaithBoard)


def test_services_between_ordered():
    b = InMemoryFaithBoard()
    b.schedule(FaithService("s2", "Grace", "Evening", _at(120), "Hall"))
    b.schedule(FaithService("s1", "Grace", "Morning", _at(30), "Hall"))
    b.schedule(FaithService("out", "Grace", "Late", _at(9999), "Hall"))
    got = b.services_between(_at(0), _at(200))
    assert [s.service_id for s in got] == ["s1", "s2"]


def test_recent_prayers_newest_first_limited():
    b = InMemoryFaithBoard()
    b.submit_prayer(PrayerRequest("p1", "a", "one", _at(0), False))
    b.submit_prayer(PrayerRequest("p2", "b", "two", _at(10), True))
    got = b.recent_prayers(limit=1)
    assert [p.request_id for p in got] == ["p2"]


def test_lookup_exact_and_by_tradition():
    b = InMemoryFaithBoard()
    b.add_scripture(ScriptureReference("ref1", "Christian", "John", 3, 16, "For God..."))
    b.add_scripture(ScriptureReference("ref2", "Christian", "Psalms", 23, 1, "The Lord..."))
    assert b.lookup("Christian", "John", 3, 16).reference_id == "ref1"
    assert b.lookup("Christian", "John", 3, 17) is None
    assert {r.reference_id for r in b.by_tradition("christian")} == {"ref1", "ref2"}


def test_faith_domain_context():
    assert FaithDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Faith]")
    assert "Non_Denominational_Respect" in FaithDomainContext.ComplianceFlags
