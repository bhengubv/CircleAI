/*
 * gaming.c — CircleAI.Gaming (C11 port of GamingPrimitives.cs).
 *
 * InMemoryGamingBoard: titles (TitleId keyed), sessions (append list), unlocks
 * (append list). MostPlayed groups sessions by TitleId (first-seen order), sorts
 * by total ticks descending, maps to live titles. Pure C11 + libc.
 */

#include "circle_ai/gaming.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_gaming_title_free(ca_gaming_title_t *t) {
    if (!t) return;
    free(t->title_id);
    free(t->name);
    free(t->genre);
    free(t->platform);
    t->title_id = t->name = t->genre = t->platform = NULL;
}
void ca_gaming_title_free_array(ca_gaming_title_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_gaming_title_free(&arr[i]);
    free(arr);
}

static bool title_copy(ca_gaming_title_t *dst, const ca_gaming_title_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->title_id = cab_strdup_empty(src->title_id);
    dst->name     = cab_strdup_empty(src->name);
    dst->genre    = cab_strdup_empty(src->genre);
    dst->platform = cab_strdup_empty(src->platform);
    if (!dst->title_id || !dst->name || !dst->genre || !dst->platform) {
        ca_gaming_title_free(dst);
        return false;
    }
    return true;
}

static bool session_copy(ca_gaming_session_t *dst,
                         const ca_gaming_session_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->session_id     = cab_strdup_empty(src->session_id);
    dst->user_id        = cab_strdup_empty(src->user_id);
    dst->title_id       = cab_strdup_empty(src->title_id);
    dst->duration_ticks = src->duration_ticks;
    dst->at_utc_ms      = src->at_utc_ms;
    if (!dst->session_id || !dst->user_id || !dst->title_id) {
        free(dst->session_id); free(dst->user_id); free(dst->title_id);
        memset(dst, 0, sizeof(*dst));
        return false;
    }
    return true;
}
static void session_free(ca_gaming_session_t *s) {
    if (!s) return;
    free(s->session_id);
    free(s->user_id);
    free(s->title_id);
    s->session_id = s->user_id = s->title_id = NULL;
}

void ca_gaming_unlock_free(ca_gaming_unlock_t *u) {
    if (!u) return;
    free(u->unlock_id);
    free(u->user_id);
    free(u->title_id);
    free(u->achievement);
    u->unlock_id = u->user_id = u->title_id = u->achievement = NULL;
}
void ca_gaming_unlock_free_array(ca_gaming_unlock_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_gaming_unlock_free(&arr[i]);
    free(arr);
}

static bool unlock_copy(ca_gaming_unlock_t *dst, const ca_gaming_unlock_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->unlock_id   = cab_strdup_empty(src->unlock_id);
    dst->user_id     = cab_strdup_empty(src->user_id);
    dst->title_id    = cab_strdup_empty(src->title_id);
    dst->achievement = cab_strdup_empty(src->achievement);
    dst->at_utc_ms   = src->at_utc_ms;
    if (!dst->unlock_id || !dst->user_id || !dst->title_id || !dst->achievement) {
        ca_gaming_unlock_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_gaming_board {
    ca_gaming_title_t   *titles;
    size_t               t_count, t_cap;
    ca_gaming_session_t *sessions;
    size_t               s_count, s_cap;
    ca_gaming_unlock_t  *unlocks;
    size_t               u_count, u_cap;
};

ca_gaming_board_t *ca_gaming_board_create(void) {
    return (ca_gaming_board_t *)calloc(1, sizeof(ca_gaming_board_t));
}
void ca_gaming_board_destroy(ca_gaming_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->t_count; ++i) ca_gaming_title_free(&b->titles[i]);
    for (size_t i = 0; i < b->s_count; ++i) session_free(&b->sessions[i]);
    for (size_t i = 0; i < b->u_count; ++i) ca_gaming_unlock_free(&b->unlocks[i]);
    free(b->titles);
    free(b->sessions);
    free(b->unlocks);
    free(b);
}

int ca_gaming_board_add_title(ca_gaming_board_t *b, const ca_gaming_title_t *t) {
    if (!b || !t) return -1;
    for (size_t i = 0; i < b->t_count; ++i) {
        if (cab_ord_eq(b->titles[i].title_id, t->title_id)) {
            ca_gaming_title_t copy;
            if (!title_copy(&copy, t)) return -1;
            ca_gaming_title_free(&b->titles[i]);
            b->titles[i] = copy;
            return 0;
        }
    }
    ca_gaming_title_t copy;
    if (!title_copy(&copy, t)) return -1;
    if (b->t_count == b->t_cap) {
        size_t nc = b->t_cap ? b->t_cap * 2 : 4;
        void *n = realloc(b->titles, nc * sizeof(*b->titles));
        if (!n) { ca_gaming_title_free(&copy); return -1; }
        b->titles = (ca_gaming_title_t *)n;
        b->t_cap = nc;
    }
    b->titles[b->t_count++] = copy;
    return 0;
}

bool ca_gaming_board_get_title(const ca_gaming_board_t *b, const char *id,
                               ca_gaming_title_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->t_count; ++i)
        if (cab_ord_eq(b->titles[i].title_id, id))
            return title_copy(out, &b->titles[i]);
    return false;
}

