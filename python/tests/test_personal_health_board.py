"""test_personal_health_board.py — CircleAI.Personal.Health port.

Covers VitalKind ordinals, the domain records, InMemoryPersonalHealthBoard
(vital recording, read-since ascending, latest, allergy upsert, medication
add/end, active-medication filtering ordered by Name) and the static
PersonalHealthDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    Allergy,
    InMemoryPersonalHealthBoard,
    IPersonalHealthBoard,
    Medication,
    PersonalHealthDomainContext,
    VitalKind,
    VitalReading,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(mins: int) -> datetime:
    return _T0 + timedelta(minutes=mins)


def test_vital_kind_ordinals_stable():
    assert [int(v) for v in VitalKind] == [0, 1, 2, 3, 4, 5, 6, 7]
    assert VitalKind.BLOOD_PRESSURE_SYSTOLIC == 0
    assert VitalKind.STEPS_COUNT == 7


def test_board_is_ipersonalhealthboard():
    assert isinstance(InMemoryPersonalHealthBoard(), IPersonalHealthBoard)


def test_record_none_raises():
    with pytest.raises(ValueError):
        InMemoryPersonalHealthBoard().record(None)  # type: ignore[arg-type]


def test_read_since_filters_by_kind_and_time_ascending():
    board = InMemoryPersonalHealthBoard()
    board.record(VitalReading(VitalKind.WEIGHT_KG, 80.0, _at(0), None))
    board.record(VitalReading(VitalKind.WEIGHT_KG, 79.0, _at(20), None))
    board.record(VitalReading(VitalKind.WEIGHT_KG, 78.0, _at(10), None))
    board.record(VitalReading(VitalKind.GLUCOSE_MG_DL, 5.0, _at(15), None))
    rows = board.read_since(VitalKind.WEIGHT_KG, _at(5))
    assert [r.value for r in rows] == [78.0, 79.0]  # >= _at(5), ascending by time
    assert all(r.kind == VitalKind.WEIGHT_KG for r in rows)


def test_latest_returns_most_recent_of_kind():
    board = InMemoryPersonalHealthBoard()
    assert board.latest(VitalKind.HEART_RATE_BPM) is None
    board.record(VitalReading(VitalKind.HEART_RATE_BPM, 60.0, _at(0), None))
    board.record(VitalReading(VitalKind.HEART_RATE_BPM, 72.0, _at(30), None))
    board.record(VitalReading(VitalKind.HEART_RATE_BPM, 65.0, _at(10), None))
    latest = board.latest(VitalKind.HEART_RATE_BPM)
    assert latest is not None and latest.value == 72.0


def test_add_allergy_upserts_and_lists():
    board = InMemoryPersonalHealthBoard()
    board.add_allergy(Allergy("al1", "Penicillin", "severe"))
    board.add_allergy(Allergy("al1", "Penicillin", "moderate"))  # upsert
    board.add_allergy(Allergy("al2", "Peanuts", "severe"))
    by_id = {a.allergy_id: a for a in board.allergies}
    assert set(by_id) == {"al1", "al2"}
    assert by_id["al1"].severity == "moderate"


def test_add_allergy_none_raises():
    with pytest.raises(ValueError):
        InMemoryPersonalHealthBoard().add_allergy(None)  # type: ignore[arg-type]


def test_active_medications_excludes_ended_ordered_by_name():
    board = InMemoryPersonalHealthBoard()
    board.add_medication(Medication("m1", "Zinc", "10mg", "OD", _at(0), None))
    board.add_medication(Medication("m2", "Aspirin", "100mg", "OD", _at(0), None))
    board.add_medication(Medication("m3", "Biotin", "5mg", "OD", _at(0), None))
    board.end_medication("m3", _at(100))
    active = board.active_medications()
    assert [m.name for m in active] == ["Aspirin", "Zinc"]  # Biotin ended; ordered by Name
    assert board.end_medication  # sanity


def test_end_medication_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryPersonalHealthBoard().end_medication("nope", _at(0))


def test_add_medication_none_raises():
    with pytest.raises(ValueError):
        InMemoryPersonalHealthBoard().add_medication(None)  # type: ignore[arg-type]


def test_personal_health_domain_context():
    ctx = PersonalHealthDomainContext
    assert ctx.SystemPromptSnippet.startswith("[DOMAIN: Personal.Health]")
    assert "not medical advice" in ctx.SystemPromptSnippet
    assert list(ctx.ComplianceFlags) == ["POPIA", "Health_Professions_Act", "Not_Medical_Advice"]
    assert list(ctx.SuggestedTools) == [
        "health_tracker",
        "symptom_checker_ref",
        "calendar",
        "document_editor",
    ]
