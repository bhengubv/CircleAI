"""test_creative_board.py — CircleAI.Creative port.

Covers InMemoryCreativeBoard (works by tag, recent inspiration newest-first,
critique average incl. 0.0 when empty) and CreativeDomainContext. C# is the
exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    CreativeDomainContext,
    CreativeWork,
    Critique,
    ICreativeBoard,
    InMemoryCreativeBoard,
    Inspiration,
)


def _at(mins: int) -> datetime:
    return datetime(2026, 1, 1, tzinfo=timezone.utc) + timedelta(minutes=mins)


def test_board_is_icreativeboard():
    assert isinstance(InMemoryCreativeBoard(), ICreativeBoard)


def test_works_by_tag_case_insensitive():
    b = InMemoryCreativeBoard()
    b.add_work(CreativeWork("w1", "Poem", "text", "me", _at(0), ["Draft"]))
    b.add_work(CreativeWork("w2", "Song", "audio", "me", _at(1), ["draft"]))
    b.add_work(CreativeWork("w3", "Final", "text", "me", _at(2), ["done"]))
    assert {w.work_id for w in b.works_by_tag("DRAFT")} == {"w1", "w2"}


def test_recent_inspiration_newest_first_limited():
    b = InMemoryCreativeBoard()
    b.record_inspiration(Inspiration("i1", "a", "http://a", _at(0)))
    b.record_inspiration(Inspiration("i2", "b", "http://b", _at(10)))
    got = b.recent_inspiration(limit=1)
    assert [i.inspiration_id for i in got] == ["i2"]


def test_avg_score():
    b = InMemoryCreativeBoard()
    b.add_critique(Critique("c1", "w1", "r1", "good", 8))
    b.add_critique(Critique("c2", "w1", "r2", "ok", 6))
    assert b.avg_score("w1") == pytest.approx(7.0)


def test_avg_score_no_critiques_is_zero():
    assert InMemoryCreativeBoard().avg_score("w1") == 0.0


def test_creative_domain_context():
    assert CreativeDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Creative]")
    assert "Copyright_Act_98_1978" in CreativeDomainContext.ComplianceFlags
