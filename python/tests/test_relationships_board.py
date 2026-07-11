"""test_relationships_board.py — CircleAI.Relationships port.

Covers InMemoryRelationshipsBoard (contacts sorted, important dates this month,
last-contact tracker, not-contacted-since) and RelationshipsDomainContext.
C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timezone

from circle_ai import (
    ContactEvent,
    IRelationshipsBoard,
    ImportantDate,
    InMemoryRelationshipsBoard,
    PersonContact,
    RelationshipsDomainContext,
)


def test_board_is_irelationshipsboard():
    assert isinstance(InMemoryRelationshipsBoard(), IRelationshipsBoard)


def test_contacts_sorted_by_name():
    b = InMemoryRelationshipsBoard()
    b.add_contact(PersonContact("c2", "Zoe", "friend", None))
    b.add_contact(PersonContact("c1", "Amy", "sister", None))
    assert [c.contact_id for c in b.contacts] == ["c1", "c2"]


def test_upcoming_this_month_ordered_by_day():
    b = InMemoryRelationshipsBoard()
    m = datetime.now(timezone.utc).month
    y = datetime.now(timezone.utc).year
    b.add_important_date(ImportantDate("d2", "c1", "bday", datetime(y, m, 20, tzinfo=timezone.utc)))
    b.add_important_date(ImportantDate("d1", "c1", "anniv", datetime(y, m, 5, tzinfo=timezone.utc)))
    got = b.upcoming_this_month()
    assert [d.date_id for d in got] == ["d1", "d2"]


def test_last_contact_is_newest():
    b = InMemoryRelationshipsBoard()
    b.record_touchpoint(ContactEvent("c1", "call", datetime(2026, 1, 1, tzinfo=timezone.utc), None))
    b.record_touchpoint(ContactEvent("c1", "text", datetime(2026, 3, 1, tzinfo=timezone.utc), None))
    assert b.last_contact("c1") == datetime(2026, 3, 1, tzinfo=timezone.utc)


def test_last_contact_none_when_never():
    assert InMemoryRelationshipsBoard().last_contact("c1") is None


def test_not_contacted_since_includes_never_and_stale():
    b = InMemoryRelationshipsBoard()
    b.add_contact(PersonContact("fresh", "Amy", "f", None))
    b.add_contact(PersonContact("stale", "Bob", "f", None))
    b.add_contact(PersonContact("never", "Cy", "f", None))
    cutoff = datetime(2026, 2, 1, tzinfo=timezone.utc)
    b.record_touchpoint(ContactEvent("fresh", "call", datetime(2026, 3, 1, tzinfo=timezone.utc), None))
    b.record_touchpoint(ContactEvent("stale", "call", datetime(2026, 1, 1, tzinfo=timezone.utc), None))
    got = {c.contact_id for c in b.not_contacted_since(cutoff)}
    assert got == {"stale", "never"}


def test_relationships_domain_context():
    assert RelationshipsDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Relationships]")
    assert list(RelationshipsDomainContext.ComplianceFlags) == ["POPIA", "Not_Therapy"]
