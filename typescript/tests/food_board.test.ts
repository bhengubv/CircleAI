// food_board.test.ts
// Verifies the CircleAI.Food port: recipes + ingredient search, meal logs,
// pantry usage + expiry.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import { InMemoryFoodBoard, FoodDomainContext, recipe, mealLog, pantryItem } from "../src/food/index";

describe("InMemoryFoodBoard", () => {
  it("adds recipes and searches by ingredient (case-insensitive substring)", () => {
    const b = new InMemoryFoodBoard();
    b.addRecipe(recipe("r1", "Tomato Soup", ["Tomato", "Basil"], ["boil"], 4, 20));
    b.addRecipe(recipe("r2", "Omelette", ["Egg", "Cheese"], ["fry"], 1, 10));
    assert.equal(b.getRecipe("r1")?.title, "Tomato Soup");
    assert.deepEqual(
      b.searchByIngredient("tomato").map((r) => r.recipeId),
      ["r1"],
    );
    assert.equal(b.searchByIngredient("xyz").length, 0);
  });

  it("searchByIngredient throws on blank", () => {
    const b = new InMemoryFoodBoard();
    assert.throws(() => b.searchByIngredient("   "), /ingredient required/);
  });

  it("logs meals and returns them since a cutoff, oldest-first", () => {
    const b = new InMemoryFoodBoard();
    b.log(mealLog("m1", "u1", "r1", new Date("2026-01-01T12:00:00Z"), 1));
    b.log(mealLog("m2", "u1", "r2", new Date("2026-01-03T12:00:00Z"), 2));
    b.log(mealLog("m3", "u2", "r1", new Date("2026-01-03T12:00:00Z"), 1));
    assert.deepEqual(
      b.logsSince("u1", new Date("2026-01-02T00:00:00Z")).map((m) => m.logId),
      ["m2"],
    );
  });

  it("stocks the pantry, decrements on use (floored at 0), and lists positive items", () => {
    const b = new InMemoryFoodBoard();
    b.stockPantry(pantryItem("p1", "Flour", 1000, "g", null));
    b.stockPantry(pantryItem("p2", "Sugar", 500, "g", null));
    b.use("p1", 300);
    b.use("p2", 999); // over-use → floored to 0
    assert.equal(b.getRecipe("nope"), undefined);
    const remaining = b.pantry().map((p) => [p.pantryItemId, p.quantity]);
    assert.deepEqual(remaining, [["p1", 700]]);
  });

  it("use throws on unknown pantry item", () => {
    const b = new InMemoryFoodBoard();
    assert.throws(() => b.use("ghost", 1), /Unknown pantry item ghost/);
  });

  it("lists expiring items before a date, earliest-first", () => {
    const b = new InMemoryFoodBoard();
    b.stockPantry(pantryItem("p1", "Milk", 1, "l", new Date("2026-01-10T00:00:00Z")));
    b.stockPantry(pantryItem("p2", "Yoghurt", 1, "l", new Date("2026-01-05T00:00:00Z")));
    b.stockPantry(pantryItem("p3", "Salt", 1, "kg", null)); // no expiry
    assert.deepEqual(
      b.expiring(new Date("2026-01-08T00:00:00Z")).map((p) => p.pantryItemId),
      ["p2"],
    );
    assert.deepEqual(
      b.expiring(new Date("2026-01-20T00:00:00Z")).map((p) => p.pantryItemId),
      ["p2", "p1"],
    );
  });

  it("domain context exposes prompt + compliance + tools", () => {
    assert.ok(FoodDomainContext.systemPromptSnippet.includes("[DOMAIN: Food]"));
    assert.deepEqual(FoodDomainContext.complianceFlags, ["Food_Safety_Act", "POPIA"]);
    assert.deepEqual(FoodDomainContext.suggestedTools, ["recipe_tools", "nutrition_db", "shopping_list", "web_search"]);
  });
});
