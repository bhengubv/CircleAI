"""test_business_board.py — CircleAI.Business port.

Covers the domain records, InMemoryBusinessBoard (unit upsert, children lookup,
KPI recording with latest-by-time + NaN-when-empty, target keying and
target-achievement ratio incl. NaN on missing/zero target) and the static
BusinessDomainContext. C# is the exact spec.
"""
from __future__ import annotations

import math
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    BusinessDomainContext,
    BusinessUnit,
    IBusinessBoard,
    InMemoryBusinessBoard,
    KpiSample,
    QuarterTarget,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(mins: int) -> datetime:
    return _T0 + timedelta(minutes=mins)


def test_board_is_ibusinessboard():
    assert isinstance(InMemoryBusinessBoard(), IBusinessBoard)


def test_add_get_and_children():
    board = InMemoryBusinessBoard()
    board.add(BusinessUnit("root", "Root", "", ["rev"]))
    board.add(BusinessUnit("a", "A", "root", ["rev"]))
    board.add(BusinessUnit("b", "B", "root", ["rev"]))
    board.add(BusinessUnit("a1", "A1", "a", []))
    assert board.get_unit("a").name == "A"
    assert {u.unit_id for u in board.children_of("root")} == {"a", "b"}
    assert {u.unit_id for u in board.children_of("a")} == {"a1"}


def test_add_none_raises():
    with pytest.raises(ValueError):
        InMemoryBusinessBoard().add(None)  # type: ignore[arg-type]


def test_latest_kpi_picks_newest():
    board = InMemoryBusinessBoard()
    board.record(KpiSample("a", "rev", 100.0, _at(0)))
    board.record(KpiSample("a", "rev", 300.0, _at(20)))
    board.record(KpiSample("a", "rev", 200.0, _at(10)))
    board.record(KpiSample("a", "cost", 50.0, _at(30)))
    assert board.latest_kpi("a", "rev") == 300.0


def test_latest_kpi_missing_is_nan():
    board = InMemoryBusinessBoard()
    assert math.isnan(board.latest_kpi("a", "rev"))


def test_record_none_raises():
    with pytest.raises(ValueError):
        InMemoryBusinessBoard().record(None)  # type: ignore[arg-type]


def test_target_achievement_ratio():
    board = InMemoryBusinessBoard()
    board.record(KpiSample("a", "rev", 80.0, _at(0)))
    board.set_target(QuarterTarget("a", "rev", 2026, 1, 100.0))
    assert board.target_achievement("a", "rev", 2026, 1) == pytest.approx(0.8)


def test_target_achievement_missing_target_is_nan():
    board = InMemoryBusinessBoard()
    board.record(KpiSample("a", "rev", 80.0, _at(0)))
    assert math.isnan(board.target_achievement("a", "rev", 2026, 1))


def test_target_achievement_zero_target_is_nan():
    board = InMemoryBusinessBoard()
    board.record(KpiSample("a", "rev", 80.0, _at(0)))
    board.set_target(QuarterTarget("a", "rev", 2026, 1, 0.0))
    assert math.isnan(board.target_achievement("a", "rev", 2026, 1))


def test_set_target_none_raises():
    with pytest.raises(ValueError):
        InMemoryBusinessBoard().set_target(None)  # type: ignore[arg-type]


def test_business_domain_context():
    assert BusinessDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Business]")
    assert list(BusinessDomainContext.ComplianceFlags) == [
        "POPIA",
        "Commercial_Law",
        "GDPR_aware",
    ]
    assert list(BusinessDomainContext.SuggestedTools) == [
        "calendar",
        "web_search",
        "document_editor",
        "task_manager",
    ]
