/*
 * autonomous_biz.c — CircleAI.AutonomousBiz (C11 port).
 *
 * RevenueLoop: append-only history + owned subscriber tokens each with a FIFO
 * cursor (fan-out snapshots the list first so a handler may unsubscribe safely).
 * Treasury: reads the loop's events and sums Amount by currency. DecisionLog:
 * append list read newest-first.
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/autonomous_biz.h"
#include "board_common.h"

/* ── TreasurySnapshot ───────────────────────────────────────────────────── */

void ca_abiz_treasury_snapshot_free(ca_abiz_treasury_snapshot_t *s) {
    if (!s) return;
    free(s->currency);
    s->currency = NULL;
}

/* ── RevenueEvent ───────────────────────────────────────────────────────── */

void ca_abiz_revenue_event_free(ca_abiz_revenue_event_t *e) {
    if (!e) return;
    free(e->event_id);
    free(e->currency);
    free(e->source);
    e->event_id = e->currency = e->source = NULL;
}
void ca_abiz_revenue_event_free_array(ca_abiz_revenue_event_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_abiz_revenue_event_free(&arr[i]);
    free(arr);
}
static bool revenue_copy(ca_abiz_revenue_event_t *dst,
                         const ca_abiz_revenue_event_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->amount    = src->amount;
    dst->at_utc_ms = src->at_utc_ms;
    dst->event_id  = cab_strdup_empty(src->event_id);
    dst->currency  = cab_strdup_empty(src->currency);
    dst->source    = cab_strdup_empty(src->source);
    if (!dst->event_id || !dst->currency || !dst->source) {
        ca_abiz_revenue_event_free(dst);
        return false;
    }
    return true;
}

/* ── AutonomousDecision ─────────────────────────────────────────────────── */

void ca_abiz_decision_free(ca_abiz_decision_t *d) {
    if (!d) return;
    free(d->decision_id);
    free(d->rationale);
    free(d->chosen_action);
    d->decision_id = d->rationale = d->chosen_action = NULL;
}
void ca_abiz_decision_free_array(ca_abiz_decision_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_abiz_decision_free(&arr[i]);
    free(arr);
}
static bool decision_copy(ca_abiz_decision_t *dst, const ca_abiz_decision_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->at_utc_ms     = src->at_utc_ms;
    dst->decision_id   = cab_strdup_empty(src->decision_id);
    dst->rationale     = cab_strdup_empty(src->rationale);
    dst->chosen_action = cab_strdup_empty(src->chosen_action);
    if (!dst->decision_id || !dst->rationale || !dst->chosen_action) {
        ca_abiz_decision_free(dst);
        return false;
    }
    return true;
}

/* ── InMemoryRevenueLoop ────────────────────────────────────────────────── */

struct ca_abiz_revenue_sub {
    ca_abiz_revenue_handler_fn handler;
    void                      *ctx;
    ca_abiz_revenue_event_t   *buf;    /* FIFO of owned copies */
    size_t                     head, count, cap;
};

struct ca_abiz_revenue_loop {
    ca_abiz_revenue_event_t  *history;
    size_t                    h_count, h_cap;
    ca_abiz_revenue_sub_t   **subs;    /* owned tokens */
    size_t                    s_count, s_cap;
};

ca_abiz_revenue_loop_t *ca_abiz_revenue_loop_create(void) {
    return (ca_abiz_revenue_loop_t *)calloc(1, sizeof(ca_abiz_revenue_loop_t));
}
static void sub_free(ca_abiz_revenue_sub_t *sub) {
    if (!sub) return;
    for (size_t i = 0; i < sub->count; ++i)
        ca_abiz_revenue_event_free(&sub->buf[(sub->head + i) % sub->cap]);
    free(sub->buf);
    free(sub);
}
void ca_abiz_revenue_loop_destroy(ca_abiz_revenue_loop_t *l) {
    if (!l) return;
    for (size_t i = 0; i < l->h_count; ++i) ca_abiz_revenue_event_free(&l->history[i]);
    for (size_t i = 0; i < l->s_count; ++i) sub_free(l->subs[i]);
    free(l->history);
    free(l->subs);
    free(l);
}
const char *ca_abiz_revenue_loop_backend_id(const ca_abiz_revenue_loop_t *l) {
    (void)l; return "in-memory";
}

