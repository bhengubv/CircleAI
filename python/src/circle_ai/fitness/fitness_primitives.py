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
from typing import Dict, List, Optional


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

    @property
    def workout_count(self) -> int:
        """Total number of logged workouts (C#: ``WorkoutCount``)."""
        with self._lock:
            return len(self._workouts)

    def workouts_by_kind(self, user_id: str, kind: str) -> List[Workout]:
        """A user's workouts of a given kind (case-insensitive), newest-first
        (C#: ``WorkoutsByKind``).
        """
        target = kind.casefold()
        with self._lock:
            matches = [
                w
                for w in self._workouts
                if w.user_id == user_id and w.kind.casefold() == target
            ]
        return sorted(matches, key=lambda w: w.at_utc, reverse=True)

    def remove_goal(self, goal_id: str) -> bool:
        """Remove a goal. Returns True if one was present (C#: ``RemoveGoal``)."""
        with self._lock:
            return self._goals.pop(goal_id, None) is not None

    def goal_by_metric(
        self, user_id: str, metric: str
    ) -> Optional[FitnessGoal]:
        """A user's soonest-due goal for a given metric (case-insensitive), or
        None (C#: ``GoalByMetric`` — ordered by ``DueOn``, first).
        """
        target = metric.casefold()
        with self._lock:
            matches = [
                g
                for g in self._goals.values()
                if g.user_id == user_id and g.metric.casefold() == target
            ]
        if not matches:
            return None
        return min(matches, key=lambda g: g.due_on)

    def avg_duration_since(self, user_id: str, since: datetime) -> float:
        """Mean workout duration (minutes) for a user since ``since``; 0.0 when
        none (C#: ``AvgDurationSince`` — ``DefaultIfEmpty(0).Average()``).
        """
        with self._lock:
            durations = [
                float(w.duration_minutes)
                for w in self._workouts
                if w.user_id == user_id and w.at_utc >= since
            ]
        return sum(durations) / len(durations) if durations else 0.0

    def total_volume_kg(self, workout_id: str) -> float:
        """Total lifted volume (sum of reps x weight) across a workout's sets
        (C#: ``TotalVolumeKg``).
        """
        with self._lock:
            return sum(
                s.reps * s.weight_kg
                for s in self._sets
                if s.workout_id == workout_id
            )
