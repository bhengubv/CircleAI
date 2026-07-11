"""circle_ai.fitness — port of the CircleAI.Fitness assembly.

(3.3.0) Real domain types + in-memory board for the Fitness vertical: workouts,
fitness goals, exercise sets, weekly volume + calorie totals — plus the static
domain context. C# is the exact spec.

The C# ``FitnessCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .fitness_domain_context import FitnessDomainContext
from .fitness_primitives import (
    ExerciseSet,
    FitnessGoal,
    IFitnessBoard,
    InMemoryFitnessBoard,
    Workout,
)

__all__ = [
    "Workout",
    "FitnessGoal",
    "ExerciseSet",
    "IFitnessBoard",
    "InMemoryFitnessBoard",
    "FitnessDomainContext",
]
