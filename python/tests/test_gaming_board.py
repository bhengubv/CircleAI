"""test_gaming_board.py — CircleAI.Gaming port.

Covers InMemoryGamingBoard (titles by genre, play-time totals, achievements
newest-first, most-played ranking) and GamingDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    AchievementUnlock,
    GameTitle,
    GamingDomainContext,
    IGamingBoard,
    InMemoryGamingBoard,
    PlaySession,
)


def _at(mins: int) -> datetime:
    return datetime(2026, 1, 1, tzinfo=timezone.utc) + timedelta(minutes=mins)


def test_board_is_igamingboard():
    assert isinstance(InMemoryGamingBoard(), IGamingBoard)


def test_titles_by_genre_case_insensitive():
    b = InMemoryGamingBoard()
    b.add_title(GameTitle("t1", "A", "RPG", "PC"))
    b.add_title(GameTitle("t2", "B", "rpg", "PC"))
    b.add_title(GameTitle("t3", "C", "FPS", "PC"))
    assert {t.title_id for t in b.titles_by_genre("RPG")} == {"t1", "t2"}


def test_total_play_time_sums_durations():
    b = InMemoryGamingBoard()
    b.record_session(PlaySession("s1", "u", "t1", timedelta(minutes=30), _at(0)))
    b.record_session(PlaySession("s2", "u", "t1", timedelta(minutes=90), _at(10)))
    b.record_session(PlaySession("s3", "u", "t2", timedelta(minutes=15), _at(20)))
    assert b.total_play_time("u", "t1") == timedelta(minutes=120)


def test_achievements_newest_first():
    b = InMemoryGamingBoard()
    b.unlock(AchievementUnlock("x1", "u", "t1", "First", _at(0)))
    b.unlock(AchievementUnlock("x2", "u", "t1", "Second", _at(60)))
    got = b.achievements_for("u")
    assert [a.unlock_id for a in got] == ["x2", "x1"]


def test_most_played_ranked_and_topk():
    b = InMemoryGamingBoard()
    b.add_title(GameTitle("t1", "A", "RPG", "PC"))
    b.add_title(GameTitle("t2", "B", "FPS", "PC"))
    b.record_session(PlaySession("s1", "u", "t1", timedelta(minutes=10), _at(0)))
    b.record_session(PlaySession("s2", "u", "t2", timedelta(minutes=100), _at(1)))
    ranked = b.most_played("u", top_k=1)
    assert [t.title_id for t in ranked] == ["t2"]


def test_most_played_topk_zero_raises():
    with pytest.raises(ValueError):
        InMemoryGamingBoard().most_played("u", top_k=0)


def test_gaming_domain_context():
    assert GamingDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Gaming]")
    assert list(GamingDomainContext.ComplianceFlags) == [
        "POPIA",
        "WASPA",
        "Child_Protection",
    ]
