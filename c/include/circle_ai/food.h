#ifndef CIRCLE_AI_FOOD_H
#define CIRCLE_AI_FOOD_H

/*
 * food.h — CircleAI.Food (C11 port of FoodPrimitives.cs).
 *
 *   Records : Recipe(RecipeId, Title, IReadOnlyList<string> Ingredients,
 *                    IReadOnlyList<string> Steps, int Servings, int PrepMinutes);
 *             MealLog(LogId, UserId, RecipeId, DateTimeOffset AtUtc, int Servings);
 *             PantryItem(PantryItemId, Name, double Quantity, string Unit,
 *                    DateTime? BestBefore).
 *   Board   : IFoodBoard -> InMemoryFoodBoard
 *               AddRecipe (RecipeId keyed), GetRecipe(id), SearchByIngredient
 *               (any ingredient contains q, OrdinalIgnoreCase; throws on blank),
 *               Log (appends), LogsSince(userId, since) ascending by AtUtc,
 *               StockPantry (PantryItemId keyed), Use(id, qty) subtracts flooring
 *               at 0 (throws on unknown), Pantry() [Quantity > 0], Expiring(before)
 *               [BestBefore set and <= before] ordered by BestBefore asc.
 *
 * DateTimeOffset/DateTime as Unix ms UTC. BestBefore optional via has_best_before.
 * Pantry / search iterate the store in insertion order (deterministic).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields, deep
 * copy getters, errors via NULL / count SIZE_MAX. Linear arrays, no pthreads.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Recipe(RecipeId, Title, Ingredients[], Steps[], int Servings, int PrepMinutes). */
typedef struct {
    char   *recipe_id;         /* owned, non-null */
    char   *title;             /* owned, non-null */
    char  **ingredients;       /* owned array of owned strings (may be NULL if 0) */
    size_t  ingredient_count;
    char  **steps;             /* owned array of owned strings (may be NULL if 0) */
    size_t  step_count;
    int     servings;
    int     prep_minutes;
} ca_food_recipe_t;

void ca_food_recipe_free(ca_food_recipe_t *r);
void ca_food_recipe_free_array(ca_food_recipe_t *arr, size_t count);

/* MealLog(LogId, UserId, RecipeId, DateTimeOffset AtUtc, int Servings). */
typedef struct {
    char   *log_id;            /* owned, non-null */
    char   *user_id;           /* owned, non-null */
    char   *recipe_id;         /* owned, non-null */
    int64_t at_utc_ms;
    int     servings;
} ca_food_meal_log_t;

void ca_food_meal_log_free(ca_food_meal_log_t *m);
void ca_food_meal_log_free_array(ca_food_meal_log_t *arr, size_t count);

/* PantryItem(PantryItemId, Name, double Quantity, string Unit,
 * DateTime? BestBefore). */
typedef struct {
    char   *pantry_item_id;    /* owned, non-null */
    char   *name;              /* owned, non-null */
    double  quantity;
    char   *unit;              /* owned, non-null */
    bool    has_best_before;   /* false == C# null BestBefore */
    int64_t best_before_ms;    /* valid only when has_best_before */
} ca_food_pantry_item_t;

void ca_food_pantry_item_free(ca_food_pantry_item_t *p);
void ca_food_pantry_item_free_array(ca_food_pantry_item_t *arr, size_t count);

typedef struct ca_food_board ca_food_board_t;

ca_food_board_t *ca_food_board_create(void); /* NULL on OOM */
void ca_food_board_destroy(ca_food_board_t *b);

/* AddRecipe(r) — RecipeId keyed set. 0 / -1. */
int ca_food_board_add_recipe(ca_food_board_t *b, const ca_food_recipe_t *r);

/* GetRecipe(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_food_board_get_recipe(const ca_food_board_t *b, const char *id,
                              ca_food_recipe_t *out);

/* SearchByIngredient(ingredient) -> fresh owned array (insertion order) of recipes
 * with any ingredient containing `ingredient` (OrdinalIgnoreCase). ingredient must
 * be non-null / non-whitespace (SIZE_MAX on blank / bad args). NULL + 0 empty. */
ca_food_recipe_t *ca_food_board_search_by_ingredient(const ca_food_board_t *b,
                                                     const char *ingredient,
                                                     size_t *out_count);

/* Log(m) — appends. 0 / -1. */
int ca_food_board_log(ca_food_board_t *b, const ca_food_meal_log_t *m);

/* LogsSince(userId, since_ms) -> fresh owned array ascending by AtUtc.
 * NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_food_meal_log_t *ca_food_board_logs_since(const ca_food_board_t *b,
                                             const char *user_id, int64_t since_ms,
                                             size_t *out_count);

/* StockPantry(p) — PantryItemId keyed set. 0 / -1. */
int ca_food_board_stock_pantry(ca_food_board_t *b,
                               const ca_food_pantry_item_t *p);

/* Use(id, qty) — Quantity = max(0, Quantity - qty). 0 on success, -1 on bad args,
 * -2 when the item is unknown (C# InvalidOperationException). */
int ca_food_board_use(ca_food_board_t *b, const char *pantry_item_id,
                      double quantity);

/* Pantry() -> fresh owned array (insertion order) of items with Quantity > 0.
 * NULL + 0 empty; NULL + SIZE_MAX on error. */
ca_food_pantry_item_t *ca_food_board_pantry(const ca_food_board_t *b,
                                            size_t *out_count);

/* Expiring(before_ms) -> fresh owned array of items with BestBefore set and
 * <= before, ordered by BestBefore asc. NULL + 0 empty; NULL + SIZE_MAX error. */
ca_food_pantry_item_t *ca_food_board_expiring(const ca_food_board_t *b,
                                              int64_t before_ms,
                                              size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_FOOD_H */
