/*
 * kids.c — CircleAI.Kids (C11 port of KidsPrimitives.cs).
 *
 * InMemoryKidsBoard: content (ContentId keyed), limits (KidName keyed), logs
 * (append list). Pure C11 + libc. No pthreads.
 */

#include "circle_ai/kids.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_kids_content_free(ca_kids_content_t *c) {
    if (!c) return;
    free(c->content_id);
    free(c->title);
    free(c->kind);
    cab_strv_free(c->tags, c->tag_count);
    c->content_id = c->title = c->kind = NULL;
    c->tags = NULL;
    c->tag_count = 0;
}
void ca_kids_content_free_array(ca_kids_content_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_kids_content_free(&arr[i]);
    free(arr);
}

static bool content_copy(ca_kids_content_t *dst, const ca_kids_content_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->content_id = cab_strdup_empty(src->content_id);
    dst->title      = cab_strdup_empty(src->title);
    dst->age_band   = src->age_band;
    dst->kind       = cab_strdup_empty(src->kind);
    bool ok = dst->content_id && dst->title && dst->kind;
    if (ok) ok = cab_strv_copy(&dst->tags, src->tags, src->tag_count);
    if (ok) dst->tag_count = src->tag_count;
    if (!ok) { ca_kids_content_free(dst); return false; }
    return true;
}

void ca_kids_daily_time_free(ca_kids_daily_time_t *d) {
    if (!d) return;
    free(d->kid_name);
    d->kid_name = NULL;
}

static bool daily_time_copy(ca_kids_daily_time_t *dst,
                            const ca_kids_daily_time_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->kid_name            = cab_strdup_empty(src->kid_name);
    dst->screen_limit_ticks  = src->screen_limit_ticks;
    dst->reading_limit_ticks = src->reading_limit_ticks;
    if (!dst->kid_name) return false;
    return true;
}

static bool time_log_copy(ca_kids_time_log_t *dst,
                          const ca_kids_time_log_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->kid_name       = cab_strdup_empty(src->kid_name);
    dst->kind           = cab_strdup_empty(src->kind);
    dst->duration_ticks = src->duration_ticks;
    dst->at_utc_ms      = src->at_utc_ms;
    if (!dst->kid_name || !dst->kind) {
        free(dst->kid_name); free(dst->kind);
        memset(dst, 0, sizeof(*dst));
        return false;
    }
    return true;
}
static void time_log_free(ca_kids_time_log_t *t) {
    if (!t) return;
    free(t->kid_name);
    free(t->kind);
    t->kid_name = t->kind = NULL;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_kids_board {
    ca_kids_content_t    *content;
    size_t                c_count, c_cap;
    ca_kids_daily_time_t *limits;
    size_t                l_count, l_cap;
    ca_kids_time_log_t   *logs;
    size_t                t_count, t_cap;
};

ca_kids_board_t *ca_kids_board_create(void) {
    return (ca_kids_board_t *)calloc(1, sizeof(ca_kids_board_t));
}
void ca_kids_board_destroy(ca_kids_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->c_count; ++i) ca_kids_content_free(&b->content[i]);
    for (size_t i = 0; i < b->l_count; ++i) ca_kids_daily_time_free(&b->limits[i]);
    for (size_t i = 0; i < b->t_count; ++i) time_log_free(&b->logs[i]);
    free(b->content);
    free(b->limits);
    free(b->logs);
    free(b);
}

