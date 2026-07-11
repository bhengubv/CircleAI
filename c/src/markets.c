/*
 * markets.c — CircleAI.Markets (C11 port of Contracts.cs + InMemoryMarkets.cs).
 *
 * Three backends over linear arrays:
 *   InMemoryInstrumentCatalog — Symbol keyed (case-insensitive) instrument set.
 *   InMemoryMarketDataFeed    — latest-quote-by-symbol + per-symbol subscriber
 *                               fan-out (snapshot-before-invoke; per-sub cursor).
 *   InMemoryOrderRouter       — validates a request against the injected catalog.
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/markets.h"
#include "board_common.h"
#include <stdio.h>

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_mkt_instrument_free(ca_mkt_instrument_t *i) {
    if (!i) return;
    free(i->symbol);
    free(i->exchange);
    free(i->currency);
    free(i->asset_class);
    i->symbol = i->exchange = i->currency = i->asset_class = NULL;
}
void ca_mkt_instrument_free_array(ca_mkt_instrument_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_mkt_instrument_free(&arr[i]);
    free(arr);
}

static bool instrument_copy(ca_mkt_instrument_t *dst,
                            const ca_mkt_instrument_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->symbol      = cab_strdup_empty(src->symbol);
    dst->exchange    = cab_strdup_empty(src->exchange);
    dst->currency    = cab_strdup_empty(src->currency);
    dst->asset_class = cab_strdup_empty(src->asset_class);
    if (!dst->symbol || !dst->exchange || !dst->currency || !dst->asset_class) {
        ca_mkt_instrument_free(dst);
        return false;
    }
    return true;
}

void ca_mkt_quote_free(ca_mkt_quote_t *q) {
    if (!q) return;
    free(q->symbol);
    q->symbol = NULL;
}

static bool quote_copy(ca_mkt_quote_t *dst, const ca_mkt_quote_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->symbol    = cab_strdup_empty(src->symbol);
    dst->bid       = src->bid;
    dst->ask       = src->ask;
    dst->last      = src->last;
    dst->at_utc_ms = src->at_utc_ms;
    if (!dst->symbol) return false;
    return true;
}

void ca_mkt_order_result_free(ca_mkt_order_result_t *r) {
    if (!r) return;
    free(r->order_id);
    free(r->failure_reason);
    r->order_id = r->failure_reason = NULL;
    r->has_failure_reason = false;
}

/* Build an OrderResult(order_id, accepted, reason?) into out. reason NULL => C#
 * null FailureReason. Returns false on OOM (out left zeroed). */
static bool order_result_make(ca_mkt_order_result_t *out, const char *order_id,
                              bool accepted, const char *reason) {
    memset(out, 0, sizeof(*out));
    out->order_id = cab_strdup_empty(order_id);
    if (!out->order_id) return false;
    out->accepted = accepted;
    if (reason) {
        out->failure_reason = cab_strdup_empty(reason);
        if (!out->failure_reason) { ca_mkt_order_result_free(out); return false; }
        out->has_failure_reason = true;
    }
    return true;
}

/* ── InMemoryInstrumentCatalog ──────────────────────────────────────────── */

struct ca_mkt_catalog {
    ca_mkt_instrument_t *items;
    size_t               count, cap;
};

ca_mkt_catalog_t *ca_mkt_catalog_create(void) {
    return (ca_mkt_catalog_t *)calloc(1, sizeof(ca_mkt_catalog_t));
}
void ca_mkt_catalog_destroy(ca_mkt_catalog_t *c) {
    if (!c) return;
    for (size_t i = 0; i < c->count; ++i) ca_mkt_instrument_free(&c->items[i]);
    free(c->items);
    free(c);
}
const char *ca_mkt_catalog_backend_id(const ca_mkt_catalog_t *c) {
    (void)c;
    return "in-memory";
}

