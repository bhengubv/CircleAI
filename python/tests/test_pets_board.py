"""test_pets_board.py — CircleAI.Pets port.

Covers the domain records, InMemoryPetsBoard (pet upsert + name ordering,
vaccination recording newest-first, weight history oldest-first, appointment
scheduling with future-only upcoming ordering) and the static PetsDomainContext.
C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    IPetsBoard,
    InMemoryPetsBoard,
    Pet,
    PetsDomainContext,
    Vaccination,
    VetAppointment,
    WeightSample,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(days: int) -> datetime:
    return _T0 + timedelta(days=days)


def _now() -> datetime:
    return datetime.now(timezone.utc)


def test_board_is_ipetsboard():
    assert isinstance(InMemoryPetsBoard(), IPetsBoard)


def test_pets_ordered_by_name():
    board = InMemoryPetsBoard()
    board.add(Pet("p2", "Rex", "dog", "lab", datetime(2020, 1, 1)))
    board.add(Pet("p1", "Milo", "cat", None, datetime(2021, 1, 1)))
    assert [p.name for p in board.pets] == ["Milo", "Rex"]
    assert board.get_pet("p1").species == "cat"


def test_vaccinations_newest_first():
    board = InMemoryPetsBoard()
    board.record_vaccination(Vaccination("p1", "Rabies", _at(1), _at(365)))
    board.record_vaccination(Vaccination("p1", "Distemper", _at(3), None))
    board.record_vaccination(Vaccination("p1", "Parvo", _at(2), None))
    got = board.vaccinations_for("p1")
    assert [v.vaccine for v in got] == ["Distemper", "Parvo", "Rabies"]


def test_weight_history_oldest_first():
    board = InMemoryPetsBoard()
    board.record_weight(WeightSample("p1", 5.0, _at(2)))
    board.record_weight(WeightSample("p1", 4.0, _at(0)))
    board.record_weight(WeightSample("p1", 4.5, _at(1)))
    got = board.weight_history("p1")
    assert [w.weight_kg for w in got] == [4.0, 4.5, 5.0]


def test_upcoming_appointments_future_only_ordered():
    board = InMemoryPetsBoard()
    now = _now()
    board.schedule(VetAppointment("a1", "p1", "checkup", now + timedelta(days=5), "Dr A"))
    board.schedule(VetAppointment("a2", "p1", "shots", now + timedelta(days=1), "Dr B"))
    board.schedule(VetAppointment("a3", "p1", "past", now - timedelta(days=1), "Dr C"))
    got = board.upcoming_appointments()
    assert [a.appt_id for a in got] == ["a2", "a1"]  # earliest future first


def test_none_guards():
    board = InMemoryPetsBoard()
    with pytest.raises(ValueError):
        board.add(None)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        board.record_vaccination(None)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        board.record_weight(None)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        board.schedule(None)  # type: ignore[arg-type]


def test_pets_domain_context():
    assert PetsDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Pets]")
    assert list(PetsDomainContext.ComplianceFlags) == [
        "Animals_Protection_Act_71_1962",
        "POPIA",
        "Vet_Referral_Required",
    ]
    assert list(PetsDomainContext.SuggestedTools) == [
        "vet_finder",
        "pet_health_db",
        "training_tools",
        "calendar",
    ]
