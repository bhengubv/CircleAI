"""test_community_board.py — CircleAI.Community port.

Covers InMemoryCommunityBoard (groups, member lookup, announcements newest-first
limited, future opportunities) and CommunityDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

from circle_ai import (
    Announcement,
    CommunityDomainContext,
    CommunityGroup,
    ICommunityBoard,
    InMemoryCommunityBoard,
    VolunteerOpportunity,
)


def _at(mins: int) -> datetime:
    return datetime(2026, 1, 1, tzinfo=timezone.utc) + timedelta(minutes=mins)


def test_board_is_icommunityboard():
    assert isinstance(InMemoryCommunityBoard(), ICommunityBoard)


def test_groups_for_member():
    b = InMemoryCommunityBoard()
    b.create(CommunityGroup("g1", "Garden", "grow", ["u1", "u2"]))
    b.create(CommunityGroup("g2", "Watch", "safety", ["u2"]))
    b.create(CommunityGroup("g3", "Book", "read", ["u3"]))
    assert {g.group_id for g in b.groups_for_member("u2")} == {"g1", "g2"}


def test_announcements_newest_first_limited():
    b = InMemoryCommunityBoard()
    b.post(Announcement("a1", "g1", "t1", "b", _at(0)))
    b.post(Announcement("a2", "g1", "t2", "b", _at(10)))
    b.post(Announcement("a3", "g1", "t3", "b", _at(20)))
    got = b.announcements_for("g1", limit=2)
    assert [a.announcement_id for a in got] == ["a3", "a2"]


def test_opportunities_future_only_ordered():
    b = InMemoryCommunityBoard()
    now = datetime.now(timezone.utc)
    b.list(VolunteerOpportunity("o2", "g1", "later", 3, now + timedelta(days=2)))
    b.list(VolunteerOpportunity("o1", "g1", "soon", 2, now + timedelta(days=1)))
    b.list(VolunteerOpportunity("old", "g1", "past", 1, now - timedelta(days=1)))
    got = b.opportunities()
    assert [o.opp_id for o in got] == ["o1", "o2"]


def test_community_domain_context():
    assert CommunityDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Community]")
    assert "NPO_Act" in CommunityDomainContext.ComplianceFlags
