/*
 * commerce_xero.c — CircleAI.Commerce.Integration.Xero (C11 port of
 * XeroPrimitives.cs).
 *
 * InMemoryXeroBoard: tokens keyed by userId, per-user tenant lists (deduped by
 * TenantId), webhook events in an appended list. Pure C11 + libc. No pthreads.
 */

#include "circle_ai/commerce_xero.h"
#include "board_common.h"

/* ── XeroTokens ─────────────────────────────────────────────────────────── */

void ca_xero_tokens_free(ca_xero_tokens_t *t) {
    if (!t) return;
    free(t->access_token);
    free(t->refresh_token);
    free(t->id_token);
    t->access_token = t->refresh_token = t->id_token = NULL;
}

static bool tokens_copy(ca_xero_tokens_t *dst, const ca_xero_tokens_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->access_token  = cab_strdup_empty(src->access_token);
    dst->refresh_token = cab_strdup_empty(src->refresh_token);
    dst->id_token      = cab_strdup_empty(src->id_token);
    dst->expires_at_utc_ms = src->expires_at_utc_ms;
    if (!dst->access_token || !dst->refresh_token || !dst->id_token) {
        ca_xero_tokens_free(dst);
        return false;
    }
    return true;
}

/* ── XeroTenant ─────────────────────────────────────────────────────────── */

void ca_xero_tenant_free(ca_xero_tenant_t *t) {
    if (!t) return;
    free(t->tenant_id);
    free(t->tenant_name);
    free(t->tenant_type);
    t->tenant_id = t->tenant_name = t->tenant_type = NULL;
}
void ca_xero_tenant_free_array(ca_xero_tenant_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_xero_tenant_free(&arr[i]);
    free(arr);
}

static bool tenant_copy(ca_xero_tenant_t *dst, const ca_xero_tenant_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->tenant_id   = cab_strdup_empty(src->tenant_id);
    dst->tenant_name = cab_strdup_empty(src->tenant_name);
    dst->tenant_type = cab_strdup_empty(src->tenant_type);
    if (!dst->tenant_id || !dst->tenant_name || !dst->tenant_type) {
        ca_xero_tenant_free(dst);
        return false;
    }
    return true;
}

/* ── XeroWebhookEvent ───────────────────────────────────────────────────── */

void ca_xero_event_free(ca_xero_event_t *e) {
    if (!e) return;
    free(e->tenant_id);
    free(e->resource_type);
    free(e->resource_id);
    e->tenant_id = e->resource_type = e->resource_id = NULL;
}
void ca_xero_event_free_array(ca_xero_event_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_xero_event_free(&arr[i]);
    free(arr);
}

static bool event_copy(ca_xero_event_t *dst, const ca_xero_event_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->tenant_id     = cab_strdup_empty(src->tenant_id);
    dst->resource_type = cab_strdup_empty(src->resource_type);
    dst->resource_id   = cab_strdup_empty(src->resource_id);
    dst->at_utc_ms     = src->at_utc_ms;
    if (!dst->tenant_id || !dst->resource_type || !dst->resource_id) {
        ca_xero_event_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

/* One userId -> tokens slot. */
typedef struct {
    char            *user_id;  /* owned */
    ca_xero_tokens_t tokens;   /* owned */
} xero_token_slot_t;

/* One userId -> tenant list slot. */
typedef struct {
    char             *user_id;  /* owned */
    ca_xero_tenant_t *tenants;  /* owned */
    size_t            count, cap;
} xero_tenant_slot_t;

struct ca_xero_board {
    xero_token_slot_t  *tokens;
    size_t              token_count, token_cap;
    xero_tenant_slot_t *tenants;
    size_t              tenant_count, tenant_cap;
    ca_xero_event_t    *events;
    size_t              event_count, event_cap;
};

ca_xero_board_t *ca_xero_board_create(void) {
    return (ca_xero_board_t *)calloc(1, sizeof(ca_xero_board_t));
}
void ca_xero_board_destroy(ca_xero_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->token_count; ++i) {
        free(b->tokens[i].user_id);
        ca_xero_tokens_free(&b->tokens[i].tokens);
    }
    free(b->tokens);
    for (size_t i = 0; i < b->tenant_count; ++i) {
        free(b->tenants[i].user_id);
        for (size_t k = 0; k < b->tenants[i].count; ++k)
            ca_xero_tenant_free(&b->tenants[i].tenants[k]);
        free(b->tenants[i].tenants);
    }
    free(b->tenants);
    for (size_t i = 0; i < b->event_count; ++i) ca_xero_event_free(&b->events[i]);
    free(b->events);
    free(b);
}

int ca_xero_board_store_tokens(ca_xero_board_t *b, const char *user_id,
                               const ca_xero_tokens_t *t) {
    if (!b || !user_id || !t) return -1;
    for (size_t i = 0; i < b->token_count; ++i) {
        if (cab_ord_eq(b->tokens[i].user_id, user_id)) {
            ca_xero_tokens_t copy;
            if (!tokens_copy(&copy, t)) return -1;
            ca_xero_tokens_free(&b->tokens[i].tokens);
            b->tokens[i].tokens = copy;
            return 0;
        }
    }
    ca_xero_tokens_t copy;
    if (!tokens_copy(&copy, t)) return -1;
    char *uid = cab_strdup(user_id);
    if (!uid) { ca_xero_tokens_free(&copy); return -1; }
    if (b->token_count == b->token_cap) {
        size_t nc = b->token_cap ? b->token_cap * 2 : 4;
        void *n = realloc(b->tokens, nc * sizeof(*b->tokens));
        if (!n) { free(uid); ca_xero_tokens_free(&copy); return -1; }
        b->tokens = (xero_token_slot_t *)n;
        b->token_cap = nc;
    }
    b->tokens[b->token_count].user_id = uid;
    b->tokens[b->token_count].tokens  = copy;
    b->token_count++;
    return 0;
}

static const ca_xero_tokens_t *token_find(const ca_xero_board_t *b,
                                          const char *user_id) {
    for (size_t i = 0; i < b->token_count; ++i)
        if (cab_ord_eq(b->tokens[i].user_id, user_id))
            return &b->tokens[i].tokens;
    return NULL;
}

bool ca_xero_board_get_tokens(const ca_xero_board_t *b, const char *user_id,
                              ca_xero_tokens_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !user_id || !out) return false;
    const ca_xero_tokens_t *t = token_find(b, user_id);
    if (!t) return false;
    return tokens_copy(out, t);
}

