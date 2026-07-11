/*
 * food.c — CircleAI.Food (C11 port of FoodPrimitives.cs).
 *
 * InMemoryFoodBoard: recipes (RecipeId keyed), meal logs (append list), pantry
 * (PantryItemId keyed). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/food.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_food_recipe_free(ca_food_recipe_t *r) {
    if (!r) return;
    free(r->recipe_id);
    free(r->title);
    cab_strv_free(r->ingredients, r->ingredient_count);
    cab_strv_free(r->steps, r->step_count);
    r->recipe_id = r->title = NULL;
    r->ingredients = r->steps = NULL;
    r->ingredient_count = r->step_count = 0;
}
void ca_food_recipe_free_array(ca_food_recipe_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_food_recipe_free(&arr[i]);
    free(arr);
}

static bool recipe_copy(ca_food_recipe_t *dst, const ca_food_recipe_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->recipe_id    = cab_strdup_empty(src->recipe_id);
    dst->title        = cab_strdup_empty(src->title);
    dst->servings     = src->servings;
    dst->prep_minutes = src->prep_minutes;
    bool ok = dst->recipe_id && dst->title;
    if (ok) ok = cab_strv_copy(&dst->ingredients, src->ingredients,
                               src->ingredient_count);
    if (ok) dst->ingredient_count = src->ingredient_count;
    if (ok) ok = cab_strv_copy(&dst->steps, src->steps, src->step_count);
    if (ok) dst->step_count = src->step_count;
    if (!ok) { ca_food_recipe_free(dst); return false; }
    return true;
}

void ca_food_meal_log_free(ca_food_meal_log_t *m) {
    if (!m) return;
    free(m->log_id);
    free(m->user_id);
    free(m->recipe_id);
    m->log_id = m->user_id = m->recipe_id = NULL;
}
void ca_food_meal_log_free_array(ca_food_meal_log_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_food_meal_log_free(&arr[i]);
    free(arr);
}

static bool meal_log_copy(ca_food_meal_log_t *dst, const ca_food_meal_log_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->log_id    = cab_strdup_empty(src->log_id);
    dst->user_id   = cab_strdup_empty(src->user_id);
    dst->recipe_id = cab_strdup_empty(src->recipe_id);
    dst->at_utc_ms = src->at_utc_ms;
    dst->servings  = src->servings;
    if (!dst->log_id || !dst->user_id || !dst->recipe_id) {
        ca_food_meal_log_free(dst);
        return false;
    }
    return true;
}

void ca_food_pantry_item_free(ca_food_pantry_item_t *p) {
    if (!p) return;
    free(p->pantry_item_id);
    free(p->name);
    free(p->unit);
    p->pantry_item_id = p->name = p->unit = NULL;
}
void ca_food_pantry_item_free_array(ca_food_pantry_item_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_food_pantry_item_free(&arr[i]);
    free(arr);
}

static bool pantry_copy(ca_food_pantry_item_t *dst,
                        const ca_food_pantry_item_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->pantry_item_id  = cab_strdup_empty(src->pantry_item_id);
    dst->name            = cab_strdup_empty(src->name);
    dst->quantity        = src->quantity;
    dst->unit            = cab_strdup_empty(src->unit);
    dst->has_best_before = src->has_best_before;
    dst->best_before_ms  = src->has_best_before ? src->best_before_ms : 0;
    if (!dst->pantry_item_id || !dst->name || !dst->unit) {
        ca_food_pantry_item_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_food_board {
    ca_food_recipe_t      *recipes;
    size_t                 r_count, r_cap;
    ca_food_meal_log_t    *logs;
    size_t                 l_count, l_cap;
    ca_food_pantry_item_t *pantry;
    size_t                 p_count, p_cap;
};

ca_food_board_t *ca_food_board_create(void) {
    return (ca_food_board_t *)calloc(1, sizeof(ca_food_board_t));
}
void ca_food_board_destroy(ca_food_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->r_count; ++i) ca_food_recipe_free(&b->recipes[i]);
    for (size_t i = 0; i < b->l_count; ++i) ca_food_meal_log_free(&b->logs[i]);
    for (size_t i = 0; i < b->p_count; ++i) ca_food_pantry_item_free(&b->pantry[i]);
    free(b->recipes);
    free(b->logs);
    free(b->pantry);
    free(b);
}

int ca_food_board_add_recipe(ca_food_board_t *b, const ca_food_recipe_t *r) {
    if (!b || !r) return -1;
    for (size_t i = 0; i < b->r_count; ++i) {
        if (cab_ord_eq(b->recipes[i].recipe_id, r->recipe_id)) {
            ca_food_recipe_t copy;
            if (!recipe_copy(&copy, r)) return -1;
            ca_food_recipe_free(&b->recipes[i]);
            b->recipes[i] = copy;
            return 0;
        }
    }
    ca_food_recipe_t copy;
    if (!recipe_copy(&copy, r)) return -1;
    if (b->r_count == b->r_cap) {
        size_t nc = b->r_cap ? b->r_cap * 2 : 4;
        void *n = realloc(b->recipes, nc * sizeof(*b->recipes));
        if (!n) { ca_food_recipe_free(&copy); return -1; }
        b->recipes = (ca_food_recipe_t *)n;
        b->r_cap = nc;
    }
    b->recipes[b->r_count++] = copy;
    return 0;
}

bool ca_food_board_get_recipe(const ca_food_board_t *b, const char *id,
                              ca_food_recipe_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->r_count; ++i)
        if (cab_ord_eq(b->recipes[i].recipe_id, id))
            return recipe_copy(out, &b->recipes[i]);
    return false;
}

/* Does any ingredient contain q (OrdinalIgnoreCase)? */
static bool recipe_has_ingredient(const ca_food_recipe_t *r, const char *q) {
    for (size_t i = 0; i < r->ingredient_count; ++i)
        if (cab_ci_contains(r->ingredients[i], q)) return true;
    return false;
}

