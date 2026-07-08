/*
 * goal_store.c — CircleAI.Memory Goal + IGoalStore (C11 port).
 *
 * Ports Goal.cs (record + AdvanceProgress) and InMemoryGoalStore.cs. The store
 * is keyed by Id (ConcurrentDictionary<string,Goal> in C#); ListAsync /
 * GetActiveAsync filter by ordinal UserId. In-memory only; no persistence.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/goal_store.h"

#include <stdlib.h>
#include <string.h>

static char *gs_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static bool gs_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p)
        if (*p != ' ' && *p != '\t' && *p != '\n' && *p != '\r' &&
            *p != '\f' && *p != '\v') return false;
    return true;
}
static float gs_clampf(float v, float lo, float hi) {
    return v < lo ? lo : (v > hi ? hi : v);
}

void ca_goal_record_free(ca_goal_record_t *g) {
    if (!g) return;
    free(g->id);
    free(g->user_id);
    free(g->title);
    free(g->description);
    free(g->notes);
    g->id = g->user_id = g->title = g->description = g->notes = NULL;
}
void ca_goal_record_free_array(ca_goal_record_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_goal_record_free(&arr[i]);
    free(arr);
}
ca_goal_record_t *ca_goal_record_copy(ca_goal_record_t *dst, const ca_goal_record_t *src) {
    if (!dst || !src) return dst;
    dst->id                = gs_strdup(src->id);
    dst->user_id           = gs_strdup(src->user_id);
    dst->title             = gs_strdup(src->title);
    dst->description       = gs_strdup(src->description);
    dst->status            = src->status;
    dst->priority          = src->priority;
    dst->created_utc_ms    = src->created_utc_ms;
    dst->has_due_utc       = src->has_due_utc;
    dst->due_utc_ms        = src->due_utc_ms;
    dst->has_completed_utc = src->has_completed_utc;
    dst->completed_utc_ms  = src->completed_utc_ms;
    dst->notes             = gs_strdup(src->notes);
    dst->progress          = src->progress;
    return dst;
}

void ca_goal_record_advance_progress(const ca_goal_record_t *g, float delta,
                                     ca_goal_record_t *out) {
    if (!g || !out) return;
    ca_goal_record_copy(out, g);
    out->progress = gs_clampf(g->progress + delta, 0.0f, 1.0f);
}

/* ── store ──────────────────────────────────────────────────────────── */

struct ca_goal_store {
    ca_goal_record_t *goals;   /* linear, keyed by id */
    size_t            count;
    size_t            cap;
};

ca_goal_store_t *ca_goal_store_create(void) {
    return (ca_goal_store_t *)calloc(1, sizeof(ca_goal_store_t));
}
void ca_goal_store_destroy(ca_goal_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i) ca_goal_record_free(&store->goals[i]);
    free(store->goals);
    free(store);
}

static ca_goal_record_t *gs_find(ca_goal_store_t *store, const char *id) {
    for (size_t i = 0; i < store->count; ++i)
        if (store->goals[i].id && strcmp(store->goals[i].id, id) == 0)
            return &store->goals[i];
    return NULL;
}

static ca_goal_record_t *gs_filter(ca_goal_store_t *store, const char *user_id,
                                   bool active_only, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store || gs_blank(user_id)) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < store->count; ++i) {
        if (store->goals[i].user_id && strcmp(store->goals[i].user_id, user_id) == 0 &&
            (!active_only || store->goals[i].status == CA_GOAL_STATUS_ACTIVE))
            ++n;
    }
    if (n == 0) return NULL;
    ca_goal_record_t *res = (ca_goal_record_t *)calloc(n, sizeof(*res));
    if (!res) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    size_t k = 0;
    for (size_t i = 0; i < store->count; ++i)
        if (store->goals[i].user_id && strcmp(store->goals[i].user_id, user_id) == 0 &&
            (!active_only || store->goals[i].status == CA_GOAL_STATUS_ACTIVE))
            ca_goal_record_copy(&res[k++], &store->goals[i]);
    if (out_count) *out_count = n;
    return res;
}

ca_goal_record_t *ca_goal_store_list(ca_goal_store_t *store, const char *user_id,
                                     size_t *out_count) {
    return gs_filter(store, user_id, false, out_count);
}

ca_goal_record_t *ca_goal_store_get_active(ca_goal_store_t *store, const char *user_id,
                                           size_t *out_count) {
    return gs_filter(store, user_id, true, out_count);
}

bool ca_goal_store_get(ca_goal_store_t *store, const char *id, ca_goal_record_t *out) {
    if (!store || gs_blank(id) || !out) return false;
    ca_goal_record_t *g = gs_find(store, id);
    if (!g) return false;
    ca_goal_record_copy(out, g);
    return true;
}

bool ca_goal_store_upsert(ca_goal_store_t *store, const ca_goal_record_t *goal) {
    if (!store || !goal || gs_blank(goal->id)) return false;
    ca_goal_record_t *existing = gs_find(store, goal->id);
    if (existing) {
        ca_goal_record_t copy; memset(&copy, 0, sizeof(copy));
        ca_goal_record_copy(&copy, goal);
        ca_goal_record_free(existing);
        *existing = copy;
        return true;
    }
    if (store->count == store->cap) {
        size_t nc = store->cap ? store->cap * 2 : 8;
        void *n = realloc(store->goals, nc * sizeof(*store->goals));
        if (!n) return false;
        store->goals = n; store->cap = nc;
    }
    ca_goal_record_copy(&store->goals[store->count], goal);
    store->count++;
    return true;
}

bool ca_goal_store_delete(ca_goal_store_t *store, const char *id) {
    if (!store || gs_blank(id)) return false;
    for (size_t i = 0; i < store->count; ++i)
        if (store->goals[i].id && strcmp(store->goals[i].id, id) == 0) {
            ca_goal_record_free(&store->goals[i]);
            memmove(&store->goals[i], &store->goals[i + 1],
                    (store->count - i - 1) * sizeof(*store->goals));
            store->count--;
            return true;
        }
    return true; /* no-op when absent (C# DeleteAsync is a no-op) */
}
