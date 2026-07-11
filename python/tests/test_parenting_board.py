"""test_parenting_board.py — CircleAI.Parenting port.

Covers the DayOfWeek enum (System.DayOfWeek ordinals), domain records,
InMemoryParentingBoard (child upsert + name ordering, milestone recording with
newest-first + empty-for-unknown + blank-child guard, routine set/get keyed by
day, age-as-of + unknown-child guard) and the static ParentingDomainContext. C#
is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    Child,
    DayOfWeek,
    IParentingBoard,
    InMemoryParentingBoard,
    Milestone,
    ParentingDomainContext,
    Routine,
    RoutineEntry,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(days: int) -> datetime:
    return _T0 + timedelta(days=days)


def test_day_of_week_ordinals():
    assert DayOfWeek.Sunday == 0
    assert DayOfWeek.Monday == 1
    assert DayOfWeek.Saturday == 6


def test_board_is_iparentingboard():
    assert isinstance(InMemoryParentingBoard(), IParentingBoard)


def test_children_ordered_by_name():
    board = InMemoryParentingBoard()
    board.add_child(Child("c2", "Bea", datetime(2018, 5, 1), "F"))
    board.add_child(Child("c1", "Amy", datetime(2020, 3, 1), None))
    assert [c.name for c in board.children] == ["Amy", "Bea"]
    assert board.get_child("c1").gender is None


def test_milestones_newest_first_and_empty_for_unknown():
    board = InMemoryParentingBoard()
    board.record_milestone(Milestone("m1", "c1", "motor", "crawl", _at(1)))
    board.record_milestone(Milestone("m2", "c1", "speech", "first word", _at(3)))
    board.record_milestone(Milestone("m3", "c1", "motor", "walk", _at(2)))
    got = board.milestones_for("c1")
    assert [m.milestone_id for m in got] == ["m2", "m3", "m1"]
    assert board.milestones_for("nobody") == []


def test_record_milestone_blank_child_raises():
    board = InMemoryParentingBoard()
    with pytest.raises(ValueError):
        board.record_milestone(Milestone("m1", "  ", "motor", "x", _at(0)))


def test_routine_set_get_keyed_by_day():
    board = InMemoryParentingBoard()
    entries = [RoutineEntry("07:00", "wake"), RoutineEntry("08:00", "school")]
    board.set_routine(Routine("c1", DayOfWeek.Monday, entries))
    got = board.get_routine("c1", DayOfWeek.Monday)
    assert got is not None and list(got.entries) == entries
    assert board.get_routine("c1", DayOfWeek.Tuesday) is None


def test_age_as_of_and_unknown_raises():
    board = InMemoryParentingBoard()
    board.add_child(Child("c1", "Amy", datetime(2020, 1, 1, tzinfo=timezone.utc), None))
    age = board.age_as_of("c1", datetime(2021, 1, 1, tzinfo=timezone.utc))
    assert age == timedelta(days=366)  # 2020 is a leap year
    with pytest.raises(RuntimeError):
        board.age_as_of("nobody", datetime(2021, 1, 1, tzinfo=timezone.utc))


def test_add_child_none_raises():
    with pytest.raises(ValueError):
        InMemoryParentingBoard().add_child(None)  # type: ignore[arg-type]


def test_parenting_domain_context():
    assert ParentingDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Parenting]")
    assert list(ParentingDomainContext.ComplianceFlags) == ["Childrens_Act_38_2005", "POPIA"]
    assert list(ParentingDomainContext.SuggestedTools) == [
        "development_tracker",
        "document_editor",
        "web_search",
        "calendar",
    ]