ca_food_recipe_t *ca_food_board_search_by_ingredient(const ca_food_board_t *b,
                                                     const char *ingredient,
                                                     size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || cab_is_ws(ingredient)) { *out_count = (size_t)-1; return NULL; }
    if (b->r_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->r_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->r_count; ++i)
        if (recipe_has_ingredient(&b->recipes[i], ingredient)) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_food_recipe_t *out = (ca_food_recipe_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!recipe_copy(&out[i], &b->recipes[idx[i]])) {
            ca_food_recipe_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_food_board_log(ca_food_board_t *b, const ca_food_meal_log_t *m) {
    if (!b || !m) return -1;
    ca_food_meal_log_t copy;
    if (!meal_log_copy(&copy, m)) return -1;
    if (b->l_count == b->l_cap) {
        size_t nc = b->l_cap ? b->l_cap * 2 : 4;
        void *n = realloc(b->logs, nc * sizeof(*b->logs));
        if (!n) { ca_food_meal_log_free(&copy); return -1; }
        b->logs = (ca_food_meal_log_t *)n;
        b->l_cap = nc;
    }
    b->logs[b->l_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected indices by AtUtc. */
static void log_sort_asc(const ca_food_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->logs[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->logs[idx[j - 1]].at_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_food_meal_log_t *ca_food_board_logs_since(const ca_food_board_t *b,
                                             const char *user_id, int64_t since_ms,
                                             size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !user_id) { *out_count = (size_t)-1; return NULL; }
    if (b->l_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->l_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->l_count; ++i) {
        const ca_food_meal_log_t *l = &b->logs[i];
        if (cab_ord_eq(l->user_id, user_id) && l->at_utc_ms >= since_ms)
            idx[n++] = i;
    }
    log_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_food_meal_log_t *out = (ca_food_meal_log_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!meal_log_copy(&out[i], &b->logs[idx[i]])) {
            ca_food_meal_log_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_food_board_stock_pantry(ca_food_board_t *b,
                               const ca_food_pantry_item_t *p) {
    if (!b || !p) return -1;
    for (size_t i = 0; i < b->p_count; ++i) {
        if (cab_ord_eq(b->pantry[i].pantry_item_id, p->pantry_item_id)) {
            ca_food_pantry_item_t copy;
            if (!pantry_copy(&copy, p)) return -1;
            ca_food_pantry_item_free(&b->pantry[i]);
            b->pantry[i] = copy;
            return 0;
        }
    }
    ca_food_pantry_item_t copy;
    if (!pantry_copy(&copy, p)) return -1;
    if (b->p_count == b->p_cap) {
        size_t nc = b->p_cap ? b->p_cap * 2 : 4;
        void *n = realloc(b->pantry, nc * sizeof(*b->pantry));
        if (!n) { ca_food_pantry_item_free(&copy); return -1; }
        b->pantry = (ca_food_pantry_item_t *)n;
        b->p_cap = nc;
    }
    b->pantry[b->p_count++] = copy;
    return 0;
}

int ca_food_board_use(ca_food_board_t *b, const char *pantry_item_id,
                      double quantity) {
    if (!b || !pantry_item_id) return -1;
    for (size_t i = 0; i < b->p_count; ++i) {
        if (cab_ord_eq(b->pantry[i].pantry_item_id, pantry_item_id)) {
            double q = b->pantry[i].quantity - quantity;
            b->pantry[i].quantity = q > 0 ? q : 0.0;
            return 0;
        }
    }
    return -2; /* Unknown pantry item -> C# InvalidOperationException */
}

ca_food_pantry_item_t *ca_food_board_pantry(const ca_food_board_t *b,
                                            size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->p_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->p_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->p_count; ++i)
        if (b->pantry[i].quantity > 0) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_food_pantry_item_t *out = (ca_food_pantry_item_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!pantry_copy(&out[i], &b->pantry[idx[i]])) {
            ca_food_pantry_item_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

/* Stable ascending sort of collected indices by BestBefore. */
static void expiry_sort_asc(const ca_food_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->pantry[key].best_before_ms;
        size_t j = i;
        while (j > 0 && b->pantry[idx[j - 1]].best_before_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_food_pantry_item_t *ca_food_board_expiring(const ca_food_board_t *b,
                                              int64_t before_ms,
                                              size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->p_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->p_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->p_count; ++i) {
        const ca_food_pantry_item_t *p = &b->pantry[i];
        if (p->has_best_before && p->best_before_ms <= before_ms) idx[n++] = i;
    }
    expiry_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_food_pantry_item_t *out = (ca_food_pantry_item_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!pantry_copy(&out[i], &b->pantry[idx[i]])) {
            ca_food_pantry_item_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
