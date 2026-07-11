/*
 * microagents.c — CircleAI.MicroAgents (C11 port).
 *
 * FuncMicroAgent / NullMicroAgent build a ca_ma_agent_t vtable. The host keeps
 * borrowed agent pointers keyed by AgentId. Search + invocation log are
 * deterministic linear scans.
 *
 * Pure C11 + libc. No pthreads.
 */

#include "circle_ai/microagents.h"
#include "board_common.h"

/* ── MicroAgentDescriptor ───────────────────────────────────────────────── */

void ca_ma_descriptor_free(ca_ma_descriptor_t *d) {
    if (!d) return;
    free(d->agent_id);
    free(d->description);
    cab_strv_free(d->capabilities, d->capability_count);
    d->agent_id = d->description = NULL;
    d->capabilities = NULL;
    d->capability_count = 0;
}
void ca_ma_descriptor_free_array(ca_ma_descriptor_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_ma_descriptor_free(&arr[i]);
    free(arr);
}
static bool descriptor_copy(ca_ma_descriptor_t *dst,
                            const ca_ma_descriptor_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->agent_id    = cab_strdup_empty(src->agent_id);
    dst->description = cab_strdup_empty(src->description);
    if (!dst->agent_id || !dst->description) { ca_ma_descriptor_free(dst); return false; }
    if (!cab_strv_copy(&dst->capabilities, src->capabilities, src->capability_count)) {
        ca_ma_descriptor_free(dst);
        return false;
    }
    dst->capability_count = src->capability_count;
    return true;
}

/* ── MicroAgentResponse ─────────────────────────────────────────────────── */

void ca_ma_response_free(ca_ma_response_t *r) {
    if (!r) return;
    free(r->agent_id);
    free(r->output);
    cab_strv_free(r->meta_keys, r->meta_count);
    cab_strv_free(r->meta_values, r->meta_count);
    r->agent_id = r->output = NULL;
    r->meta_keys = r->meta_values = NULL;
    r->meta_count = 0;
    r->has_metadata = false;
}

/* ── MicroAgentInvocation ───────────────────────────────────────────────── */

void ca_ma_invocation_free(ca_ma_invocation_t *i) {
    if (!i) return;
    free(i->agent_id);
    free(i->input);
    free(i->response_text);
    i->agent_id = i->input = i->response_text = NULL;
}
void ca_ma_invocation_free_array(ca_ma_invocation_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_ma_invocation_free(&arr[i]);
    free(arr);
}
static bool invocation_copy(ca_ma_invocation_t *dst,
                            const ca_ma_invocation_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->at_utc_ms     = src->at_utc_ms;
    dst->agent_id      = cab_strdup_empty(src->agent_id);
    dst->input         = cab_strdup_empty(src->input);
    dst->response_text = cab_strdup_empty(src->response_text);
    if (!dst->agent_id || !dst->input || !dst->response_text) {
        ca_ma_invocation_free(dst);
        return false;
    }
    return true;
}

/* ── FuncMicroAgent / NullMicroAgent ────────────────────────────────────── */

int ca_ma_func_agent(const char *agent_id, const char *description,
                     char *const *capabilities, size_t capability_count,
                     ca_ma_invoke_fn invoke, void *ctx, ca_ma_agent_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out || cab_is_ws(agent_id) || !invoke) return -1;
    ca_ma_descriptor_t d;
    memset(&d, 0, sizeof(d));
    d.agent_id    = cab_strdup(agent_id);
    d.description = cab_strdup_empty(description ? description : "");
    if (!d.agent_id || !d.description) { ca_ma_descriptor_free(&d); return -1; }
    if (!cab_strv_copy(&d.capabilities, capabilities, capability_count)) {
        ca_ma_descriptor_free(&d);
        return -1;
    }
    d.capability_count = capability_count;

    out->descriptor  = d;
    out->agent_id    = d.agent_id;   /* borrows the owned descriptor string */
    out->backend_id  = "func";
    out->invoke      = invoke;
    out->ctx         = ctx;
    return 0;
}

void ca_ma_agent_free(ca_ma_agent_t *a) {
    if (!a) return;
    ca_ma_descriptor_free(&a->descriptor);
    a->agent_id = a->backend_id = NULL;
    a->invoke = NULL;
    a->ctx = NULL;
}

