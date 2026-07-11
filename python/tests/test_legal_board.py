"""test_legal_board.py — CircleAI.Legal port.

Covers the domain records, InMemoryLegalBoard (matter open/close, active-matter
descending order, contract expiry filtering + ascending order, upcoming-deadline
filtering + ascending order, clause tag lookup case-insensitively) and the static
LegalDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    Clause,
    Contract,
    ILegalBoard,
    InMemoryLegalBoard,
    LegalDeadline,
    LegalDomainContext,
    Matter,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(days: int) -> datetime:
    return _T0 + timedelta(days=days)


def _d(days: int) -> datetime:
    return datetime(2026, 1, 1) + timedelta(days=days)


def test_board_is_ilegalboard():
    assert isinstance(InMemoryLegalBoard(), ILegalBoard)


def test_open_close_and_active_matters_descending():
    board = InMemoryLegalBoard()
    board.open(Matter("m1", "Old", "ZA", "C1", _at(0), True))
    board.open(Matter("m2", "New", "ZA", "C2", _at(10), True))
    board.open(Matter("m3", "Mid", "ZA", "C3", _at(5), True))
    assert [m.matter_id for m in board.active_matters] == ["m2", "m3", "m1"]
    board.close("m2")
    assert board.get_matter("m2").open is False
    assert [m.matter_id for m in board.active_matters] == ["m3", "m1"]


def test_open_none_raises():
    with pytest.raises(ValueError):
        InMemoryLegalBoard().open(None)  # type: ignore[arg-type]


def test_close_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryLegalBoard().close("nope")


def test_contracts_expiring_before_filters_and_orders():
    board = InMemoryLegalBoard()
    board.add_contract(Contract("c1", "m1", "A", _d(0), _d(30), ("X",)))
    board.add_contract(Contract("c2", "m1", "B", _d(0), _d(10), ("Y",)))
    board.add_contract(Contract("c3", "m1", "NoExpiry", _d(0), None, ("Z",)))
    board.add_contract(Contract("c4", "m1", "Later", _d(0), _d(90), ("W",)))
    res = board.contracts_expiring_before(_d(40))
    assert [c.contract_id for c in res] == ["c2", "c1"]  # 10, 30; None + 90 excluded


def test_add_contract_none_raises():
    with pytest.raises(ValueError):
        InMemoryLegalBoard().add_contract(None)  # type: ignore[arg-type]


def test_upcoming_deadlines_filters_and_orders():
    board = InMemoryLegalBoard()
    board.add(LegalDeadline("d1", "m1", "past", _d(1)))
    board.add(LegalDeadline("d2", "m1", "soon", _d(5)))
    board.add(LegalDeadline("d3", "m1", "later", _d(20)))
    res = board.upcoming_deadlines(_d(3))
    assert [d.deadline_id for d in res] == ["d2", "d3"]  # d1 (day 1) is before now (day 3)


def test_upcoming_deadlines_inclusive_of_now():
    board = InMemoryLegalBoard()
    board.add(LegalDeadline("d0", "m1", "exactly now", _d(3)))
    assert [d.deadline_id for d in board.upcoming_deadlines(_d(3))] == ["d0"]


def test_add_deadline_none_raises():
    with pytest.raises(ValueError):
        InMemoryLegalBoard().add(None)  # type: ignore[arg-type]


def test_clauses_by_tag_case_insensitive():
    board = InMemoryLegalBoard()
    board.add_clause(Clause("cl1", "Indemnity", "body", ("Liability", "Risk")))
    board.add_clause(Clause("cl2", "Termination", "body", ("Exit",)))
    board.add_clause(Clause("cl3", "Cap", "body", ("risk",)))
    hits = {c.clause_id for c in board.clauses_by_tag("RISK")}
    assert hits == {"cl1", "cl3"}
    assert board.clauses_by_tag("none") == []


def test_add_clause_none_raises():
    with pytest.raises(ValueError):
        InMemoryLegalBoard().add_clause(None)  # type: ignore[arg-type]


def test_clauses_by_tag_blank_raises():
    board = InMemoryLegalBoard()
    with pytest.raises(ValueError):
        board.clauses_by_tag("")
    with pytest.raises(ValueError):
        board.clauses_by_tag("   ")


def test_legal_domain_context():
    assert LegalDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Legal]")
    assert "not legal advice" in LegalDomainContext.SystemPromptSnippet
    assert list(LegalDomainContext.ComplianceFlags) == [
        "Legal_Practice_Act_28_2014",
        "Attorneys_Act",
        "POPIA",
        "Professional_Legal_Privilege",
    ]
    assert list(LegalDomainContext.SuggestedTools) == [
        "legal_research",
        "document_editor",
        "contract_analyser",
    ]
