"""test_goal_progress.py

Validates Goal.advance_progress() against all vectors in
fixtures/goal_progress.json.
"""
from __future__ import annotations

import json
import pathlib
from datetime import datetime, timezone

import pytest

from circle_ai.memory.goal import Goal, GoalPriority, GoalStatus

FIXTURES_DIR = pathlib.Path(__file__).parent.parent.parent / "fixtures"
EPSILON = 1e-5


def _load_vectors() -> list[dict]:
    with open(FIXTURES_DIR / "goal_progress.json", encoding="utf-8") as f:
        data = json.load(f)
    return data["vectors"]


VECTORS = _load_vectors()


def _make_goal(progress: float) -> Goal:
    return Goal(
        id="test-goal",
        user_id="test-user",
        title="Test Goal",
        description="A test goal",
        status=GoalStatus.ACTIVE,
        priority=GoalPriority.NORMAL,
        created_utc=datetime.now(timezone.utc),
        progress=progress,
    )


@pytest.mark.parametrize("vector", VECTORS, ids=[v["id"] for v in VECTORS])
def test_goal_advance_progress(vector: dict) -> None:
    goal = _make_goal(float(vector["initial_progress"]))
    delta = float(vector["delta"])
    expected = float(vector["expected_progress"])

    result = goal.advance_progress(delta)

    assert abs(result.progress - expected) <= EPSILON, (
        f"[{vector['id']}] progress mismatch: got {result.progress}, expected {expected}"
    )


def test_original_goal_not_mutated() -> None:
    """advance_progress must return a new Goal, leaving the original unchanged."""
    goal = _make_goal(0.4)
    result = goal.advance_progress(0.3)
    assert goal.progress == pytest.approx(0.4, abs=EPSILON)
    assert result.progress == pytest.approx(0.7, abs=EPSILON)


def test_clamp_max() -> None:
    goal = _make_goal(0.9)
    result = goal.advance_progress(0.5)
    assert result.progress == pytest.approx(1.0, abs=EPSILON)


def test_clamp_min() -> None:
    goal = _make_goal(0.1)
    result = goal.advance_progress(-0.5)
    assert result.progress == pytest.approx(0.0, abs=EPSILON)