static int null_agent_invoke(void *ctx, const char *input,
                             ca_ma_response_t *out) {
    (void)ctx; (void)input;
    memset(out, 0, sizeof(*out));
    out->agent_id = cab_strdup("null");
    out->output   = cab_strdup_empty("");
    if (!out->agent_id || !out->output) { ca_ma_response_free(out); return -1; }
    return 0;
}

int ca_ma_null_agent(ca_ma_agent_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out) return -1;
    ca_ma_descriptor_t d;
    memset(&d, 0, sizeof(d));
    d.agent_id    = cab_strdup("null");
    d.description = cab_strdup("No-op micro agent");
    if (!d.agent_id || !d.description) { ca_ma_descriptor_free(&d); return -1; }
    d.capabilities = NULL;
    d.capability_count = 0;

    out->descriptor = d;
    out->agent_id   = d.agent_id;
    out->backend_id = "null";
    out->invoke     = null_agent_invoke;
    out->ctx        = NULL;
    return 0;
}

bool ca_ma_agent_invoke(const ca_ma_agent_t *a, const char *input,
                        ca_ma_response_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!a || !a->invoke || !out) return false;
    return a->invoke(a->ctx, input, out) == 0;
}

const char *ca_ma_null_agent_backend_id(void) { return "null"; }

/* ── InMemoryMicroAgentHost ─────────────────────────────────────────────── */

struct ca_ma_host {
    const ca_ma_agent_t **agents;   /* borrowed pointers, keyed by AgentId */
    size_t                count, cap;
};

ca_ma_host_t *ca_ma_host_create(void) {
    return (ca_ma_host_t *)calloc(1, sizeof(ca_ma_host_t));
}
void ca_ma_host_destroy(ca_ma_host_t *h) {
    if (!h) return;
    free(h->agents);
    free(h);
}
const char *ca_ma_host_backend_id(const ca_ma_host_t *h) {
    (void)h; return "in-memory";
}

int ca_ma_host_register(ca_ma_host_t *h, const ca_ma_agent_t *agent) {
    if (!h || !agent || !agent->agent_id) return -1;
    for (size_t i = 0; i < h->count; ++i) {
        if (cab_ord_eq(h->agents[i]->agent_id, agent->agent_id)) {
            h->agents[i] = agent;
            return 0;
        }
    }
    if (h->count == h->cap) {
        size_t nc = h->cap ? h->cap * 2 : 4;
        void *n = realloc(h->agents, nc * sizeof(*h->agents));
        if (!n) return -1;
        h->agents = (const ca_ma_agent_t **)n;
        h->cap = nc;
    }
    h->agents[h->count++] = agent;
    return 0;
}