static bool sub_push(ca_abiz_revenue_sub_t *sub,
                     const ca_abiz_revenue_event_t *e) {
    if (sub->count == sub->cap) {
        size_t nc = sub->cap ? sub->cap * 2 : 4;
        ca_abiz_revenue_event_t *nb =
            (ca_abiz_revenue_event_t *)calloc(nc, sizeof(*nb));
        if (!nb) return false;
        for (size_t i = 0; i < sub->count; ++i)
            nb[i] = sub->buf[(sub->head + i) % sub->cap];
        free(sub->buf);
        sub->buf = nb;
        sub->cap = nc;
        sub->head = 0;
    }
    ca_abiz_revenue_event_t copy;
    if (!revenue_copy(&copy, e)) return false;
    sub->buf[(sub->head + sub->count) % sub->cap] = copy;
    sub->count++;
    return true;
}

int ca_abiz_revenue_loop_publish(ca_abiz_revenue_loop_t *l,
                                 const ca_abiz_revenue_event_t *e) {
    if (!l || !e) return -1;
    /* append to history */
    if (l->h_count == l->h_cap) {
        size_t nc = l->h_cap ? l->h_cap * 2 : 4;
        void *n = realloc(l->history, nc * sizeof(*l->history));
        if (!n) return -1;
        l->history = (ca_abiz_revenue_event_t *)n;
        l->h_cap = nc;
    }
    ca_abiz_revenue_event_t copy;
    if (!revenue_copy(&copy, e)) return -1;
    l->history[l->h_count++] = copy;

    /* snapshot subscribers, then notify */
    size_t sn = l->s_count;
    ca_abiz_revenue_sub_t **snap = NULL;
    if (sn > 0) {
        snap = (ca_abiz_revenue_sub_t **)malloc(sn * sizeof(*snap));
        if (!snap) return -1;
        memcpy(snap, l->subs, sn * sizeof(*snap));
    }
    for (size_t i = 0; i < sn; ++i) {
        ca_abiz_revenue_sub_t *s = snap[i];
        if (s->handler) s->handler(s->ctx, e);
        sub_push(s, e);
    }
    free(snap);
    return (int)sn;
}

