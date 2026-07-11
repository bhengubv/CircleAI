"""test_family_board.py — CircleAI.Family port.

Covers the domain records, InMemoryFamilyBoard (member upsert + name ordering,
event scheduling + membership-filtered time-ordered lookup, expense recording,
total-paid-by since, case-insensitive spend-by-category since) and the static
FamilyDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone
from decimal import Decimal

import pytest

from circle_ai import (
    FamilyDomainContext,
    FamilyEvent,
    FamilyMember,
    IFamilyBoard,
    InMemoryFamilyBoard,
    SharedExpense,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(days: int) -> datetime:
    return _T0 + timedelta(days=days)


def test_board_is_ifamilyboard():
    assert isinstance(InMemoryFamilyBoard(), IFamilyBoard)


def test_members_ordered_by_name():
    board = InMemoryFamilyBoard()
    board.add(FamilyMember("m2", "Bob", "parent", _T0))
    board.add(FamilyMember("m1", "Ann", "parent", _T0))
    assert [m.name for m in board.members] == ["Ann", "Bob"]
    assert board.get_member("m1").role == "parent"


def test_events_for_member_filtered_and_time_ordered():
    board = InMemoryFamilyBoard()
    board.schedule(FamilyEvent("e1", "Dentist", _at(3), ["m1"]))
    board.schedule(FamilyEvent("e2", "Braai", _at(1), ["m1", "m2"]))
    board.schedule(FamilyEvent("e3", "Soccer", _at(2), ["m2"]))
    got = board.events_for_member("m1")
    assert [e.event_id for e in got] == ["e2", "e1"]  # AtUtc ascending


def test_total_paid_by_since():
    board = InMemoryFamilyBoard()
    board.record(SharedExpense("x1", "m1", Decimal("100"), "ZAR", "food", _at(1)))
    board.record(SharedExpense("x2", "m1", Decimal("50"), "ZAR", "fuel", _at(5)))
    board.record(SharedExpense("x3", "m2", Decimal("999"), "ZAR", "food", _at(5)))
    board.record(SharedExpense("x4", "m1", Decimal("7"), "ZAR", "food", _at(0)))
    # since day 1 inclusive -> x1 + x2 (x4 at day0 excluded, x3 is m2)
    assert board.total_paid_by("m1", _at(1)) == Decimal("150")


def test_spend_by_category_case_insensitive_since():
    board = InMemoryFamilyBoard()
    board.record(SharedExpense("x1", "m1", Decimal("100"), "ZAR", "Food", _at(1)))
    board.record(SharedExpense("x2", "m2", Decimal("40"), "ZAR", "food", _at(2)))
    board.record(SharedExpense("x3", "m1", Decimal("999"), "ZAR", "food", _at(0)))
    assert board.spend_by_category("FOOD", _at(1)) == Decimal("140")


def test_none_guards():
    board = InMemoryFamilyBoard()
    with pytest.raises(ValueError):
        board.add(None)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        board.schedule(None)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        board.record(None)  # type: ignore[arg-type]


def test_empty_sums_are_zero():
    board = InMemoryFamilyBoard()
    assert board.total_paid_by("nobody", _T0) == Decimal(0)
    assert board.spend_by_category("nothing", _T0) == Decimal(0)


def test_family_domain_context():
    assert FamilyDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Family]")
    assert list(FamilyDomainContext.ComplianceFlags) == ["POPIA", "Childrens_Act_38_2005"]
    assert list(FamilyDomainContext.SuggestedTools) == [
        "shared_calendar",
        "family_budget",
        "document_editor",
        "task_manager",
    ]