bool ca_xero_board_tokens_expired(const ca_xero_board_t *b, const char *user_id,
                                  int64_t now_ms) {
    if (!b || !user_id) return true;
    const ca_xero_tokens_t *t = token_find(b, user_id);
    if (!t) return true;   /* no tokens -> expired */
    return now_ms >= t->expires_at_utc_ms;
}

static xero_tenant_slot_t *tenant_slot_get_or_add(ca_xero_board_t *b,
                                                  const char *user_id) {
    for (size_t i = 0; i < b->tenant_count; ++i)
        if (cab_ord_eq(b->tenants[i].user_id, user_id)) return &b->tenants[i];
    if (b->tenant_count == b->tenant_cap) {
        size_t nc = b->tenant_cap ? b->tenant_cap * 2 : 4;
        void *n = realloc(b->tenants, nc * sizeof(*b->tenants));
        if (!n) return NULL;
        b->tenants = (xero_tenant_slot_t *)n;
        b->tenant_cap = nc;
    }
    xero_tenant_slot_t *s = &b->tenants[b->tenant_count];
    memset(s, 0, sizeof(*s));
    s->user_id = cab_strdup(user_id);
    if (!s->user_id) return NULL;
    b->tenant_count++;
    return s;
}

int ca_xero_board_add_tenant(ca_xero_board_t *b, const char *user_id,
                             const ca_xero_tenant_t *t) {
    if (!b || !user_id || !t) return -1;
    xero_tenant_slot_t *s = tenant_slot_get_or_add(b, user_id);
    if (!s) return -1;
    /* dedup by TenantId (list.Any(x => x.TenantId == t.TenantId)). */
    for (size_t i = 0; i < s->count; ++i)
        if (cab_ord_eq(s->tenants[i].tenant_id, t->tenant_id)) return 0;

    ca_xero_tenant_t copy;
    if (!tenant_copy(&copy, t)) return -1;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->tenants, nc * sizeof(*s->tenants));
        if (!n) { ca_xero_tenant_free(&copy); return -1; }
        s->tenants = (ca_xero_tenant_t *)n;
        s->cap = nc;
    }
    s->tenants[s->count++] = copy;
    return 0;
}

ca_xero_tenant_t *ca_xero_board_tenants_for(const ca_xero_board_t *b,
                                            const char *user_id,
                                            size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !user_id) { *out_count = (size_t)-1; return NULL; }
    const xero_tenant_slot_t *s = NULL;
    for (size_t i = 0; i < b->tenant_count; ++i)
        if (cab_ord_eq(b->tenants[i].user_id, user_id)) { s = &b->tenants[i]; break; }
    if (!s || s->count == 0) { *out_count = 0; return NULL; }

    ca_xero_tenant_t *out = (ca_xero_tenant_t *)calloc(s->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < s->count; ++i) {
        if (!tenant_copy(&out[i], &s->tenants[i])) {
            ca_xero_tenant_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = s->count;
    return out;
}

int ca_xero_board_record_webhook(ca_xero_board_t *b, const ca_xero_event_t *e) {
    if (!b || !e) return -1;
    ca_xero_event_t copy;
    if (!event_copy(&copy, e)) return -1;
    if (b->event_count == b->event_cap) {
        size_t nc = b->event_cap ? b->event_cap * 2 : 4;
        void *n = realloc(b->events, nc * sizeof(*b->events));
        if (!n) { ca_xero_event_free(&copy); return -1; }
        b->events = (ca_xero_event_t *)n;
        b->event_cap = nc;
    }
    b->events[b->event_count++] = copy;
    return 0;
}

/* Stable descending sort of an index array by event at_utc_ms. */
static void event_sort_desc(const ca_xero_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int64_t kt = b->events[key].at_utc_ms;
        size_t j = i;
        while (j > 0 && b->events[idx[j - 1]].at_utc_ms < kt) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_xero_event_t *ca_xero_board_recent_events(const ca_xero_board_t *b, int limit,
                                             size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || limit < 0) { *out_count = (size_t)-1; return NULL; }
    if (b->event_count == 0 || limit == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->event_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < b->event_count; ++i) idx[i] = i;
    event_sort_desc(b, idx, b->event_count);

    size_t n = b->event_count;
    if (n > (size_t)limit) n = (size_t)limit;   /* Take(limit) after ordering */
    ca_xero_event_t *out = (ca_xero_event_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!event_copy(&out[i], &b->events[idx[i]])) {
            ca_xero_event_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}
