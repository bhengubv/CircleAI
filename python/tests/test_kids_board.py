"""test_kids_board.py — CircleAI.Kids port.

Covers AgeAppropriateness, InMemoryKidsBoard (content by band, daily limits,
used-today by date, over-limit for screen/reading vs unknown kind) and
KidsDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

from circle_ai import (
    AgeAppropriateness,
    DailyTime,
    IKidsBoard,
    InMemoryKidsBoard,
    KidsContent,
    KidsDomainContext,
    TimeLog,
)

_NOW = datetime(2026, 1, 15, 14, 0, tzinfo=timezone.utc)


def test_board_is_ikidsboard():
    assert isinstance(InMemoryKidsBoard(), IKidsBoard)


def test_content_for_band_ordered_by_title():
    b = InMemoryKidsBoard()
    b.add_content(KidsContent("c2", "Zebra", AgeAppropriateness.TODDLER, "book", []))
    b.add_content(KidsContent("c1", "Apple", AgeAppropriateness.TODDLER, "book", []))
    b.add_content(KidsContent("c3", "Older", AgeAppropriateness.TEEN, "game", []))
    got = b.content_for(AgeAppropriateness.TODDLER)
    assert [c.content_id for c in got] == ["c1", "c2"]


def test_used_today_sums_same_date():
    b = InMemoryKidsBoard()
    b.record_time(TimeLog("Kid", "screen", timedelta(minutes=30), _NOW))
    b.record_time(TimeLog("Kid", "screen", timedelta(minutes=20), _NOW.replace(hour=9)))
    b.record_time(TimeLog("Kid", "screen", timedelta(minutes=99), _NOW - timedelta(days=1)))
    assert b.used_today("Kid", "screen", _NOW) == timedelta(minutes=50)


def test_over_limit_screen_and_reading():
    b = InMemoryKidsBoard()
    b.set_limits(DailyTime("Kid", timedelta(minutes=60), timedelta(minutes=30)))
    b.record_time(TimeLog("Kid", "screen", timedelta(minutes=90), _NOW))
    b.record_time(TimeLog("Kid", "reading", timedelta(minutes=10), _NOW))
    assert b.over_limit("Kid", "screen", _NOW) is True
    assert b.over_limit("Kid", "reading", _NOW) is False


def test_over_limit_unknown_kind_never_over():
    b = InMemoryKidsBoard()
    b.set_limits(DailyTime("Kid", timedelta(minutes=60), timedelta(minutes=30)))
    b.record_time(TimeLog("Kid", "outdoor", timedelta(hours=5), _NOW))
    assert b.over_limit("Kid", "outdoor", _NOW) is False


def test_over_limit_no_limits_is_false():
    assert InMemoryKidsBoard().over_limit("Kid", "screen", _NOW) is False


def test_kids_domain_context():
    assert KidsDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Kids]")
    assert "COPPA_principles" in KidsDomainContext.ComplianceFlags
