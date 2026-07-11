# food_primitives.py
#
# Port of CircleAI.Food FoodPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Food vertical: recipes,
# meal logs, pantry (with best-before expiry). C# ConcurrentDictionary -> dict;
# the meal-log list is guarded by a single lock. DateTimeOffset -> datetime,
# DateTime? -> Optional[datetime]. Ingredient search is case-insensitive
# substring match (C# Contains(..., OrdinalIgnoreCase)).

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import Dict, List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class Recipe:
    """Mirrors ``CircleAI.Food.Recipe``."""

    recipe_id: str
    title: str
    ingredients: Sequence[str]
    steps: Sequence[str]
    servings: int
    prep_minutes: int


@dataclass(frozen=True, slots=True)
class MealLog:
    """Mirrors ``CircleAI.Food.MealLog``."""

    log_id: str
    user_id: str
    recipe_id: str
    at_utc: datetime
    servings: int


@dataclass(frozen=True, slots=True)
class PantryItem:
    """Mirrors ``CircleAI.Food.PantryItem`` — ``DateTime? BestBefore``."""

    pantry_item_id: str
    name: str
    quantity: float
    unit: str
    best_before: Optional[datetime]


class IFoodBoard(ABC):
    """In-memory board for recipes, meal logs and a pantry."""

    @abstractmethod
    def add_recipe(self, r: Recipe) -> None:
        ...

    @abstractmethod
    def get_recipe(self, id: str) -> Optional[Recipe]:
        ...

    @abstractmethod
    def search_by_ingredient(self, ingredient: str) -> List[Recipe]:
        ...

    @abstractmethod
    def log(self, m: MealLog) -> None:
        ...

    @abstractmethod
    def logs_since(self, user_id: str, since: datetime) -> List[MealLog]:
        ...

    @abstractmethod
    def stock_pantry(self, p: PantryItem) -> None:
        ...

    @abstractmethod
    def use(self, pantry_item_id: str, quantity: float) -> None:
        ...

    @abstractmethod
    def pantry(self) -> List[PantryItem]:
        ...

    @abstractmethod
    def expiring(self, before: datetime) -> List[PantryItem]:
        ...


class InMemoryFoodBoard(IFoodBoard):
    """Thread-safe in-memory :class:`IFoodBoard`."""

    def __init__(self) -> None:
        self._recipes: Dict[str, Recipe] = {}
        self._logs: List[MealLog] = []
        self._pantry: Dict[str, PantryItem] = {}
        self._lock = threading.Lock()

    def add_recipe(self, r: Recipe) -> None:
        if r is None:
            raise ValueError("recipe must not be None")
        with self._lock:
            self._recipes[r.recipe_id] = r

    def get_recipe(self, id: str) -> Optional[Recipe]:
        with self._lock:
            return self._recipes.get(id)

    def search_by_ingredient(self, ingredient: str) -> List[Recipe]:
        if ingredient is None or ingredient.strip() == "":
            raise ValueError("ingredient required")
        needle = ingredient.casefold()
        with self._lock:
            return [
                r
                for r in self._recipes.values()
                if any(needle in i.casefold() for i in r.ingredients)
            ]

    def log(self, m: MealLog) -> None:
        if m is None:
            raise ValueError("meal log must not be None")
        with self._lock:
            self._logs.append(m)

    def logs_since(self, user_id: str, since: datetime) -> List[MealLog]:
        with self._lock:
            items = [
                l for l in self._logs if l.user_id == user_id and l.at_utc >= since
            ]
        items.sort(key=lambda l: l.at_utc)
        return items

    def stock_pantry(self, p: PantryItem) -> None:
        if p is None:
            raise ValueError("pantry item must not be None")
        with self._lock:
            self._pantry[p.pantry_item_id] = p

    def use(self, pantry_item_id: str, quantity: float) -> None:
        with self._lock:
            p = self._pantry.get(pantry_item_id)
            if p is None:
                raise RuntimeError(f"Unknown pantry item {pantry_item_id}")
            new_qty = max(0.0, p.quantity - quantity)
            self._pantry[pantry_item_id] = PantryItem(
                p.pantry_item_id, p.name, new_qty, p.unit, p.best_before
            )

    def pantry(self) -> List[PantryItem]:
        with self._lock:
            return [p for p in self._pantry.values() if p.quantity > 0]

    def expiring(self, before: datetime) -> List[PantryItem]:
        with self._lock:
            items = [
                p
                for p in self._pantry.values()
                if p.best_before is not None and p.best_before <= before
            ]
        items.sort(key=lambda p: p.best_before)  # type: ignore[arg-type,return-value]
        return items
