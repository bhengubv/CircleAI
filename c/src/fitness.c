/*
 * fitness.c — CircleAI.Fitness (C11 port of FitnessPrimitives.cs).
 *
 * InMemoryFitnessBoard: workouts (append list), goals (GoalId keyed), sets
 * (append list). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/fitness.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_fitness_workout_free(ca_fitness_workout_t *w) {
    if (!w) return;
    free(w->workout_id);
    free(w->user_id);
    free(w->kind);
    w->workout_id = w->user_id = w->kind = NULL;
}
void ca_fitness_workout_free_array(ca_fitness_workout_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_fitness_workout_free(&arr[i]);
    free(arr);
}

static bool workout_copy(ca_fitness_workout_t *dst,
                         const ca_fitness_workout_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->workout_id       = cab_strdup_empty(src->workout_id);
    dst->user_id          = cab_strdup_empty(src->user_id);
    dst->kind             = cab_strdup_empty(src->kind);
    dst->duration_minutes = src->duration_minutes;
    dst->calories_burned  = src->calories_burned;
    dst->at_utc_ms        = src->at_utc_ms;
    if (!dst->workout_id || !dst->user_id || !dst->kind) {
        ca_fitness_workout_free(dst);
        return false;
    }
    return true;
}

void ca_fitness_goal_free(ca_fitness_goal_t *g) {
    if (!g) return;
    free(g->goal_id);
    free(g->user_id);
    free(g->metric);
    g->goal_id = g->user_id = g->metric = NULL;
}
void ca_fitness_goal_free_array(ca_fitness_goal_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_fitness_goal_free(&arr[i]);
    free(arr);
}

static bool goal_copy(ca_fitness_goal_t *dst, const ca_fitness_goal_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->goal_id   = cab_strdup_empty(src->goal_id);
    dst->user_id   = cab_strdup_empty(src->user_id);
    dst->metric    = cab_strdup_empty(src->metric);
    dst->target    = src->target;
    dst->due_on_ms = src->due_on_ms;
    if (!dst->goal_id || !dst->user_id || !dst->metric) {
        ca_fitness_goal_free(dst);
        return false;
    }
    return true;
}

void ca_fitness_set_free(ca_fitness_set_t *s) {
    if (!s) return;
    free(s->set_id);
    free(s->workout_id);
    free(s->exercise);
    s->set_id = s->workout_id = s->exercise = NULL;
}
void ca_fitness_set_free_array(ca_fitness_set_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_fitness_set_free(&arr[i]);
    free(arr);
}

static bool set_copy(ca_fitness_set_t *dst, const ca_fitness_set_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->set_id     = cab_strdup_empty(src->set_id);
    dst->workout_id = cab_strdup_empty(src->workout_id);
    dst->exercise   = cab_strdup_empty(src->exercise);
    dst->reps       = src->reps;
    dst->weight_kg  = src->weight_kg;
    if (!dst->set_id || !dst->workout_id || !dst->exercise) {
        ca_fitness_set_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_fitness_board {
    ca_fitness_workout_t *workouts;
    size_t                w_count, w_cap;
    ca_fitness_goal_t    *goals;
    size_t                g_count, g_cap;
    ca_fitness_set_t     *sets;
    size_t                s_count, s_cap;
};

ca_fitness_board_t *ca_fitness_board_create(void) {
    return (ca_fitness_board_t *)calloc(1, sizeof(ca_fitness_board_t));
}
void ca_fitness_board_destroy(ca_fitness_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->w_count; ++i) ca_fitness_workout_free(&b->workouts[i]);
    for (size_t i = 0; i < b->g_count; ++i) ca_fitness_goal_free(&b->goals[i]);
    for (size_t i = 0; i < b->s_count; ++i) ca_fitness_set_free(&b->sets[i]);
    free(b->workouts);
    free(b->goals);
    free(b->sets);
    free(b);
}

int ca_fitness_board_log(ca_fitness_board_t *b, const ca_fitness_workout_t *w) {
    if (!b || !w) return -1;
    ca_fitness_workout_t copy;
    if (!workout_copy(&copy, w)) return -1;
    if (b->w_count == b->w_cap) {
        size_t nc = b->w_cap ? b->w_cap * 2 : 4;
        void *n = realloc(b->workouts, nc * sizeof(*b->workouts));
        if (!n) { ca_fitness_workout_free(&copy); return -1; }
        b->workouts = (ca_fitness_workout_t *)n;
        b->w_cap = nc;
    }
    b->workouts[b->w_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected indices by AtUtc. */
