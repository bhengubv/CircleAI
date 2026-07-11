/*
 * test_food.c — CircleAI.Food (C11 port) verification against FoodPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include "circle_ai/circle_ai.h"

static void test_recipes(void) {
    ca_food_board_t *b = ca_food_board_create();
    assert(b);
    assert(ca_food_board_add_recipe(b, NULL) == -1);

    char *ing1[] = { (char *)"Eggs", (char *)"Flour", (char *)"Milk" };
    char *steps1[] = { (char *)"Mix", (char *)"Cook" };
    ca_food_recipe_t r1; memset(&r1, 0, sizeof(r1));
    r1.recipe_id = (char *)"r1"; r1.title = (char *)"Pancakes";
    r1.ingredients = ing1; r1.ingredient_count = 3;
    r1.steps = steps1; r1.step_count = 2; r1.servings = 4; r1.prep_minutes = 15;

    char *ing2[] = { (char *)"Tomato", (char *)"Basil" };
    ca_food_recipe_t r2; memset(&r2, 0, sizeof(r2));
    r2.recipe_id = (char *)"r2"; r2.title = (char *)"Salad";
    r2.ingredients = ing2; r2.ingredient_count = 2; r2.servings = 2; r2.prep_minutes = 5;
    assert(ca_food_board_add_recipe(b, &r1) == 0);
    assert(ca_food_board_add_recipe(b, &r2) == 0);

    ca_food_recipe_t got;
    assert(ca_food_board_get_recipe(b, "r1", &got));
    assert(got.ingredient_count == 3 && strcmp(got.ingredients[0], "Eggs") == 0);
    assert(got.step_count == 2 && got.servings == 4);
    ca_food_recipe_free(&got);
    assert(!ca_food_board_get_recipe(b, "nope", &got));

    /* SearchByIngredient "egg" (CI) -> r1. */
    size_t n = 0;
    ca_food_recipe_t *hits = ca_food_board_search_by_ingredient(b, "egg", &n);
    assert(n == 1 && strcmp(hits[0].recipe_id, "r1") == 0);
    ca_food_recipe_free_array(hits, n);
    /* blank throws -> SIZE_MAX. */
    assert(ca_food_board_search_by_ingredient(b, "  ", &n) == NULL && n == (size_t)-1);
    /* no match. */
    hits = ca_food_board_search_by_ingredient(b, "zzz", &n);
    assert(hits == NULL && n == 0);

    ca_food_board_destroy(b);
    printf("  recipes: ok\n");
}

static void test_logs_pantry(void) {
    ca_food_board_t *b = ca_food_board_create();

    ca_food_meal_log_t m1; memset(&m1, 0, sizeof(m1));
    m1.log_id = (char *)"m1"; m1.user_id = (char *)"u1"; m1.recipe_id = (char *)"r1";
    m1.at_utc_ms = 300; m1.servings = 1;
    ca_food_meal_log_t m2; memset(&m2, 0, sizeof(m2));
    m2.log_id = (char *)"m2"; m2.user_id = (char *)"u1"; m2.recipe_id = (char *)"r2";
    m2.at_utc_ms = 100; m2.servings = 2;
    assert(ca_food_board_log(b, &m1) == 0);
    assert(ca_food_board_log(b, &m2) == 0);

    /* LogsSince(50) ascending: m2(100), m1(300). */
    size_t n = 0;
    ca_food_meal_log_t *ls = ca_food_board_logs_since(b, "u1", 50, &n);
    assert(n == 2 && strcmp(ls[0].log_id, "m2") == 0 && strcmp(ls[1].log_id, "m1") == 0);
    ca_food_meal_log_free_array(ls, n);
    /* since 200 -> m1 only. */
    ls = ca_food_board_logs_since(b, "u1", 200, &n);
    assert(n == 1 && strcmp(ls[0].log_id, "m1") == 0);
    ca_food_meal_log_free_array(ls, n);

    /* Pantry. */
    ca_food_pantry_item_t p1; memset(&p1, 0, sizeof(p1));
    p1.pantry_item_id = (char *)"p1"; p1.name = (char *)"Flour"; p1.quantity = 2.0;
    p1.unit = (char *)"kg"; p1.has_best_before = true; p1.best_before_ms = 500;
    ca_food_pantry_item_t p2; memset(&p2, 0, sizeof(p2));
    p2.pantry_item_id = (char *)"p2"; p2.name = (char *)"Salt"; p2.quantity = 1.0;
    p2.unit = (char *)"kg"; p2.has_best_before = false;
    ca_food_pantry_item_t p3; memset(&p3, 0, sizeof(p3));
    p3.pantry_item_id = (char *)"p3"; p3.name = (char *)"Milk"; p3.quantity = 1.0;
    p3.unit = (char *)"L"; p3.has_best_before = true; p3.best_before_ms = 200;
    assert(ca_food_board_stock_pantry(b, &p1) == 0);
    assert(ca_food_board_stock_pantry(b, &p2) == 0);
    assert(ca_food_board_stock_pantry(b, &p3) == 0);

    assert(ca_food_board_use(b, "nope", 1.0) == -2);
    assert(ca_food_board_use(b, "p1", 0.5) == 0);   /* 2.0 -> 1.5 */
    assert(ca_food_board_use(b, "p2", 5.0) == 0);   /* 1.0 -> 0 (drops out) */

    ca_food_pantry_item_t *pan = ca_food_board_pantry(b, &n);
    assert(n == 2); /* p1(1.5), p3(1.0); p2 at 0 excluded */
    assert(strcmp(pan[0].pantry_item_id, "p1") == 0 && pan[0].quantity == 1.5);
    assert(strcmp(pan[1].pantry_item_id, "p3") == 0);
    ca_food_pantry_item_free_array(pan, n);

    /* Expiring(before=300): p3(200) only [p1 at 500 too late, p2 has none];
     * ordered by BestBefore. */
    ca_food_pantry_item_t *exp = ca_food_board_expiring(b, 300, &n);
    assert(n == 1 && strcmp(exp[0].pantry_item_id, "p3") == 0);
    ca_food_pantry_item_free_array(exp, n);
    /* before=600: p3(200), p1(500) sorted asc. */
    exp = ca_food_board_expiring(b, 600, &n);
    assert(n == 2 && strcmp(exp[0].pantry_item_id, "p3") == 0 &&
           strcmp(exp[1].pantry_item_id, "p1") == 0);
    ca_food_pantry_item_free_array(exp, n);

    ca_food_board_destroy(b);
    printf("  logs_pantry: ok\n");
}

int main(void) {
    test_recipes();
    test_logs_pantry();
    printf("test_food: all assertions passed\n");
    return 0;
}
