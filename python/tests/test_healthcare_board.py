"""test_healthcare_board.py — CircleAI.Healthcare port.

Covers the domain records, InMemoryHealthcareBoard (patient upsert, appointment
scheduling + ascending-time ordering, status update, prescription descending-time
ordering) and the static HealthcareDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    HealthAppointment,
    HealthcareDomainContext,
    IHealthcareBoard,
    InMemoryHealthcareBoard,
    Patient,
    Prescription,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(mins: int) -> datetime:
    return _T0 + timedelta(minutes=mins)


def test_board_is_ihealthcareboard():
    assert isinstance(InMemoryHealthcareBoard(), IHealthcareBoard)


def test_records_are_frozen():
    p = Patient("p1", "Alice", datetime(1990, 5, 1))
    with pytest.raises(Exception):
        p.name = "x"  # type: ignore[misc]


def test_register_and_get_patient_upserts():
    board = InMemoryHealthcareBoard()
    assert board.get_patient("p1") is None
    board.register(Patient("p1", "Alice", datetime(1990, 5, 1)))
    board.register(Patient("p1", "Alicia", datetime(1990, 5, 1)))
    got = board.get_patient("p1")
    assert got is not None and got.name == "Alicia"


def test_register_none_raises():
    board = InMemoryHealthcareBoard()
    with pytest.raises(ValueError):
        board.register(None)  # type: ignore[arg-type]


def test_appointments_for_orders_ascending_by_time():
    board = InMemoryHealthcareBoard()
    board.schedule(HealthAppointment("a3", "p1", "Dr A", _at(30), "booked"))
    board.schedule(HealthAppointment("a1", "p1", "Dr A", _at(0), "booked"))
    board.schedule(HealthAppointment("a2", "p1", "Dr A", _at(15), "booked"))
    board.schedule(HealthAppointment("ax", "other", "Dr B", _at(5), "booked"))
    appts = board.appointments_for("p1")
    assert [a.appt_id for a in appts] == ["a1", "a2", "a3"]
    assert all(a.patient_id == "p1" for a in appts)


def test_update_status_replaces():
    board = InMemoryHealthcareBoard()
    board.schedule(HealthAppointment("a1", "p1", "Dr A", _at(0), "booked"))
    board.update_status("a1", "completed")
    assert board.appointments_for("p1")[0].status == "completed"


def test_update_status_unknown_raises():
    board = InMemoryHealthcareBoard()
    with pytest.raises(RuntimeError):
        board.update_status("nope", "x")


def test_schedule_none_raises():
    board = InMemoryHealthcareBoard()
    with pytest.raises(ValueError):
        board.schedule(None)  # type: ignore[arg-type]


def test_prescriptions_for_orders_descending_by_prescribed():
    board = InMemoryHealthcareBoard()
    board.prescribe(Prescription("r1", "p1", "Amox", "500mg", "TID", _at(0)))
    board.prescribe(Prescription("r2", "p1", "Ibu", "200mg", "BID", _at(60)))
    board.prescribe(Prescription("rx", "other", "X", "1", "OD", _at(30)))
    board.prescribe(Prescription("r3", "p1", "Para", "1g", "QID", _at(30)))
    rx = board.prescriptions_for("p1")
    assert [r.rx_id for r in rx] == ["r2", "r3", "r1"]  # 60, 30, 0
    assert all(r.patient_id == "p1" for r in rx)


def test_prescribe_none_raises():
    board = InMemoryHealthcareBoard()
    with pytest.raises(ValueError):
        board.prescribe(None)  # type: ignore[arg-type]


def test_healthcare_domain_context():
    assert HealthcareDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Healthcare]")
    assert "ICD-10" in HealthcareDomainContext.SystemPromptSnippet
    assert list(HealthcareDomainContext.ComplianceFlags) == [
        "HIPAA",
        "POPIA",
        "Health_Professions_Act_56_1974",
        "NHA_61_2003",
        "ICD10",
    ]
    assert list(HealthcareDomainContext.SuggestedTools) == [
        "ehr_system",
        "appointment_scheduler",
        "document_editor",
        "icd10_lookup",
    ]
