"""test_personal_mental_board.py — CircleAI.Personal.Mental port.

Covers Mood ordinals, the domain records, InMemoryMentalHealthBoard (mood log +
live 7-day window, journal entry add with EntryId guard + descending order,
coping-strategy tag lookup case-insensitively, 7-day average with the NaN empty
rule) and the static PersonalMentalDomainContext. C# is the exact spec.

Last7Days uses a live UtcNow-7d cutoff, so timestamps are anchored to now().
"""
from __future__ import annotations

import math
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    CopingStrategy,
    InMemoryMentalHealthBoard,
    IMentalHealthBoard,
    JournalEntry,
    Mood,
    MoodLog,
    PersonalMentalDomainContext,
)


def _now() -> datetime:
    return datetime.now(timezone.utc)


def test_mood_ordinals_stable():
    assert [int(m) for m in Mood] == [0, 1, 2, 3, 4]
    assert Mood.VERY_LOW == 0 and Mood.GREAT == 4


def test_board_is_imentalhealthboard():
    assert isinstance(InMemoryMentalHealthBoard(), IMentalHealthBoard)


def test_log_mood_none_raises():
    with pytest.raises(ValueError):
        InMemoryMentalHealthBoard().log_mood(None)  # type: ignore[arg-type]


def test_last_7_days_windows_and_orders_ascending():
    board = InMemoryMentalHealthBoard()
    now = _now()
    board.log_mood(MoodLog(Mood.LOW, now - timedelta(days=10), "old"))     # outside window
    board.log_mood(MoodLog(Mood.GOOD, now - timedelta(days=1), "recent"))
    board.log_mood(MoodLog(Mood.GREAT, now - timedelta(days=3), "mid"))
    recent = board.last_7_days()
    assert [m.note for m in recent] == ["mid", "recent"]  # ascending by time, old excluded


def test_add_entry_requires_entry_id():
    board = InMemoryMentalHealthBoard()
    with pytest.raises(ValueError):
        board.add_entry(JournalEntry("", "t", "b", _now()))
    with pytest.raises(ValueError):
        board.add_entry(JournalEntry("   ", "t", "b", _now()))


def test_add_entry_none_raises():
    with pytest.raises(ValueError):
        InMemoryMentalHealthBoard().add_entry(None)  # type: ignore[arg-type]


def test_entries_descending_by_time_and_upsert():
    board = InMemoryMentalHealthBoard()
    now = _now()
    board.add_entry(JournalEntry("e1", "First", "b", now - timedelta(hours=3)))
    board.add_entry(JournalEntry("e2", "Second", "b", now - timedelta(hours=1)))
    board.add_entry(JournalEntry("e1", "First-edited", "b2", now - timedelta(hours=2)))  # upsert
    entries = board.entries
    # e2 is at -1h (newest); e1 was upserted to -2h -> descending order: e2, e1.
    assert [e.entry_id for e in entries] == ["e2", "e1"]
    assert len(entries) == 2  # upsert kept a single e1
    assert {e.entry_id: e.title for e in entries}["e1"] == "First-edited"


def test_register_strategy_none_raises():
    with pytest.raises(ValueError):
        InMemoryMentalHealthBoard().register_strategy(None)  # type: ignore[arg-type]


def test_strategies_by_tag_case_insensitive():
    board = InMemoryMentalHealthBoard()
    board.register_strategy(CopingStrategy("s1", "Box Breathing", "…", ("Anxiety", "Breath")))
    board.register_strategy(CopingStrategy("s2", "Grounding", "…", ("anxiety",)))
    board.register_strategy(CopingStrategy("s3", "Journaling", "…", ("Reflection",)))
    hits = {s.strategy_id for s in board.strategies_by_tag("ANXIETY")}
    assert hits == {"s1", "s2"}
    assert board.strategies_by_tag("none") == []


def test_strategies_by_tag_blank_raises():
    board = InMemoryMentalHealthBoard()
    with pytest.raises(ValueError):
        board.strategies_by_tag("")
    with pytest.raises(ValueError):
        board.strategies_by_tag("   ")


def test_avg_mood_7_day_averages_ordinals():
    board = InMemoryMentalHealthBoard()
    now = _now()
    board.log_mood(MoodLog(Mood.NEUTRAL, now - timedelta(days=1), None))  # 2
    board.log_mood(MoodLog(Mood.GREAT, now - timedelta(days=2), None))    # 4
    board.log_mood(MoodLog(Mood.LOW, now - timedelta(days=10), None))     # outside window
    assert board.avg_mood_7_day() == pytest.approx(3.0)  # (2 + 4) / 2


def test_avg_mood_7_day_empty_is_nan():
    assert math.isnan(InMemoryMentalHealthBoard().avg_mood_7_day())


def test_personal_mental_domain_context():
    ctx = PersonalMentalDomainContext
    assert ctx.SystemPromptSnippet.startswith("[DOMAIN: Personal.Mental]")
    assert "SADAG" in ctx.SystemPromptSnippet
    assert list(ctx.ComplianceFlags) == [
        "POPIA",
        "Mental_Health_Care_Act_17_2002",
        "Not_Therapy",
        "Crisis_Protocol",
    ]
    assert list(ctx.SuggestedTools) == ["journal", "breathing_tools", "mood_tracker", "web_search"]