int ca_mkt_catalog_add(ca_mkt_catalog_t *c, const ca_mkt_instrument_t *item) {
    if (!c || !item) return -1;
    /* Case-insensitive dictionary keyed by Symbol. */
    for (size_t i = 0; i < c->count; ++i) {
        if (cab_ci_eq(c->items[i].symbol, item->symbol)) {
            ca_mkt_instrument_t copy;
            if (!instrument_copy(&copy, item)) return -1;
            ca_mkt_instrument_free(&c->items[i]);
            c->items[i] = copy;
            return 0;
        }
    }
    ca_mkt_instrument_t copy;
    if (!instrument_copy(&copy, item)) return -1;
    if (c->count == c->cap) {
        size_t nc = c->cap ? c->cap * 2 : 4;
        void *n = realloc(c->items, nc * sizeof(*c->items));
        if (!n) { ca_mkt_instrument_free(&copy); return -1; }
        c->items = (ca_mkt_instrument_t *)n;
        c->cap = nc;
    }
    c->items[c->count++] = copy;
    return 0;
}

bool ca_mkt_catalog_get(const ca_mkt_catalog_t *c, const char *symbol,
                        ca_mkt_instrument_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!c || cab_is_ws(symbol) || !out) return false;
    for (size_t i = 0; i < c->count; ++i)
        if (cab_ci_eq(c->items[i].symbol, symbol))
            return instrument_copy(out, &c->items[i]);
    return false;
}

