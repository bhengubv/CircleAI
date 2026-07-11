// food_board_test.go
//
// Verifies the CircleAI.Food port (food_board.go): recipe add/get, ingredient
// search (case-insensitive), meal-log since, and pantry stock/use/expiring.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestFood_RecipesAndSearch(t *testing.T) {
	b := circleai.NewInMemoryFoodBoard()
	b.AddRecipe(circleai.Recipe{RecipeId: "r1", Title: "Pasta", Ingredients: []string{"Tomato", "Basil"}, Servings: 2, PrepMinutes: 20})
	b.AddRecipe(circleai.Recipe{RecipeId: "r2", Title: "Salad", Ingredients: []string{"Lettuce", "tomato"}, Servings: 1, PrepMinutes: 10})
	if got, ok := b.GetRecipe("r1"); !ok || got.Title != "Pasta" {
		t.Fatalf("get recipe = %+v ok=%v", got, ok)
	}
	hits := b.SearchByIngredient("TOMATO")
	if len(hits) != 2 || hits[0].RecipeId != "r1" || hits[1].RecipeId != "r2" {
		t.Fatalf("ingredient search failed: %+v", hits)
	}
}

func TestFood_PantryAndExpiring(t *testing.T) {
	b := circleai.NewInMemoryFoodBoard()
	early := time.Date(2026, 1, 10, 0, 0, 0, 0, time.UTC)
	late := time.Date(2026, 3, 1, 0, 0, 0, 0, time.UTC)
	b.StockPantry(circleai.PantryItem{PantryItemId: "p1", Name: "Milk", Quantity: 2, Unit: "L", BestBefore: &early})
	b.StockPantry(circleai.PantryItem{PantryItemId: "p2", Name: "Rice", Quantity: 5, Unit: "kg", BestBefore: &late})
	b.StockPantry(circleai.PantryItem{PantryItemId: "p3", Name: "Salt", Quantity: 1, Unit: "kg"})

	if err := b.Use("p1", 3); err != nil {
		t.Fatalf("use: %v", err)
	}
	// p1 floored to 0 -> excluded from Pantry.
	pantry := b.Pantry()
	if len(pantry) != 2 || pantry[0].PantryItemId != "p2" || pantry[1].PantryItemId != "p3" {
		t.Fatalf("pantry (qty>0, sorted) failed: %+v", pantry)
	}
	if err := b.Use("ghost", 1); err == nil {
		t.Fatalf("use unknown item must error")
	}

	exp := b.Expiring(time.Date(2026, 2, 1, 0, 0, 0, 0, time.UTC))
	if len(exp) != 1 || exp[0].PantryItemId != "p1" {
		t.Fatalf("expiring failed: %+v", exp)
	}

	// Meal logs since.
	now := time.Date(2026, 7, 8, 12, 0, 0, 0, time.UTC)
	b.Log(circleai.MealLog{LogId: "m1", UserId: "u1", RecipeId: "r1", AtUtc: now, Servings: 1})
	b.Log(circleai.MealLog{LogId: "m2", UserId: "u1", RecipeId: "r2", AtUtc: now.Add(-72 * time.Hour), Servings: 2})
	logs := b.LogsSince("u1", now.Add(-48*time.Hour))
	if len(logs) != 1 || logs[0].LogId != "m1" {
		t.Fatalf("logs-since failed: %+v", logs)
	}
}
