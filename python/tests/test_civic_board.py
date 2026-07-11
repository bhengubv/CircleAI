"""test_civic_board.py — CircleAI.Civic port.

Covers InMemoryCivicBoard (report/resolve, open issues excluding Resolved,
reps by district case-insensitive incl. null-district, upcoming events) and
CivicDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    CivicDomainContext,
    CivicEvent,
    CivicIssue,
    ICivicBoard,
    InMemoryCivicBoard,
    Representative,
)


def _now_plus(days: int) -> datetime:
    return datetime.now(timezone.utc) + timedelta(days=days)


def test_board_is_icivicboard():
    assert isinstance(InMemoryCivicBoard(), ICivicBoard)


def test_open_issues_excludes_resolved():
    b = InMemoryCivicBoard()
    t = datetime(2026, 1, 1, tzinfo=timezone.utc)
    b.report(CivicIssue("i1", "roads", "pothole", 0, 0, t, "Open"))
    b.report(CivicIssue("i2", "water", "leak", 0, 0, t, "Open"))
    b.resolve("i2", "Resolved")
    assert {i.issue_id for i in b.open_issues()} == {"i1"}


def test_resolve_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryCivicBoard().resolve("nope", "Resolved")


def test_reps_for_district_case_insensitive_and_null_safe():
    b = InMemoryCivicBoard()
    b.add_rep(Representative("r1", "Ann", "Ward 1", "a@x", "Central"))
    b.add_rep(Representative("r2", "Bob", "Ward 2", "b@x", "central"))
    b.add_rep(Representative("r3", "Cy", "Mayor", "c@x", None))
    assert {r.rep_id for r in b.reps_for_district("CENTRAL")} == {"r1", "r2"}


def test_upcoming_events_future_ordered():
    b = InMemoryCivicBoard()
    b.schedule(CivicEvent("e2", "Later", _now_plus(2), "Hall", "all"))
    b.schedule(CivicEvent("e1", "Soon", _now_plus(1), "Hall", "all"))
    b.schedule(CivicEvent("old", "Past", datetime(2000, 1, 1, tzinfo=timezone.utc), "Hall", "all"))
    got = b.upcoming_events()
    assert [e.event_id for e in got] == ["e1", "e2"]


def test_civic_domain_context():
    assert CivicDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Civic]")
    assert "PAJA" in CivicDomainContext.ComplianceFlags