/* Stable ascending sort of collected indices by Symbol (ordinal). */
static void catalog_sort_symbol(const ca_mkt_catalog_t *c, size_t *idx,
                                size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 &&
               strcmp(c->items[idx[j - 1]].symbol, c->items[key].symbol) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_mkt_instrument_t *ca_mkt_catalog_search(const ca_mkt_catalog_t *c,
                                           const char *query, int top_k,
                                           size_t *out_count) {
    if (!out_count) return NULL;
    if (!c || !query || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    if (c->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(c->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < c->count; ++i)
        if (cab_ci_contains(c->items[i].symbol, query)) idx[n++] = i;
    catalog_sort_symbol(c, idx, n);
    if ((size_t)top_k < n) n = (size_t)top_k;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_mkt_instrument_t *out = (ca_mkt_instrument_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!instrument_copy(&out[i], &c->items[idx[i]])) {
            ca_mkt_instrument_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

/* ── InMemoryMarketDataFeed ─────────────────────────────────────────────── */

/* Per-subscriber cursor: buffered quotes delivered but not yet drained. */
struct ca_mkt_feed_sub {
    char                   *symbol;   /* owned; the subscribed symbol */
    ca_mkt_quote_handler_fn handler;
    void                   *ctx;
    ca_mkt_quote_t         *buf;      /* ring-less FIFO of buffered copies */
    size_t                  head, count, cap;
};

typedef struct {
    char             *symbol;  /* owned */
    ca_mkt_quote_t    quote;   /* owned latest quote */
    bool              has_quote;
} feed_quote_slot_t;

struct ca_mkt_feed {
    feed_quote_slot_t  *quotes;
    size_t              q_count, q_cap;
    ca_mkt_feed_sub_t **subs;    /* borrowed pointers (owned tokens) */
    size_t              s_count, s_cap;
};

ca_mkt_feed_t *ca_mkt_feed_create(void) {
    return (ca_mkt_feed_t *)calloc(1, sizeof(ca_mkt_feed_t));
}

static void feed_sub_free(ca_mkt_feed_sub_t *sub) {
    if (!sub) return;
    for (size_t i = 0; i < sub->count; ++i)
        ca_mkt_quote_free(&sub->buf[(sub->head + i) % sub->cap]);
    free(sub->buf);
    free(sub->symbol);
    free(sub);
}

void ca_mkt_feed_destroy(ca_mkt_feed_t *f) {
    if (!f) return;
    for (size_t i = 0; i < f->q_count; ++i) {
        free(f->quotes[i].symbol);
        if (f->quotes[i].has_quote) ca_mkt_quote_free(&f->quotes[i].quote);
    }
    for (size_t i = 0; i < f->s_count; ++i) feed_sub_free(f->subs[i]);
    free(f->quotes);
    free(f->subs);
    free(f);
}
const char *ca_mkt_feed_backend_id(const ca_mkt_feed_t *f) {
    (void)f;
    return "in-memory";
}

/* Buffer a fresh copy of q onto sub's cursor. Returns false on OOM. */
static bool feed_sub_push(ca_mkt_feed_sub_t *sub, const ca_mkt_quote_t *q) {
    if (sub->count == sub->cap) {
        size_t nc = sub->cap ? sub->cap * 2 : 4;
        ca_mkt_quote_t *nb = (ca_mkt_quote_t *)calloc(nc, sizeof(*nb));
        if (!nb) return false;
        for (size_t i = 0; i < sub->count; ++i)
            nb[i] = sub->buf[(sub->head + i) % sub->cap];
        free(sub->buf);
        sub->buf = nb;
        sub->cap = nc;
        sub->head = 0;
    }
    ca_mkt_quote_t copy;
    if (!quote_copy(&copy, q)) return false;
    sub->buf[(sub->head + sub->count) % sub->cap] = copy;
    sub->count++;
    return true;
}

int ca_mkt_feed_publish(ca_mkt_feed_t *f, const ca_mkt_quote_t *q) {
    if (!f || !q) return -1;
    /* Store latest quote by Symbol (case-insensitive). */
    feed_quote_slot_t *slot = NULL;
    for (size_t i = 0; i < f->q_count; ++i)
        if (cab_ci_eq(f->quotes[i].symbol, q->symbol)) { slot = &f->quotes[i]; break; }
    if (!slot) {
        if (f->q_count == f->q_cap) {
            size_t nc = f->q_cap ? f->q_cap * 2 : 4;
            void *n = realloc(f->quotes, nc * sizeof(*f->quotes));
            if (!n) return -1;
            f->quotes = (feed_quote_slot_t *)n;
            f->q_cap = nc;
        }
        slot = &f->quotes[f->q_count];
        memset(slot, 0, sizeof(*slot));
        slot->symbol = cab_strdup_empty(q->symbol);
        if (!slot->symbol) return -1;
        f->q_count++;
    }
    ca_mkt_quote_t qcopy;
    if (!quote_copy(&qcopy, q)) return -1;
    if (slot->has_quote) ca_mkt_quote_free(&slot->quote);
    slot->quote = qcopy;
    slot->has_quote = true;

    /* Snapshot the matching subscriber list before invoking, so a handler that
     * unsubscribes mid-fan-out is safe (mirrors list.ToArray()). */
    size_t nsub = 0;
    for (size_t i = 0; i < f->s_count; ++i)
        if (cab_ci_eq(f->subs[i]->symbol, q->symbol)) nsub++;
    if (nsub == 0) return 0;

    ca_mkt_feed_sub_t **snap =
        (ca_mkt_feed_sub_t **)malloc(nsub * sizeof(*snap));
    if (!snap) return -1;
    size_t k = 0;
    for (size_t i = 0; i < f->s_count; ++i)
        if (cab_ci_eq(f->subs[i]->symbol, q->symbol)) snap[k++] = f->subs[i];

    for (size_t i = 0; i < nsub; ++i) {
        ca_mkt_feed_sub_t *sub = snap[i];
        feed_sub_push(sub, q); /* buffer for cursor drain (best-effort) */
        if (sub->handler) sub->handler(sub->ctx, q);
    }
    free(snap);
    return (int)nsub;
}

bool ca_mkt_feed_get_quote(const ca_mkt_feed_t *f, const char *symbol,
                           ca_mkt_quote_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!f || cab_is_ws(symbol) || !out) return false;
    for (size_t i = 0; i < f->q_count; ++i)
        if (cab_ci_eq(f->quotes[i].symbol, symbol) && f->quotes[i].has_quote)
            return quote_copy(out, &f->quotes[i].quote);
    return false;
}

ca_mkt_feed_sub_t *ca_mkt_feed_subscribe(ca_mkt_feed_t *f, const char *symbol,
                                         ca_mkt_quote_handler_fn handler,
                                         void *ctx) {
    if (!f || cab_is_ws(symbol) || !handler) return NULL;
    ca_mkt_feed_sub_t *sub =
        (ca_mkt_feed_sub_t *)calloc(1, sizeof(*sub));
    if (!sub) return NULL;
    sub->symbol = cab_strdup_empty(symbol);
    if (!sub->symbol) { free(sub); return NULL; }
    sub->handler = handler;
    sub->ctx = ctx;
    if (f->s_count == f->s_cap) {
        size_t nc = f->s_cap ? f->s_cap * 2 : 4;
        void *n = realloc(f->subs, nc * sizeof(*f->subs));
        if (!n) { free(sub->symbol); free(sub); return NULL; }
        f->subs = (ca_mkt_feed_sub_t **)n;
        f->s_cap = nc;
    }
    f->subs[f->s_count++] = sub;
    return sub;
}

void ca_mkt_feed_unsubscribe(ca_mkt_feed_t *f, ca_mkt_feed_sub_t *sub) {
    if (!f || !sub) return;
    for (size_t i = 0; i < f->s_count; ++i) {
        if (f->subs[i] == sub) {
            memmove(&f->subs[i], &f->subs[i + 1],
                    (f->s_count - i - 1) * sizeof(*f->subs));
            f->s_count--;
            feed_sub_free(sub);
            return;
        }
    }
}

bool ca_mkt_feed_sub_next(ca_mkt_feed_sub_t *sub, ca_mkt_quote_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!sub || !out || sub->count == 0) return false;
    *out = sub->buf[sub->head];
    sub->head = (sub->head + 1) % sub->cap;
    sub->count--;
    return true;
}

size_t ca_mkt_feed_sub_pending(const ca_mkt_feed_sub_t *sub) {
    return sub ? sub->count : 0;
}

/* ── InMemoryOrderRouter ────────────────────────────────────────────────── */

struct ca_mkt_router {
    const ca_mkt_catalog_t *catalog; /* borrowed */
    long long               seq;
};

ca_mkt_router_t *ca_mkt_router_create(const ca_mkt_catalog_t *catalog) {
    if (!catalog) return NULL;
    ca_mkt_router_t *r = (ca_mkt_router_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->catalog = catalog;
    r->seq = 0;
    return r;
}
void ca_mkt_router_destroy(ca_mkt_router_t *r) { free(r); }
const char *ca_mkt_router_backend_id(const ca_mkt_router_t *r) {
    (void)r;
    return "in-memory";
}

/* NextId() => "ord-{Interlocked.Increment(ref _seq)}" (first is ord-1). */
static bool router_next_id(ca_mkt_router_t *r, char *buf, size_t buflen) {
    long long id = ++r->seq;
    int w = snprintf(buf, buflen, "ord-%lld", id);
    return w > 0 && (size_t)w < buflen;
}

int ca_mkt_router_submit(ca_mkt_router_t *r, const ca_mkt_order_request_t *req,
                         ca_mkt_order_result_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!r || !req || !out) return -1;

    char id[64];

    if (req->quantity <= 0) {
        if (!router_next_id(r, id, sizeof(id))) return -1;
        return order_result_make(out, id, false, "Quantity must be positive")
                   ? 0 : -1;
    }
    if (req->type == CA_MKT_TYPE_LIMIT &&
        (!req->has_limit_price || req->limit_price <= 0)) {
        if (!router_next_id(r, id, sizeof(id))) return -1;
        return order_result_make(out, id, false,
                                 "Limit order requires positive LimitPrice")
                   ? 0 : -1;
    }

    /* await _catalog.GetAsync(req.Symbol): unknown symbol => reject. Note the
     * catalog GetAsync throws on whitespace symbol; mirror by treating a
     * whitespace symbol as "unknown" only after passing the earlier gates. */
    ca_mkt_instrument_t inst;
    bool known = ca_mkt_catalog_get(r->catalog, req->symbol, &inst);
    if (known) ca_mkt_instrument_free(&inst);
    if (!known) {
        if (!router_next_id(r, id, sizeof(id))) return -1;
        return order_result_make(out, id, false, "Unknown symbol") ? 0 : -1;
    }

    if (!router_next_id(r, id, sizeof(id))) return -1;
    return order_result_make(out, id, true, NULL) ? 0 : -1;
}