static void workout_sort_asc(const ca_fitness_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->workouts[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->workouts[idx[j - 1]].at_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_fitness_workout_t *ca_fitness_board_workouts_this_week(
    const ca_fitness_board_t *b, const char *user_id, int64_t now_ms,
    size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !user_id) { *out_count = (size_t)-1; return NULL; }
    if (b->w_count == 0) { *out_count = 0; return NULL; }

    int64_t week_start = cab_week_start_ms(now_ms);
    size_t *idx = (size_t *)malloc(b->w_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->w_count; ++i) {
        const ca_fitness_workout_t *w = &b->workouts[i];
        if (cab_ord_eq(w->user_id, user_id) && w->at_utc_ms >= week_start)
            idx[n++] = i;
    }
    workout_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_fitness_workout_t *out = (ca_fitness_workout_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!workout_copy(&out[i], &b->workouts[idx[i]])) {
            ca_fitness_workout_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

double ca_fitness_board_total_calories_since(const ca_fitness_board_t *b,
                                             const char *user_id,
                                             int64_t since_ms) {
    if (!b || !user_id) return 0.0;
    double sum = 0.0;
    for (size_t i = 0; i < b->w_count; ++i) {
        const ca_fitness_workout_t *w = &b->workouts[i];
        if (cab_ord_eq(w->user_id, user_id) && w->at_utc_ms >= since_ms)
            sum += w->calories_burned;
    }
    return sum;
}

int ca_fitness_board_set_goal(ca_fitness_board_t *b, const ca_fitness_goal_t *g) {
    if (!b || !g) return -1;
    for (size_t i = 0; i < b->g_count; ++i) {
        if (cab_ord_eq(b->goals[i].goal_id, g->goal_id)) {
            ca_fitness_goal_t copy;
            if (!goal_copy(&copy, g)) return -1;
            ca_fitness_goal_free(&b->goals[i]);
            b->goals[i] = copy;
            return 0;
        }
    }
    ca_fitness_goal_t copy;
    if (!goal_copy(&copy, g)) return -1;
    if (b->g_count == b->g_cap) {
        size_t nc = b->g_cap ? b->g_cap * 2 : 4;
        void *n = realloc(b->goals, nc * sizeof(*b->goals));
        if (!n) { ca_fitness_goal_free(&copy); return -1; }
        b->goals = (ca_fitness_goal_t *)n;
        b->g_cap = nc;
    }
    b->goals[b->g_count++] = copy;
    return 0;
}

ca_fitness_goal_t *ca_fitness_board_goals_for(const ca_fitness_board_t *b,
                                              const char *user_id,
                                              size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !user_id) { *out_count = (size_t)-1; return NULL; }
    if (b->g_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->g_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->g_count; ++i)
        if (cab_ord_eq(b->goals[i].user_id, user_id)) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_fitness_goal_t *out = (ca_fitness_goal_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!goal_copy(&out[i], &b->goals[idx[i]])) {
            ca_fitness_goal_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_fitness_board_add_set(ca_fitness_board_t *b, const ca_fitness_set_t *s) {
    if (!b || !s) return -1;
    ca_fitness_set_t copy;
    if (!set_copy(&copy, s)) return -1;
    if (b->s_count == b->s_cap) {
        size_t nc = b->s_cap ? b->s_cap * 2 : 4;
        void *n = realloc(b->sets, nc * sizeof(*b->sets));
        if (!n) { ca_fitness_set_free(&copy); return -1; }
        b->sets = (ca_fitness_set_t *)n;
        b->s_cap = nc;
    }
    b->sets[b->s_count++] = copy;
    return 0;
}

ca_fitness_set_t *ca_fitness_board_sets_for(const ca_fitness_board_t *b,
                                            const char *workout_id,
                                            size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !workout_id) { *out_count = (size_t)-1; return NULL; }
    if (b->s_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->s_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->s_count; ++i)
        if (cab_ord_eq(b->sets[i].workout_id, workout_id)) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_fitness_set_t *out = (ca_fitness_set_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!set_copy(&out[i], &b->sets[idx[i]])) {
            ca_fitness_set_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
