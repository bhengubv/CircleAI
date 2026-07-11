"""test_construction_board.py — CircleAI.Construction port.

Covers InMemoryConstructionBoard (projects, open tasks ordered by due, spend +
remaining budget, unknown guards) and ConstructionDomainContext. C# is the
exact spec.
"""
from __future__ import annotations

from datetime import datetime, timezone
from decimal import Decimal

import pytest

from circle_ai import (
    ConstructionDomainContext,
    ConstructionProject,
    ConstructionTask,
    CostEntry,
    IConstructionBoard,
    InMemoryConstructionBoard,
)


def _d(day: int) -> datetime:
    return datetime(2026, 4, day, tzinfo=timezone.utc)


def test_board_is_iconstructionboard():
    assert isinstance(InMemoryConstructionBoard(), IConstructionBoard)


def test_open_tasks_ordered_by_due_and_complete():
    b = InMemoryConstructionBoard()
    b.add(ConstructionTask("t2", "p1", "roof", _d(10), False))
    b.add(ConstructionTask("t1", "p1", "foundation", _d(2), False))
    b.add(ConstructionTask("done", "p1", "survey", _d(1), True))
    assert [t.construction_task_id for t in b.open_construction_tasks_for("p1")] == ["t1", "t2"]
    b.complete("t1")
    assert [t.construction_task_id for t in b.open_construction_tasks_for("p1")] == ["t2"]


def test_complete_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryConstructionBoard().complete("nope")


def test_spend_and_remaining_budget():
    b = InMemoryConstructionBoard()
    b.create(ConstructionProject("p1", "House", _d(1), None, Decimal("100000"), "ZAR"))
    b.record_cost(CostEntry("c1", "p1", "materials", Decimal("30000"), datetime(2026, 4, 5, tzinfo=timezone.utc)))
    b.record_cost(CostEntry("c2", "p1", "labour", Decimal("20000"), datetime(2026, 4, 6, tzinfo=timezone.utc)))
    assert b.spend_for("p1") == Decimal("50000")
    assert b.remaining_budget("p1") == Decimal("50000")


def test_remaining_budget_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryConstructionBoard().remaining_budget("nope")


def test_construction_domain_context():
    assert ConstructionDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Construction]")
    assert "NHBRC_Act" in ConstructionDomainContext.ComplianceFlags
