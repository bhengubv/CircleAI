"""test_hr_board.py — CircleAI.HR port.

Covers the domain records, InMemoryHRBoard (hire upsert, name-ordered Employees,
leave request + decision, case-insensitive pending filter, review recording and
average rating with the empty=0.0 rule) and the static HRDomainContext. C# is
the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timezone
from decimal import Decimal

import pytest

from circle_ai import (
    Employee,
    HRDomainContext,
    IHRBoard,
    InMemoryHRBoard,
    LeaveRequest,
    PerformanceReview,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def test_board_is_ihrboard():
    assert isinstance(InMemoryHRBoard(), IHRBoard)


def test_hire_upserts_and_get():
    board = InMemoryHRBoard()
    assert board.get_employee("e1") is None
    board.hire(Employee("e1", "Ann", "Dev", _T0, Decimal("50000"), "ZAR"))
    board.hire(Employee("e1", "Ann", "Lead", _T0, Decimal("60000"), "ZAR"))
    got = board.get_employee("e1")
    assert got is not None and got.role == "Lead" and got.salary == Decimal("60000")


def test_hire_none_raises():
    with pytest.raises(ValueError):
        InMemoryHRBoard().hire(None)  # type: ignore[arg-type]


def test_employees_ordered_by_name():
    board = InMemoryHRBoard()
    board.hire(Employee("e2", "Zoe", "Dev", _T0, Decimal("1"), "ZAR"))
    board.hire(Employee("e1", "Ann", "Dev", _T0, Decimal("1"), "ZAR"))
    board.hire(Employee("e3", "Max", "Dev", _T0, Decimal("1"), "ZAR"))
    assert [e.name for e in board.employees] == ["Ann", "Max", "Zoe"]


def test_leave_request_decide_and_pending():
    board = InMemoryHRBoard()
    board.request(LeaveRequest("r1", "e1", "Annual", _T0, _T0, "Pending"))
    board.request(LeaveRequest("r2", "e1", "Sick", _T0, _T0, "pending"))
    board.request(LeaveRequest("r3", "e2", "Annual", _T0, _T0, "Approved"))
    board.decide_leave("r1", "Approved")
    pending_ids = {r.request_id for r in board.pending_leaves()}
    # r2 stays pending (case-insensitive), r1 now approved, r3 already approved.
    assert pending_ids == {"r2"}


def test_decide_leave_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryHRBoard().decide_leave("nope", "Approved")


def test_request_none_raises():
    with pytest.raises(ValueError):
        InMemoryHRBoard().request(None)  # type: ignore[arg-type]


def test_avg_rating_for_computes_mean():
    board = InMemoryHRBoard()
    board.review(PerformanceReview("v1", "e1", _T0, 4, "ok"))
    board.review(PerformanceReview("v2", "e1", _T0, 2, "meh"))
    board.review(PerformanceReview("v3", "e2", _T0, 5, "great"))
    assert board.avg_rating_for("e1") == pytest.approx(3.0)


def test_avg_rating_for_no_reviews_is_zero():
    board = InMemoryHRBoard()
    board.review(PerformanceReview("v1", "e1", _T0, 5, "ok"))
    assert board.avg_rating_for("nobody") == 0.0


def test_review_none_raises():
    with pytest.raises(ValueError):
        InMemoryHRBoard().review(None)  # type: ignore[arg-type]


def test_hr_domain_context():
    assert HRDomainContext.SystemPromptSnippet.startswith("[DOMAIN: HR]")
    assert list(HRDomainContext.ComplianceFlags) == [
        "LRA_66_1995",
        "BCEA",
        "EEA",
        "Skills_Development_Act",
        "POPIA",
    ]
    assert list(HRDomainContext.SuggestedTools) == [
        "hris",
        "document_editor",
        "analytics",
        "job_boards",
    ]
