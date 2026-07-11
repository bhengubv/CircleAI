// food_board.go
//
// Ports the CircleAI.Food primitive vertical (FoodPrimitives.cs):
//   Recipe / MealLog / PantryItem (records) -> value structs
//   IFoodBoard               -> FoodBoard interface (I-prefix dropped)
//   InMemoryFoodBoard        -> InMemoryFoodBoard
//
// The FoodDomainContext / FoodCompanionAdapter (LLM glue) are out of scope.
//
// DETERMINISM: recipes/pantry mirror ConcurrentDictionary in C# (no defined
// order); SearchByIngredient and Pantry sort by their id/key for stable output.
// LogsSince orders by AtUtc ascending. Expiring orders by BestBefore ascending
// (ties broken by PantryItemId). PantryItem.BestBefore is a *time.Time to model
// the C# nullable DateTime.

package circleai

import (
	"errors"
	"sort"
	"strings"
	"sync"
	"time"
)

// Recipe is a recipe. Ports the Recipe record. Ingredients and Steps mirror the
// C# IReadOnlyList<string>.
type Recipe struct {
	RecipeId    string
	Title       string
	Ingredients []string
	Steps       []string
	Servings    int
	PrepMinutes int
}

// MealLog is a logged meal. Ports the MealLog record.
type MealLog struct {
	LogId    string
	UserId   string
	RecipeId string
	AtUtc    time.Time
	Servings int
}

// PantryItem is a pantry stock item. Ports the PantryItem record. BestBefore is
// a *time.Time to model the C# nullable DateTime.
type PantryItem struct {
	PantryItemId string
	Name         string
	Quantity     float64
	Unit         string
	BestBefore   *time.Time
}

// FoodBoard is the recipes/meal-log/pantry board. Ports IFoodBoard.
type FoodBoard interface {
	AddRecipe(r Recipe)
	GetRecipe(id string) (Recipe, bool)
	// SearchByIngredient finds recipes whose ingredients contain the substring
	// (case-insensitive). Panics on blank input, matching the C# ArgumentException.
	SearchByIngredient(ingredient string) []Recipe
	Log(m MealLog)
	// LogsSince lists a user's meal logs at/after since, oldest-first.
	LogsSince(userId string, since time.Time) []MealLog
	StockPantry(p PantryItem)
	// Use decrements a pantry item's quantity (floored at 0); errors on unknown id.
	Use(pantryItemId string, quantity float64) error
	// Pantry lists items with quantity > 0.
	Pantry() []PantryItem
	// Expiring lists items with a BestBefore at/before the given time.
	Expiring(before time.Time) []PantryItem
}

// InMemoryFoodBoard is a concurrency-safe in-memory FoodBoard. Ports
// InMemoryFoodBoard.
type InMemoryFoodBoard struct {
	mu      sync.Mutex
	recipes map[string]Recipe
	logs    []MealLog
	pantry  map[string]PantryItem
}

// NewInMemoryFoodBoard constructs an empty board.
func NewInMemoryFoodBoard() *InMemoryFoodBoard {
	return &InMemoryFoodBoard{
		recipes: make(map[string]Recipe),
		pantry:  make(map[string]PantryItem),
	}
}

// AddRecipe stores (or replaces by RecipeId) a recipe. Ports AddRecipe.
func (b *InMemoryFoodBoard) AddRecipe(r Recipe) {
	b.mu.Lock()
	b.recipes[r.RecipeId] = r
	b.mu.Unlock()
}

// GetRecipe returns the recipe for id, or (zero,false). Ports GetRecipe.
func (b *InMemoryFoodBoard) GetRecipe(id string) (Recipe, bool) {
	b.mu.Lock()
	r, ok := b.recipes[id]
	b.mu.Unlock()
	return r, ok
}

// SearchByIngredient finds recipes containing the ingredient substring
// (case-insensitive), sorted by RecipeId. Ports SearchByIngredient.
func (b *InMemoryFoodBoard) SearchByIngredient(ingredient string) []Recipe {
	if strings.TrimSpace(ingredient) == "" {
		panic("ingredient required")
	}
	needle := strings.ToLower(ingredient)
	b.mu.Lock()
	out := make([]Recipe, 0)
	for _, r := range b.recipes {
		for _, ing := range r.Ingredients {
			if strings.Contains(strings.ToLower(ing), needle) {
				out = append(out, r)
				break
			}
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].RecipeId < out[j].RecipeId })
	return out
}

// Log appends a meal log. Ports Log.
func (b *InMemoryFoodBoard) Log(m MealLog) {
	b.mu.Lock()
	b.logs = append(b.logs, m)
	b.mu.Unlock()
}

// LogsSince lists a user's meal logs at/after since, oldest-first. Ports
// LogsSince.
func (b *InMemoryFoodBoard) LogsSince(userId string, since time.Time) []MealLog {
	b.mu.Lock()
	out := make([]MealLog, 0)
	for _, l := range b.logs {
		if l.UserId == userId && !l.AtUtc.Before(since) {
			out = append(out, l)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].AtUtc.Before(out[j].AtUtc) })
	return out
}

// StockPantry stores (or replaces by PantryItemId) a pantry item. Ports
// StockPantry.
func (b *InMemoryFoodBoard) StockPantry(p PantryItem) {
	b.mu.Lock()
	b.pantry[p.PantryItemId] = p
	b.mu.Unlock()
}

// Use decrements a pantry item's quantity, floored at 0. Ports Use (throws on
// unknown id -> error).
func (b *InMemoryFoodBoard) Use(pantryItemId string, quantity float64) error {
	b.mu.Lock()
	defer b.mu.Unlock()
	p, ok := b.pantry[pantryItemId]
	if !ok {
		return errors.New("Unknown pantry item " + pantryItemId)
	}
	q := p.Quantity - quantity
	if q < 0 {
		q = 0
	}
	p.Quantity = q
	b.pantry[pantryItemId] = p
	return nil
}

// Pantry lists items with quantity > 0, sorted by PantryItemId. Ports Pantry.
func (b *InMemoryFoodBoard) Pantry() []PantryItem {
	b.mu.Lock()
	out := make([]PantryItem, 0)
	for _, p := range b.pantry {
		if p.Quantity > 0 {
			out = append(out, p)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool { return out[i].PantryItemId < out[j].PantryItemId })
	return out
}

// Expiring lists items with a BestBefore at/before before, ordered by BestBefore
// ascending (ties by PantryItemId). Ports Expiring.
func (b *InMemoryFoodBoard) Expiring(before time.Time) []PantryItem {
	b.mu.Lock()
	out := make([]PantryItem, 0)
	for _, p := range b.pantry {
		if p.BestBefore != nil && !p.BestBefore.After(before) {
			out = append(out, p)
		}
	}
	b.mu.Unlock()
	sort.SliceStable(out, func(i, j int) bool {
		if !out[i].BestBefore.Equal(*out[j].BestBefore) {
			return out[i].BestBefore.Before(*out[j].BestBefore)
		}
		return out[i].PantryItemId < out[j].PantryItemId
	})
	return out
}

// Interface guard.
var _ FoodBoard = (*InMemoryFoodBoard)(nil)
