"""test_fitness_board.py — CircleAI.Fitness port.

Covers InMemoryFitnessBoard (log + weekly workouts, calorie totals, goals,
exercise sets) and FitnessDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    ExerciseSet,
    FitnessDomainContext,
    FitnessGoal,
    IFitnessBoard,
    InMemoryFitnessBoard,
    Workout,
)


def test_board_is_ifitnessboard():
    assert isinstance(InMemoryFitnessBoard(), IFitnessBoard)


def test_workouts_this_week_and_order():
    b = InMemoryFitnessBoard()
    now = datetime(2026, 1, 7, 12, 0, tzinfo=timezone.utc)  # Wed; week start Sun 01-04
    b.log(Workout("w1", "u", "run", 30, 300.0, datetime(2026, 1, 5, 6, 0, tzinfo=timezone.utc)))
    b.log(Workout("w2", "u", "lift", 45, 200.0, datetime(2026, 1, 4, 6, 0, tzinfo=timezone.utc)))
    b.log(Workout("old", "u", "run", 30, 999.0, datetime(2026, 1, 1, 6, 0, tzinfo=timezone.utc)))
    got = b.workouts_this_week("u", now)
    assert [w.workout_id for w in got] == ["w2", "w1"]  # ordered ascending by time


def test_total_calories_since():
    b = InMemoryFitnessBoard()
    since = datetime(2026, 1, 1, tzinfo=timezone.utc)
    b.log(Workout("w1", "u", "run", 30, 300.0, datetime(2026, 1, 2, tzinfo=timezone.utc)))
    b.log(Workout("w2", "u", "run", 30, 250.0, datetime(2026, 1, 3, tzinfo=timezone.utc)))
    b.log(Workout("before", "u", "run", 30, 999.0, datetime(2025, 12, 31, tzinfo=timezone.utc)))
    assert b.total_calories_since("u", since) == pytest.approx(550.0)


def test_goals_for_user():
    b = InMemoryFitnessBoard()
    due = datetime(2026, 6, 1, tzinfo=timezone.utc)
    b.set_goal(FitnessGoal("g1", "u", "weight", 80.0, due))
    b.set_goal(FitnessGoal("g2", "other", "weight", 90.0, due))
    assert {g.goal_id for g in b.goals_for("u")} == {"g1"}


def test_sets_for_workout():
    b = InMemoryFitnessBoard()
    b.add_set(ExerciseSet("s1", "w1", "squat", 5, 100.0))
    b.add_set(ExerciseSet("s2", "w1", "bench", 5, 80.0))
    b.add_set(ExerciseSet("s3", "w2", "row", 8, 60.0))
    assert {s.set_id for s in b.sets_for("w1")} == {"s1", "s2"}


def test_none_guards():
    b = InMemoryFitnessBoard()
    for fn in (lambda: b.log(None), lambda: b.set_goal(None), lambda: b.add_set(None)):
        with pytest.raises(ValueError):
            fn()  # type: ignore[misc]


def test_fitness_domain_context():
    assert FitnessDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Fitness]")
    assert list(FitnessDomainContext.ComplianceFlags) == [
        "HPCSA_Fitness",
        "POPIA",
        "Not_Medical_Advice",
    ]
