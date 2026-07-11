/*
 * sports.c — CircleAI.Sports (C11 port of SportsPrimitives.cs).
 *
 * InMemorySportsBoard: activities (flat append list), sessions (SessionId keyed).
 * History newest-first; TotalKmThisWeek sums since the Sunday week-start; Best is
 * the fastest qualifying activity; Upcoming filters incomplete future sessions.
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/sports.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_sports_activity_free(ca_sports_activity_t *a) {
    if (!a) return;
    free(a->activity_id);
    free(a->user_id);
    a->activity_id = a->user_id = NULL;
}
void ca_sports_activity_free_array(ca_sports_activity_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_sports_activity_free(&arr[i]);
    free(arr);
}

static bool activity_copy(ca_sports_activity_t *dst,
                          const ca_sports_activity_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->activity_id   = cab_strdup_empty(src->activity_id);
    dst->user_id       = cab_strdup_empty(src->user_id);
    dst->kind          = src->kind;
    dst->distance_km   = src->distance_km;
    dst->duration_ticks = src->duration_ticks;
    dst->at_utc_ms     = src->at_utc_ms;
    if (!dst->activity_id || !dst->user_id) {
        ca_sports_activity_free(dst);
        return false;
    }
    return true;
}

void ca_sports_personal_best_free(ca_sports_personal_best_t *p) {
    if (!p) return;
    free(p->user_id);
    p->user_id = NULL;
}

void ca_sports_session_free(ca_sports_session_t *s) {
    if (!s) return;
    free(s->session_id);
    free(s->user_id);
    free(s->plan);
    s->session_id = s->user_id = s->plan = NULL;
}
void ca_sports_session_free_array(ca_sports_session_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_sports_session_free(&arr[i]);
    free(arr);
}

static bool session_copy(ca_sports_session_t *dst,
                         const ca_sports_session_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->session_id       = cab_strdup_empty(src->session_id);
    dst->user_id          = cab_strdup_empty(src->user_id);
    dst->plan             = cab_strdup_empty(src->plan);
    dst->scheduled_utc_ms = src->scheduled_utc_ms;
    dst->completed        = src->completed;
    if (!dst->session_id || !dst->user_id || !dst->plan) {
        ca_sports_session_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_sports_board {
    ca_sports_activity_t *acts;
    size_t                a_count, a_cap;
    ca_sports_session_t  *sess;
    size_t                s_count, s_cap;
};

ca_sports_board_t *ca_sports_board_create(void) {
    return (ca_sports_board_t *)calloc(1, sizeof(ca_sports_board_t));
}
void ca_sports_board_destroy(ca_sports_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->a_count; ++i) ca_sports_activity_free(&b->acts[i]);
    for (size_t i = 0; i < b->s_count; ++i) ca_sports_session_free(&b->sess[i]);
    free(b->acts);
    free(b->sess);
    free(b);
}

int ca_sports_board_log(ca_sports_board_t *b, const ca_sports_activity_t *a) {
    if (!b || !a) return -1;
    ca_sports_activity_t copy;
    if (!activity_copy(&copy, a)) return -1;
    if (b->a_count == b->a_cap) {
        size_t nc = b->a_cap ? b->a_cap * 2 : 4;
        void *n = realloc(b->acts, nc * sizeof(*b->acts));
        if (!n) { ca_sports_activity_free(&copy); return -1; }
        b->acts = (ca_sports_activity_t *)n;
        b->a_cap = nc;
    }
    b->acts[b->a_count++] = copy;
    return 0;
}

/* Stable descending sort of collected indices by AtUtc. */
static void act_sort_desc(const ca_sports_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->acts[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->acts[idx[j - 1]].at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_sports_activity_t *ca_sports_board_history(const ca_sports_board_t *b,
                                              const char *user_id, int limit,
                                              size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !user_id || limit <= 0) { *out_count = (size_t)-1; return NULL; }
    if (b->a_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->a_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->a_count; ++i)
        if (cab_ord_eq(b->acts[i].user_id, user_id)) idx[n++] = i;
    act_sort_desc(b, idx, n);

    if ((size_t)limit < n) n = (size_t)limit;
    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_sports_activity_t *out = (ca_sports_activity_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!activity_copy(&out[i], &b->acts[idx[i]])) {
            ca_sports_activity_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

double ca_sports_board_total_km_this_week(const ca_sports_board_t *b,
                                          const char *user_id,
                                          ca_distance_kind_t kind,
                                          int64_t now_ms) {
    if (!b || !user_id) return 0.0;
    int64_t week_start = cab_week_start_ms(now_ms);
    double sum = 0.0;
    for (size_t i = 0; i < b->a_count; ++i) {
        const ca_sports_activity_t *a = &b->acts[i];
        if (cab_ord_eq(a->user_id, user_id) && a->kind == kind &&
            a->at_utc_ms >= week_start)
            sum += a->distance_km;
    }
    return sum;
}

bool ca_sports_board_best(const ca_sports_board_t *b, const char *user_id,
                          ca_distance_kind_t kind, double distance_km,
                          ca_sports_personal_best_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !user_id || !out) return false;
    /* OrderBy(Duration).FirstOrDefault(): the smallest-Duration qualifying
     * activity; on ties LINQ keeps the first in source order (stable). */
    const ca_sports_activity_t *hit = NULL;
    for (size_t i = 0; i < b->a_count; ++i) {
        const ca_sports_activity_t *a = &b->acts[i];
        if (cab_ord_eq(a->user_id, user_id) && a->kind == kind &&
            a->distance_km >= distance_km) {
            if (!hit || a->duration_ticks < hit->duration_ticks) hit = a;
        }
    }
    if (!hit) return false;
    out->user_id = cab_strdup_empty(user_id);
    out->kind = kind;
    out->distance_km = distance_km; /* the query distance, per the C# */
    out->time_ticks = hit->duration_ticks;
    out->achieved_utc_ms = hit->at_utc_ms;
    if (!out->user_id) { ca_sports_personal_best_free(out); return false; }
    return true;
}

int ca_sports_board_schedule(ca_sports_board_t *b, const ca_sports_session_t *s) {
    if (!b || !s) return -1;
    for (size_t i = 0; i < b->s_count; ++i) {
        if (cab_ord_eq(b->sess[i].session_id, s->session_id)) {
            ca_sports_session_t copy;
            if (!session_copy(&copy, s)) return -1;
            ca_sports_session_free(&b->sess[i]);
            b->sess[i] = copy;
            return 0;
        }
    }
    ca_sports_session_t copy;
    if (!session_copy(&copy, s)) return -1;
    if (b->s_count == b->s_cap) {
        size_t nc = b->s_cap ? b->s_cap * 2 : 4;
        void *n = realloc(b->sess, nc * sizeof(*b->sess));
        if (!n) { ca_sports_session_free(&copy); return -1; }
        b->sess = (ca_sports_session_t *)n;
        b->s_cap = nc;
    }
    b->sess[b->s_count++] = copy;
    return 0;
}

int ca_sports_board_complete(ca_sports_board_t *b, const char *session_id) {
    if (!b || !session_id) return -1;
    for (size_t i = 0; i < b->s_count; ++i) {
        if (cab_ord_eq(b->sess[i].session_id, session_id)) {
            b->sess[i].completed = true;
            return 0;
        }
    }
    return -2; /* Unknown session -> C# InvalidOperationException */
}

/* Stable ascending sort of collected indices by ScheduledUtc. */
static void sess_sort_asc(const ca_sports_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->sess[key].scheduled_utc_ms;
        size_t j = i;
        while (j > 0 && b->sess[idx[j - 1]].scheduled_utc_ms > kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_sports_session_t *ca_sports_board_upcoming(const ca_sports_board_t *b,
                                              const char *user_id, int64_t now_ms,
                                              size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !user_id) { *out_count = (size_t)-1; return NULL; }
    if (b->s_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->s_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->s_count; ++i) {
        const ca_sports_session_t *s = &b->sess[i];
        if (cab_ord_eq(s->user_id, user_id) && !s->completed &&
            s->scheduled_utc_ms >= now_ms)
            idx[n++] = i;
    }
    sess_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_sports_session_t *out = (ca_sports_session_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!session_copy(&out[i], &b->sess[idx[i]])) {
            ca_sports_session_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
