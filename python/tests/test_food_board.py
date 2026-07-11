"""test_food_board.py — CircleAI.Food port.

Covers InMemoryFoodBoard (recipe add/get, case-insensitive ingredient search,
meal logs, pantry stock/use/expiry) and FoodDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timezone

import pytest

from circle_ai import (
    FoodDomainContext,
    IFoodBoard,
    InMemoryFoodBoard,
    MealLog,
    PantryItem,
    Recipe,
)


def test_board_is_ifoodboard():
    assert isinstance(InMemoryFoodBoard(), IFoodBoard)


def test_search_by_ingredient_case_insensitive_substring():
    b = InMemoryFoodBoard()
    b.add_recipe(Recipe("r1", "Omelette", ["Eggs", "Butter"], ["beat", "fry"], 1, 5))
    b.add_recipe(Recipe("r2", "Salad", ["Lettuce", "Tomato"], ["chop"], 2, 10))
    hits = b.search_by_ingredient("egg")
    assert {r.recipe_id for r in hits} == {"r1"}


def test_search_blank_raises():
    with pytest.raises(ValueError):
        InMemoryFoodBoard().search_by_ingredient("   ")


def test_logs_since_ordered():
    b = InMemoryFoodBoard()
    since = datetime(2026, 1, 1, tzinfo=timezone.utc)
    b.log(MealLog("l2", "u", "r1", datetime(2026, 1, 3, tzinfo=timezone.utc), 1))
    b.log(MealLog("l1", "u", "r1", datetime(2026, 1, 2, tzinfo=timezone.utc), 1))
    b.log(MealLog("old", "u", "r1", datetime(2025, 12, 1, tzinfo=timezone.utc), 1))
    got = b.logs_since("u", since)
    assert [l.log_id for l in got] == ["l1", "l2"]


def test_pantry_use_clamps_and_hides_zero():
    b = InMemoryFoodBoard()
    b.stock_pantry(PantryItem("p1", "flour", 2.0, "kg", None))
    b.use("p1", 5.0)  # over-consume -> clamps to 0
    assert b.pantry() == []  # zero-quantity items are hidden


def test_use_unknown_raises():
    with pytest.raises(RuntimeError):
        InMemoryFoodBoard().use("nope", 1.0)


def test_expiring_before_date_ordered():
    b = InMemoryFoodBoard()
    b.stock_pantry(PantryItem("a", "milk", 1.0, "l", datetime(2026, 1, 10, tzinfo=timezone.utc)))
    b.stock_pantry(PantryItem("b", "yog", 1.0, "l", datetime(2026, 1, 5, tzinfo=timezone.utc)))
    b.stock_pantry(PantryItem("c", "rice", 1.0, "kg", None))  # no expiry -> excluded
    got = b.expiring(datetime(2026, 1, 20, tzinfo=timezone.utc))
    assert [p.pantry_item_id for p in got] == ["b", "a"]


def test_food_domain_context():
    assert FoodDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Food]")
    assert list(FoodDomainContext.ComplianceFlags) == ["Food_Safety_Act", "POPIA"]
