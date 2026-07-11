"""test_elderly_board.py — CircleAI.Elderly port.

Covers the domain records, InMemoryElderlyCareBoard (care-plan set/get keyed by
resident, reminder add/deactivate + active filter, check-in recording +
latest-by-time, missed-check-in logic) and the static ElderlyDomainContext. C#
is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    CarePlan,
    ElderlyCheckIn,
    ElderlyDomainContext,
    IElderlyCareBoard,
    InMemoryElderlyCareBoard,
    MedReminder,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(days: int) -> datetime:
    return _T0 + timedelta(days=days)


def test_board_is_ielderlycareboard():
    assert isinstance(InMemoryElderlyCareBoard(), IElderlyCareBoard)


def test_set_get_plan_keyed_by_resident():
    board = InMemoryElderlyCareBoard()
    board.set_plan(CarePlan("pl1", "Gogo", ["diabetes"], ["penicillin"], "gentle"))
    board.set_plan(CarePlan("pl2", "Gogo", ["diabetes", "hypertension"], [], "firm"))
    got = board.get_plan("Gogo")
    assert got is not None and got.plan_id == "pl2" and list(got.medical_conditions) == [
        "diabetes",
        "hypertension",
    ]
    assert board.get_plan("Nobody") is None


def test_reminders_add_deactivate_and_active_filter():
    board = InMemoryElderlyCareBoard()
    board.add_reminder(MedReminder("r1", "Gogo", "Metformin", timedelta(hours=8), True))
    board.add_reminder(MedReminder("r2", "Gogo", "Aspirin", timedelta(hours=20), True))
    board.add_reminder(MedReminder("r3", "Mkhulu", "Statin", timedelta(hours=21), True))
    board.deactivate_reminder("r2")
    active = {r.reminder_id for r in board.active_reminders_for("Gogo")}
    assert active == {"r1"}


def test_deactivate_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryElderlyCareBoard().deactivate_reminder("nope")


def test_latest_check_in_and_missed():
    board = InMemoryElderlyCareBoard()
    board.record_check_in(ElderlyCheckIn("c1", "Gogo", _at(1), "ok", None))
    board.record_check_in(ElderlyCheckIn("c2", "Gogo", _at(3), "ok", "all good"))
    board.record_check_in(ElderlyCheckIn("c3", "Gogo", _at(2), "ok", None))
    latest = board.latest_check_in("Gogo")
    assert latest is not None and latest.check_in_id == "c2"
    # latest at day3: not missed relative to day2, missed relative to day4
    assert board.missed_check_in("Gogo", _at(2)) is False
    assert board.missed_check_in("Gogo", _at(4)) is True


def test_missed_check_in_no_history_true():
    board = InMemoryElderlyCareBoard()
    assert board.latest_check_in("Nobody") is None
    assert board.missed_check_in("Nobody", _T0) is True


def test_none_guards():
    board = InMemoryElderlyCareBoard()
    with pytest.raises(ValueError):
        board.set_plan(None)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        board.add_reminder(None)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        board.record_check_in(None)  # type: ignore[arg-type]


def test_elderly_domain_context():
    assert ElderlyDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Elderly]")
    assert list(ElderlyDomainContext.ComplianceFlags) == [
        "Older_Persons_Act_13_2006",
        "Social_Assistance_Act",
        "POPIA",
    ]
    assert list(ElderlyDomainContext.SuggestedTools) == [
        "medication_reminder",
        "calendar",
        "web_search",
        "document_editor",
    ]