ca_abiz_revenue_sub_t *ca_abiz_revenue_loop_subscribe(ca_abiz_revenue_loop_t *l,
                                                      ca_abiz_revenue_handler_fn h,
                                                      void *ctx) {
    if (!l) return NULL;
    ca_abiz_revenue_sub_t *s =
        (ca_abiz_revenue_sub_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->handler = h;
    s->ctx = ctx;
    if (l->s_count == l->s_cap) {
        size_t nc = l->s_cap ? l->s_cap * 2 : 4;
        void *n = realloc(l->subs, nc * sizeof(*l->subs));
        if (!n) { free(s); return NULL; }
        l->subs = (ca_abiz_revenue_sub_t **)n;
        l->s_cap = nc;
    }
    l->subs[l->s_count++] = s;
    return s;
}

void ca_abiz_revenue_loop_unsubscribe(ca_abiz_revenue_loop_t *l,
                                      ca_abiz_revenue_sub_t *sub) {
    if (!l || !sub) return;
    for (size_t i = 0; i < l->s_count; ++i) {
        if (l->subs[i] == sub) {
            l->subs[i] = l->subs[l->s_count - 1];
            l->s_count--;
            sub_free(sub);
            return;
        }
    }
}

ca_abiz_revenue_event_t *ca_abiz_revenue_loop_read(const ca_abiz_revenue_loop_t *l,
                                                   int64_t since_ms,
                                                   size_t *out_count) {
    if (!out_count) return NULL;
    if (!l) { *out_count = (size_t)-1; return NULL; }
    if (l->h_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(l->h_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < l->h_count; ++i)
        if (l->history[i].at_utc_ms >= since_ms) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_abiz_revenue_event_t *out =
        (ca_abiz_revenue_event_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!revenue_copy(&out[i], &l->history[idx[i]])) {
            ca_abiz_revenue_event_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

bool ca_abiz_revenue_sub_next(ca_abiz_revenue_sub_t *sub,
                              ca_abiz_revenue_event_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!sub || !out || sub->count == 0) return false;
    *out = sub->buf[sub->head];
    sub->head = (sub->head + 1) % sub->cap;
    sub->count--;
    return true;
}
size_t ca_abiz_revenue_sub_pending(const ca_abiz_revenue_sub_t *sub) {
    return sub ? sub->count : 0;
}

const char *ca_abiz_null_revenue_loop_backend_id(void) { return "null"; }

/* ── InMemoryTreasury ───────────────────────────────────────────────────── */

int ca_abiz_treasury_snapshot(const ca_abiz_revenue_loop_t *loop,
                              const char *currency, int64_t now_ms,
                              ca_abiz_treasury_snapshot_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!loop || !out) return -1;
    const char *cur = currency ? currency : "ZAR";

    /* sum Amount over all events (since MinValue) whose Currency matches */
    ca_abiz_decimal_t bal = 0;
    for (size_t i = 0; i < loop->h_count; ++i)
        if (cab_ci_eq(loop->history[i].currency, cur))
            bal += loop->history[i].amount;

    out->balance = bal;
    out->at_utc_ms = now_ms;
    out->currency = cab_strdup(cur);
    if (!out->currency) return -1;
    return 0;
}
const char *ca_abiz_treasury_backend_id(void) { return "in-memory"; }

int ca_abiz_null_treasury_snapshot(ca_abiz_treasury_snapshot_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out) return -1;
    out->balance = 0;
    out->at_utc_ms = INT64_MIN; /* DateTimeOffset.MinValue surrogate */
    out->currency = cab_strdup("ZAR");
    if (!out->currency) return -1;
    return 0;
}
const char *ca_abiz_null_treasury_backend_id(void) { return "null"; }

/* ── InMemoryDecisionLog ────────────────────────────────────────────────── */

struct ca_abiz_decision_log {
    ca_abiz_decision_t *items;
    size_t              count, cap;
};

ca_abiz_decision_log_t *ca_abiz_decision_log_create(void) {
    return (ca_abiz_decision_log_t *)calloc(1, sizeof(ca_abiz_decision_log_t));
}
void ca_abiz_decision_log_destroy(ca_abiz_decision_log_t *l) {
    if (!l) return;
    for (size_t i = 0; i < l->count; ++i) ca_abiz_decision_free(&l->items[i]);
    free(l->items);
    free(l);
}
const char *ca_abiz_decision_log_backend_id(const ca_abiz_decision_log_t *l) {
    (void)l; return "in-memory";
}

int ca_abiz_decision_log_append(ca_abiz_decision_log_t *l,
                                const ca_abiz_decision_t *d) {
    if (!l || !d) return -1;
    ca_abiz_decision_t copy;
    if (!decision_copy(&copy, d)) return -1;
    if (l->count == l->cap) {
        size_t nc = l->cap ? l->cap * 2 : 4;
        void *n = realloc(l->items, nc * sizeof(*l->items));
        if (!n) { ca_abiz_decision_free(&copy); return -1; }
        l->items = (ca_abiz_decision_t *)n;
        l->cap = nc;
    }
    l->items[l->count++] = copy;
    return 0;
}

/* Stable descending sort of indices by AtUtc. */
static void decision_sort_desc(const ca_abiz_decision_log_t *l, size_t *idx,
                               size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = l->items[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && l->items[idx[j - 1]].at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_abiz_decision_t *ca_abiz_decision_log_read(const ca_abiz_decision_log_t *l,
                                              int limit, size_t *out_count) {
    if (!out_count) return NULL;
    if (!l || limit <= 0) { *out_count = (size_t)-1; return NULL; }
    if (l->count == 0) { *out_count = 0; return NULL; }

    size_t n = l->count;
    size_t *idx = (size_t *)malloc(n * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) idx[i] = i;
    decision_sort_desc(l, idx, n);
    if ((size_t)limit < n) n = (size_t)limit;

    ca_abiz_decision_t *out = (ca_abiz_decision_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!decision_copy(&out[i], &l->items[idx[i]])) {
            ca_abiz_decision_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

const char *ca_abiz_null_decision_log_backend_id(void) { return "null"; }
