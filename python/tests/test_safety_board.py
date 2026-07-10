"""test_safety_board.py — CircleAI.Safety port (personal-safety domain pack).

Covers IncidentSeverity ordinals, the domain records, InMemorySafetyBoard
(incident logging, descending-time ordering, severity routing, hazard upsert,
emergency contacts) and the static SafetyDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    EmergencyContact,
    Hazard,
    Incident,
    IncidentSeverity,
    InMemorySafetyBoard,
    SafetyDomainContext,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(mins: int) -> datetime:
    return _T0 + timedelta(minutes=mins)


def test_incident_severity_ordinals_stable():
    assert [(e.name, int(e)) for e in IncidentSeverity] == [
        ("INFO", 0),
        ("WARNING", 1),
        ("CRITICAL", 2),
        ("EMERGENCY", 3),
    ]


def test_records_are_frozen():
    inc = Incident("i1", IncidentSeverity.INFO, "d", None, None, _at(0))
    with pytest.raises(Exception):
        inc.description = "x"  # type: ignore[misc]


def test_log_none_raises():
    board = InMemorySafetyBoard()
    with pytest.raises(ValueError):
        board.log(None)  # type: ignore[arg-type]


def test_active_orders_by_time_descending():
    board = InMemorySafetyBoard()
    board.log(Incident("a", IncidentSeverity.INFO, "d", None, None, _at(0)))
    board.log(Incident("b", IncidentSeverity.INFO, "d", None, None, _at(10)))
    board.log(Incident("c", IncidentSeverity.INFO, "d", None, None, _at(5)))
    assert [i.incident_id for i in board.active] == ["b", "c", "a"]


def test_at_or_above_severity_filters_and_orders():
    board = InMemorySafetyBoard()
    board.log(Incident("info", IncidentSeverity.INFO, "d", None, None, _at(0)))
    board.log(Incident("warn", IncidentSeverity.WARNING, "d", None, None, _at(1)))
    board.log(Incident("crit", IncidentSeverity.CRITICAL, "d", None, None, _at(2)))
    board.log(Incident("emer", IncidentSeverity.EMERGENCY, "d", None, None, _at(3)))

    at_warn = board.at_or_above_severity(IncidentSeverity.WARNING)
    assert [i.incident_id for i in at_warn] == ["emer", "crit", "warn"]

    at_crit = board.at_or_above_severity(IncidentSeverity.CRITICAL)
    assert [i.incident_id for i in at_crit] == ["emer", "crit"]

    at_info = board.at_or_above_severity(IncidentSeverity.INFO)
    assert len(at_info) == 4


def test_active_returns_snapshot_copy():
    board = InMemorySafetyBoard()
    board.log(Incident("a", IncidentSeverity.INFO, "d", None, None, _at(0)))
    snap = board.active
    snap.clear()
    assert len(board.active) == 1


def test_note_hazard_upserts_by_id_and_orders_desc():
    board = InMemorySafetyBoard()
    board.note_hazard(Hazard("h1", "old", "cat", _at(0)))
    board.note_hazard(Hazard("h2", "second", "cat", _at(5)))
    # Same id upserts (replaces) — count stays 2, description updated.
    board.note_hazard(Hazard("h1", "new", "cat", _at(10)))
    hazards = board.hazards
    assert len(hazards) == 2
    assert hazards[0].hazard_id == "h1"  # noted_utc 10 is newest
    assert hazards[0].description == "new"
    assert hazards[1].hazard_id == "h2"


def test_note_hazard_none_raises():
    board = InMemorySafetyBoard()
    with pytest.raises(ValueError):
        board.note_hazard(None)  # type: ignore[arg-type]


def test_contacts_first_and_order():
    board = InMemorySafetyBoard()
    assert board.first_contact is None
    assert board.contacts == []
    c1 = EmergencyContact("c1", "Alice", "111", "mother")
    c2 = EmergencyContact("c2", "Bob", "222", "brother")
    board.add_contact(c1)
    board.add_contact(c2)
    assert board.first_contact == c1  # insertion order
    assert [c.contact_id for c in board.contacts] == ["c1", "c2"]


def test_add_contact_none_raises():
    board = InMemorySafetyBoard()
    with pytest.raises(ValueError):
        board.add_contact(None)  # type: ignore[arg-type]


def test_incident_lat_lon_optional():
    inc = Incident("i", IncidentSeverity.WARNING, "d", -26.2041, 28.0473, _at(0))
    assert inc.latitude == pytest.approx(-26.2041)
    assert inc.longitude == pytest.approx(28.0473)


# ── SafetyDomainContext ───────────────────────────────────────────────────────

def test_domain_context_prompt_and_flags():
    assert SafetyDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Safety]")
    assert "10111" in SafetyDomainContext.SystemPromptSnippet
    assert "10177" in SafetyDomainContext.SystemPromptSnippet
    assert list(SafetyDomainContext.ComplianceFlags) == [
        "POPIA",
        "OHS_Act",
        "Emergency_Protocol_10111",
    ]
    assert list(SafetyDomainContext.SuggestedTools) == [
        "emergency_contacts",
        "document_editor",
        "map",
        "web_search",
    ]