ca_gaming_title_t *ca_gaming_board_titles_by_genre(const ca_gaming_board_t *b,
                                                   const char *genre,
                                                   size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !genre) { *out_count = (size_t)-1; return NULL; }
    if (b->t_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->t_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->t_count; ++i)
        if (cab_ci_eq(b->titles[i].genre, genre)) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_gaming_title_t *out = (ca_gaming_title_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!title_copy(&out[i], &b->titles[idx[i]])) {
            ca_gaming_title_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_gaming_board_record_session(ca_gaming_board_t *b,
                                   const ca_gaming_session_t *s) {
    if (!b || !s) return -1;
    ca_gaming_session_t copy;
    if (!session_copy(&copy, s)) return -1;
    if (b->s_count == b->s_cap) {
        size_t nc = b->s_cap ? b->s_cap * 2 : 4;
        void *n = realloc(b->sessions, nc * sizeof(*b->sessions));
        if (!n) { session_free(&copy); return -1; }
        b->sessions = (ca_gaming_session_t *)n;
        b->s_cap = nc;
    }
    b->sessions[b->s_count++] = copy;
    return 0;
}

int64_t ca_gaming_board_total_play_time(const ca_gaming_board_t *b,
                                        const char *user_id,
                                        const char *title_id) {
    if (!b || !user_id || !title_id) return 0;
    int64_t sum = 0;
    for (size_t i = 0; i < b->s_count; ++i) {
        const ca_gaming_session_t *s = &b->sessions[i];
        if (cab_ord_eq(s->user_id, user_id) && cab_ord_eq(s->title_id, title_id))
            sum += s->duration_ticks;
    }
    return sum;
}

int ca_gaming_board_unlock(ca_gaming_board_t *b, const ca_gaming_unlock_t *u) {
    if (!b || !u) return -1;
    ca_gaming_unlock_t copy;
    if (!unlock_copy(&copy, u)) return -1;
    if (b->u_count == b->u_cap) {
        size_t nc = b->u_cap ? b->u_cap * 2 : 4;
        void *n = realloc(b->unlocks, nc * sizeof(*b->unlocks));
        if (!n) { ca_gaming_unlock_free(&copy); return -1; }
        b->unlocks = (ca_gaming_unlock_t *)n;
        b->u_cap = nc;
    }
    b->unlocks[b->u_count++] = copy;
    return 0;
}

/* Stable descending sort of collected indices by AtUtc. */
static void unlock_sort_desc(const ca_gaming_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->unlocks[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->unlocks[idx[j - 1]].at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_gaming_unlock_t *ca_gaming_board_achievements_for(const ca_gaming_board_t *b,
                                                     const char *user_id,
                                                     size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !user_id) { *out_count = (size_t)-1; return NULL; }
    if (b->u_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->u_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->u_count; ++i)
        if (cab_ord_eq(b->unlocks[i].user_id, user_id)) idx[n++] = i;
    unlock_sort_desc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_gaming_unlock_t *out = (ca_gaming_unlock_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!unlock_copy(&out[i], &b->unlocks[idx[i]])) {
            ca_gaming_unlock_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

/* Group scratch: a title id with its summed ticks, in first-seen order. */
typedef struct { const char *title_id; int64_t ticks; } gaming_group_t;

/* Stable descending sort of groups by summed ticks (ties keep first-seen). */
static void group_sort_desc(gaming_group_t *g, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        gaming_group_t key = g[i];
        size_t j = i;
        while (j > 0 && g[j - 1].ticks < key.ticks) {
            g[j] = g[j - 1];
            j--;
        }
        g[j] = key;
    }
}

/* Locate a live title copy by id into *out; false when the title is absent. */
static bool find_title_copy(const ca_gaming_board_t *b, const char *title_id,
                            ca_gaming_title_t *out) {
    for (size_t i = 0; i < b->t_count; ++i)
        if (cab_ord_eq(b->titles[i].title_id, title_id))
            return title_copy(out, &b->titles[i]);
    return false;
}

ca_gaming_title_t *ca_gaming_board_most_played(const ca_gaming_board_t *b,
                                               const char *user_id, int top_k,
                                               size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !user_id || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    if (b->s_count == 0) { *out_count = 0; return NULL; }

    /* Build groups in first-seen order over the user's sessions. */
    gaming_group_t *g = (gaming_group_t *)calloc(b->s_count, sizeof(gaming_group_t));
    if (!g) { *out_count = (size_t)-1; return NULL; }
    size_t gc = 0;
    for (size_t i = 0; i < b->s_count; ++i) {
        const ca_gaming_session_t *s = &b->sessions[i];
        if (!cab_ord_eq(s->user_id, user_id)) continue;
        size_t k;
        for (k = 0; k < gc; ++k)
            if (cab_ord_eq(g[k].title_id, s->title_id)) break;
        if (k == gc) { g[gc].title_id = s->title_id; g[gc].ticks = 0; gc++; }
        g[k].ticks += s->duration_ticks;
    }
    if (gc == 0) { free(g); *out_count = 0; return NULL; }

    group_sort_desc(g, gc);

    /* Map groups (top_k) to live titles, dropping ones no longer present. */
    ca_gaming_title_t *out = (ca_gaming_title_t *)calloc(gc, sizeof(*out));
    if (!out) { free(g); *out_count = (size_t)-1; return NULL; }
    size_t taken = 0, produced = 0;
    for (size_t i = 0; i < gc && taken < (size_t)top_k; ++i) {
        ca_gaming_title_t t;
        if (find_title_copy(b, g[i].title_id, &t)) {
            out[produced++] = t;
            taken++;
        } else {
            /* C# Take(topK) counts the null before Where filters it. */
            taken++;
        }
    }
    free(g);

    if (produced == 0) { free(out); *out_count = 0; return NULL; }
    *out_count = produced;
    return out;
}
