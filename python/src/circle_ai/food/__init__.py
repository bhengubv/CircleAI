"""circle_ai.food — port of the CircleAI.Food assembly.

(3.3.0) Real domain types + in-memory board for the Food vertical: recipes,
meal logs, and a pantry with best-before expiry tracking — plus the static
domain context. C# is the exact spec.

The C# ``FoodCompanionAdapter`` (decorates ``ICompanionSession``) is
intentionally not ported.
"""
from __future__ import annotations

from .food_domain_context import FoodDomainContext
from .food_primitives import (
    IFoodBoard,
    InMemoryFoodBoard,
    MealLog,
    PantryItem,
    Recipe,
)

__all__ = [
    "Recipe",
    "MealLog",
    "PantryItem",
    "IFoodBoard",
    "InMemoryFoodBoard",
    "FoodDomainContext",
]