int ca_kids_board_add_content(ca_kids_board_t *b, const ca_kids_content_t *c) {
    if (!b || !c) return -1;
    for (size_t i = 0; i < b->c_count; ++i) {
        if (cab_ord_eq(b->content[i].content_id, c->content_id)) {
            ca_kids_content_t copy;
            if (!content_copy(&copy, c)) return -1;
            ca_kids_content_free(&b->content[i]);
            b->content[i] = copy;
            return 0;
        }
    }
    ca_kids_content_t copy;
    if (!content_copy(&copy, c)) return -1;
    if (b->c_count == b->c_cap) {
        size_t nc = b->c_cap ? b->c_cap * 2 : 4;
        void *n = realloc(b->content, nc * sizeof(*b->content));
        if (!n) { ca_kids_content_free(&copy); return -1; }
        b->content = (ca_kids_content_t *)n;
        b->c_cap = nc;
    }
    b->content[b->c_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected indices by Title (ordinal). */
static void content_sort_title(const ca_kids_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 && strcmp(b->content[idx[j - 1]].title,
                              b->content[key].title) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_kids_content_t *ca_kids_board_content_for(const ca_kids_board_t *b,
                                             ca_age_appropriateness_t band,
                                             size_t *out_count) {
    if (!out_count) return NULL;
    if (!b) { *out_count = (size_t)-1; return NULL; }
    if (b->c_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->c_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->c_count; ++i)
        if (b->content[i].age_band == band) idx[n++] = i;
    content_sort_title(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_kids_content_t *out = (ca_kids_content_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!content_copy(&out[i], &b->content[idx[i]])) {
            ca_kids_content_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_kids_board_set_limits(ca_kids_board_t *b, const ca_kids_daily_time_t *d) {
    if (!b || !d) return -1;
    for (size_t i = 0; i < b->l_count; ++i) {
        if (cab_ord_eq(b->limits[i].kid_name, d->kid_name)) {
            ca_kids_daily_time_t copy;
            if (!daily_time_copy(&copy, d)) return -1;
            ca_kids_daily_time_free(&b->limits[i]);
            b->limits[i] = copy;
            return 0;
        }
    }
    ca_kids_daily_time_t copy;
    if (!daily_time_copy(&copy, d)) return -1;
    if (b->l_count == b->l_cap) {
        size_t nc = b->l_cap ? b->l_cap * 2 : 4;
        void *n = realloc(b->limits, nc * sizeof(*b->limits));
        if (!n) { ca_kids_daily_time_free(&copy); return -1; }
        b->limits = (ca_kids_daily_time_t *)n;
        b->l_cap = nc;
    }
    b->limits[b->l_count++] = copy;
    return 0;
}

bool ca_kids_board_limits_for(const ca_kids_board_t *b, const char *kid_name,
                              ca_kids_daily_time_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !kid_name || !out) return false;
    for (size_t i = 0; i < b->l_count; ++i)
        if (cab_ord_eq(b->limits[i].kid_name, kid_name))
            return daily_time_copy(out, &b->limits[i]);
    return false;
}

int ca_kids_board_record_time(ca_kids_board_t *b, const ca_kids_time_log_t *t) {
    if (!b || !t) return -1;
    ca_kids_time_log_t copy;
    if (!time_log_copy(&copy, t)) return -1;
    if (b->t_count == b->t_cap) {
        size_t nc = b->t_cap ? b->t_cap * 2 : 4;
        void *n = realloc(b->logs, nc * sizeof(*b->logs));
        if (!n) { time_log_free(&copy); return -1; }
        b->logs = (ca_kids_time_log_t *)n;
        b->t_cap = nc;
    }
    b->logs[b->t_count++] = copy;
    return 0;
}

int64_t ca_kids_board_used_today(const ca_kids_board_t *b, const char *kid_name,
                                 const char *kind, int64_t now_ms) {
    if (!b || !kid_name || !kind) return 0;
    int64_t today = cab_utc_day(now_ms);
    int64_t sum = 0;
    for (size_t i = 0; i < b->t_count; ++i) {
        const ca_kids_time_log_t *l = &b->logs[i];
        if (cab_ord_eq(l->kid_name, kid_name) && cab_ord_eq(l->kind, kind) &&
            cab_utc_day(l->at_utc_ms) == today)
            sum += l->duration_ticks;
    }
    return sum;
}

bool ca_kids_board_over_limit(const ca_kids_board_t *b, const char *kid_name,
                              const char *kind, int64_t now_ms) {
    if (!b || !kid_name || !kind) return false;
    const ca_kids_daily_time_t *lim = NULL;
    for (size_t i = 0; i < b->l_count; ++i)
        if (cab_ord_eq(b->limits[i].kid_name, kid_name)) {
            lim = &b->limits[i];
            break;
        }
    if (!lim) return false; /* no limits set */
    int64_t used = ca_kids_board_used_today(b, kid_name, kind, now_ms);
    int64_t cap = cab_ci_eq(kind, "screen")  ? lim->screen_limit_ticks
                : cab_ci_eq(kind, "reading") ? lim->reading_limit_ticks
                : INT64_MAX; /* TimeSpan.MaxValue */
    return used > cap;
}
