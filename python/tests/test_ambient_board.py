"""test_ambient_board.py — CircleAI.Ambient port.

Covers InMemoryAmbientBoard (latest reading, history newest-first limited,
comfort check within tolerance / False when missing pref or reading). C# is the
exact spec. (The Ambient assembly has no domain context.)
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

from circle_ai import (
    AmbientPreference,
    AmbientReading,
    IAmbientBoard,
    InMemoryAmbientBoard,
)


def _at(mins: int) -> datetime:
    return datetime(2026, 1, 1, tzinfo=timezone.utc) + timedelta(minutes=mins)


def test_board_is_iambientboard():
    assert isinstance(InMemoryAmbientBoard(), IAmbientBoard)


def test_latest_and_history():
    b = InMemoryAmbientBoard()
    b.record(AmbientReading("d1", 21.0, 45.0, 300.0, 35.0, _at(0)))
    b.record(AmbientReading("d1", 22.0, 46.0, 310.0, 36.0, _at(10)))
    assert b.latest("d1").temperature_c == 22.0
    hist = b.history("d1", limit=1)
    assert len(hist) == 1 and hist[0].at_utc == _at(10)


def test_is_comfortable_within_tolerance():
    b = InMemoryAmbientBoard()
    b.set_preference(AmbientPreference("lounge", 22.0, 45.0, 40.0))
    b.record(AmbientReading("d1", 23.5, 50.0, 300.0, 38.0, _at(0)))  # all within tol
    assert b.is_comfortable("d1", "lounge") is True


def test_is_comfortable_false_when_out_of_range():
    b = InMemoryAmbientBoard()
    b.set_preference(AmbientPreference("lounge", 22.0, 45.0, 40.0))
    b.record(AmbientReading("d1", 30.0, 50.0, 300.0, 38.0, _at(0)))  # temp too high
    assert b.is_comfortable("d1", "lounge") is False


def test_is_comfortable_false_without_pref_or_reading():
    b = InMemoryAmbientBoard()
    assert b.is_comfortable("d1", "lounge") is False
    b.record(AmbientReading("d1", 22.0, 45.0, 300.0, 30.0, _at(0)))
    assert b.is_comfortable("d1", "lounge") is False  # still no pref
