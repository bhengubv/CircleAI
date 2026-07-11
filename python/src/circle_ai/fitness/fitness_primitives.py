# fitness_primitives.py
#
# Port of CircleAI.Fitness FitnessPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Fitness vertical:
# workouts, fitness goals, exercise sets. C# ConcurrentDictionary -> dict;
# lists guarded by a single lock. DateTimeOffset -> datetime, DateTime -> datetime.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta
from typing import Dict, List


@dataclass(frozen=True, slots=True)
class Workout:
    """Mirrors ``CircleAI.Fitness.Workout``."""

    workout_id: str
    user_id: str
    kind: str
    duration_minutes: int
    calories_burned: float
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class FitnessGoal:
    """Mirrors ``CircleAI.Fitness.FitnessGoal``."""

    goal_id: str
    user_id: str
    metric: str
    target: float
    due_on: datetime


@dataclass(frozen=True, slots=True)
class ExerciseSet:
    """Mirrors ``CircleAI.Fitness.ExerciseSet``."""

    set_id: str
    workout_id: str
    exercise: str
    reps: int
    weight_kg: float


def _week_start(now: datetime) -> datetime:
    """Midnight of the most recent Sunday, mirroring C#
    ``now.Date.AddDays(-(int)now.DayOfWeek)`` (Sunday=0).
    """
    midnight = datetime(now.year, now.month, now.day, tzinfo=now.tzinfo)
    dow = (now.weekday() + 1) % 7
    return midnight - timedelta(days=dow)


class IFitnessBoard(ABC):
    """In-memory board for workouts, goals and exercise sets."""

    @abstractmethod
    def log(self, w: Workout) -> None:
        ...

    @abstractmethod
    def workouts_this_week(self, user_id: str, now: datetime) -> List[Workout]:
        ...

    @abstractmethod
    def total_calories_since(self, user_id: str, since: datetime) -> float:
        ...

    @abstractmethod
    def set_goal(self, g: FitnessGoal) -> None:
        ...

    @abstractmethod
    def goals_for(self, user_id: str) -> List[FitnessGoal]:
        ...

    @abstractmethod
    def add_set(self, s: ExerciseSet) -> None:
        ...

    @abstractmethod
    def sets_for(self, workout_id: str) -> List[ExerciseSet]:
        ...


class InMemoryFitnessBoard(IFitnessBoard):
    """Thread-safe in-memory :class:`IFitnessBoard`."""

    def __init__(self) -> None:
        self._workouts: List[Workout] = []
        self._goals: Dict[str, FitnessGoal] = {}
        self._sets: List[ExerciseSet] = []
        self._lock = threading.Lock()

    def log(self, w: Workout) -> None:
        if w is None:
            raise ValueError("workout must not be None")
        with self._lock:
            self._workouts.append(w)

    def workouts_this_week(self, user_id: str, now: datetime) -> List[Workout]:
        week_start = _week_start(now)
        with self._lock:
            items = [
                w
                for w in self._workouts
                if w.user_id == user_id and w.at_utc >= week_start
            ]
        items.sort(key=lambda w: w.at_utc)
        return items

    def total_calories_since(self, user_id: str, since: datetime) -> float:
        with self._lock:
            return sum(
                w.calories_burned
                for w in self._workouts
                if w.user_id == user_id and w.at_utc >= since
            )

    def set_goal(self, g: FitnessGoal) -> None:
        if g is None:
            raise ValueError("fitness goal must not be None")
        with self._lock:
            self._goals[g.goal_id] = g

    def goals_for(self, user_id: str) -> List[FitnessGoal]:
        with self._lock:
            return [g for g in self._goals.values() if g.user_id == user_id]

    def add_set(self, s: ExerciseSet) -> None:
        if s is None:
            raise ValueError("exercise set must not be None")
        with self._lock:
            self._sets.append(s)

    def sets_for(self, workout_id: str) -> List[ExerciseSet]:
        with self._lock:
            return [s for s in self._sets if s.workout_id == workout_id]