ca_ma_descriptor_t *ca_ma_host_list(const ca_ma_host_t *h, size_t *out_count) {
    if (!out_count) return NULL;
    if (!h) { *out_count = (size_t)-1; return NULL; }
    if (h->count == 0) { *out_count = 0; return NULL; }
    ca_ma_descriptor_t *out = (ca_ma_descriptor_t *)calloc(h->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < h->count; ++i) {
        if (!descriptor_copy(&out[i], &h->agents[i]->descriptor)) {
            ca_ma_descriptor_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = h->count;
    return out;
}

bool ca_ma_host_invoke(const ca_ma_host_t *h, const char *agent_id,
                       const char *input, ca_ma_response_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!h || !agent_id || !out) return false;
    for (size_t i = 0; i < h->count; ++i)
        if (cab_ord_eq(h->agents[i]->agent_id, agent_id))
            return ca_ma_agent_invoke(h->agents[i], input, out);
    return false;
}

/* ── MicroAgentSearch ───────────────────────────────────────────────────── */

/* Stable ascending sort of indices by AgentId (ordinal). */
static void sort_by_agent_id(const ca_ma_descriptor_t *all, size_t *idx,
                             size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        size_t j = i;
        while (j > 0 && strcmp(all[idx[j - 1]].agent_id, all[key].agent_id) > 0) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

static ca_ma_descriptor_t *materialise(const ca_ma_descriptor_t *all,
                                       const size_t *idx, size_t n,
                                       size_t *out_count) {
    if (n == 0) { *out_count = 0; return NULL; }
    ca_ma_descriptor_t *out = (ca_ma_descriptor_t *)calloc(n, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!descriptor_copy(&out[i], &all[idx[i]])) {
            ca_ma_descriptor_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = n;
    return out;
}

ca_ma_descriptor_t *ca_ma_search_by_capability(const ca_ma_descriptor_t *all,
                                               size_t all_count,
                                               const char *capability,
                                               size_t *out_count) {
    if (!out_count) return NULL;
    if ((!all && all_count) || cab_is_ws(capability)) { *out_count = (size_t)-1; return NULL; }
    if (all_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(all_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < all_count; ++i) {
        if (cab_strv_ci_contains(all[i].capabilities, all[i].capability_count, capability))
            idx[n++] = i;
    }
    sort_by_agent_id(all, idx, n);
    ca_ma_descriptor_t *out = materialise(all, idx, n, out_count);
    free(idx);
    return out;
}

/* Does the descriptor match `query` (case-insensitive substring in AgentId /
 * Description / any Capability)? */
static bool descriptor_matches(const ca_ma_descriptor_t *d, const char *query) {
    if (cab_ci_contains(d->agent_id, query)) return true;
    if (cab_ci_contains(d->description, query)) return true;
    for (size_t i = 0; i < d->capability_count; ++i)
        if (cab_ci_contains(d->capabilities[i], query)) return true;
    return false;
}

ca_ma_descriptor_t *ca_ma_search(const ca_ma_descriptor_t *all, size_t all_count,
                                 const char *query, int top_k,
                                 size_t *out_count) {
    if (!out_count) return NULL;
    if ((!all && all_count) || !query || top_k <= 0) { *out_count = (size_t)-1; return NULL; }
    if (all_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(all_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < all_count && n < (size_t)top_k; ++i)
        if (descriptor_matches(&all[i], query)) idx[n++] = i;
    /* Take(topK) preserves input order (no sort). */
    ca_ma_descriptor_t *out = materialise(all, idx, n, out_count);
    free(idx);
    return out;
}

/* ── MicroAgentInvocationLog ────────────────────────────────────────────── */

struct ca_ma_log {
    ca_ma_invocation_t *items;
    size_t              count, cap;
};

ca_ma_log_t *ca_ma_log_create(void) {
    return (ca_ma_log_t *)calloc(1, sizeof(ca_ma_log_t));
}
void ca_ma_log_destroy(ca_ma_log_t *l) {
    if (!l) return;
    for (size_t i = 0; i < l->count; ++i) ca_ma_invocation_free(&l->items[i]);
    free(l->items);
    free(l);
}

int ca_ma_log_append(ca_ma_log_t *l, const ca_ma_invocation_t *inv) {
    if (!l || !inv) return -1;
    ca_ma_invocation_t copy;
    if (!invocation_copy(&copy, inv)) return -1;
    if (l->count == l->cap) {
        size_t nc = l->cap ? l->cap * 2 : 4;
        void *n = realloc(l->items, nc * sizeof(*l->items));
        if (!n) { ca_ma_invocation_free(&copy); return -1; }
        l->items = (ca_ma_invocation_t *)n;
        l->cap = nc;
    }
    l->items[l->count++] = copy;
    return 0;
}

/* Stable descending sort of indices by AtUtc. */
static void log_sort_desc(const ca_ma_log_t *l, size_t *idx, size_t n) {
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

ca_ma_invocation_t *ca_ma_log_for_agent(const ca_ma_log_t *l,
                                        const char *agent_id, int limit,
                                        size_t *out_count) {
    if (!out_count) return NULL;
    if (!l || !agent_id || limit <= 0) { *out_count = (size_t)-1; return NULL; }
    if (l->count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(l->count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < l->count; ++i)
        if (cab_ord_eq(l->items[i].agent_id, agent_id)) idx[n++] = i;
    log_sort_desc(l, idx, n);
    if ((size_t)limit < n) n = (size_t)limit;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_ma_invocation_t *out = (ca_ma_invocation_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!invocation_copy(&out[i], &l->items[idx[i]])) {
            ca_ma_invocation_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

size_t ca_ma_log_total(const ca_ma_log_t *l) {
    return l ? l->count : 0;
}
