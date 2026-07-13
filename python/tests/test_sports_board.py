"""test_sports_board.py — CircleAI.Sports port.

Covers DistanceKind, InMemorySportsBoard (log + history limit/order, weekly km,
personal best = fastest qualifying effort, schedule/complete/upcoming) and the
static SportsDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    DistanceKind,
    ISportsBoard,
    InMemorySportsBoard,
    PersonalBest,
    SportsActivity,
    SportsDomainContext,
    TrainingSession,
)

_T0 = datetime(2026, 1, 7, 12, 0, tzinfo=timezone.utc)  # a Wednesday


def _at(mins: int) -> datetime:
    return _T0 + timedelta(minutes=mins)


def _act(aid: str, kind: DistanceKind, km: float, dur_min: float, at: datetime):
    # SportsActivity == CircleAI.Sports.Activity(ActivityId, UserId, ...) — id first, user second.
    return SportsActivity(aid, "u", kind, km, timedelta(minutes=dur_min), at)


def test_board_is_isportsboard():
    assert isinstance(InMemorySportsBoard(), ISportsBoard)


def test_history_newest_first_and_limited():
    b = InMemorySportsBoard()
    b.log(_act("a1", DistanceKind.RUN, 5, 30, _at(0)))
    b.log(_act("a2", DistanceKind.RUN, 5, 28, _at(60)))
    b.log(_act("a3", DistanceKind.RUN, 5, 26, _at(30)))
    hist = b.history("u", limit=2)
    assert [h.activity_id for h in hist] == ["a2", "a3"]


def test_history_limit_zero_raises():
    with pytest.raises(ValueError):
        InMemorySportsBoard().history("u", limit=0)


def test_log_none_raises():
    with pytest.raises(ValueError):
        InMemorySportsBoard().log(None)  # type: ignore[arg-type]


def test_total_km_this_week_sums_from_sunday():
    b = InMemorySportsBoard()
    now = datetime(2026, 1, 7, 12, 0, tzinfo=timezone.utc)  # Wed
    # Sunday 2026-01-04 is the week start.
    b.log(_act("in1", DistanceKind.RUN, 5.0, 30, datetime(2026, 1, 4, 6, 0, tzinfo=timezone.utc)))
    b.log(_act("in2", DistanceKind.RUN, 3.0, 20, datetime(2026, 1, 6, 6, 0, tzinfo=timezone.utc)))
    b.log(_act("out", DistanceKind.RUN, 9.0, 40, datetime(2026, 1, 3, 6, 0, tzinfo=timezone.utc)))
    b.log(_act("bike", DistanceKind.BIKE, 20.0, 40, datetime(2026, 1, 6, 6, 0, tzinfo=timezone.utc)))
    assert b.total_km_this_week("u", DistanceKind.RUN, now) == pytest.approx(8.0)


def test_best_is_fastest_over_distance():
    b = InMemorySportsBoard()
    b.log(_act("slow", DistanceKind.RUN, 10.0, 60, _at(0)))
    b.log(_act("fast", DistanceKind.RUN, 10.0, 45, _at(10)))
    b.log(_act("short", DistanceKind.RUN, 4.0, 20, _at(20)))  # below distance
    pb = b.best("u", DistanceKind.RUN, 10.0)
    assert isinstance(pb, PersonalBest)
    assert pb.time == timedelta(minutes=45)


def test_best_none_when_no_qualifier():
    assert InMemorySportsBoard().best("u", DistanceKind.SWIM, 1.0) is None


def test_schedule_complete_and_upcoming():
    b = InMemorySportsBoard()
    future = datetime.now(timezone.utc) + timedelta(days=1)
    b.schedule(TrainingSession("s1", "u", "intervals", future, False))
    b.schedule(TrainingSession("s2", "u", "long run", future + timedelta(hours=1), False))
    assert {s.session_id for s in b.upcoming("u")} == {"s1", "s2"}
    b.complete("s1")
    assert {s.session_id for s in b.upcoming("u")} == {"s2"}


def test_complete_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemorySportsBoard().complete("nope")


def test_upcoming_excludes_past():
    b = InMemorySportsBoard()
    past = datetime.now(timezone.utc) - timedelta(days=1)
    b.schedule(TrainingSession("old", "u", "p", past, False))
    assert b.upcoming("u") == []


def test_sports_domain_context():
    assert SportsDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Sports]")
    assert list(SportsDomainContext.ComplianceFlags) == [
        "WADA",
        "SASCOC",
        "Sport_Recreation_SA",
        "POPIA",
    ]
    assert "performance_tracker" in SportsDomainContext.SuggestedTools
