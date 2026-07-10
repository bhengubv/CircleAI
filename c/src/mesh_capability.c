/*
 * mesh_capability.c — CircleAI.AetherNet mesh capability discovery (C11 port).
 *
 * In-memory latest-per-peer registry + null / capturing broadcasters, ported
 * 1:1 from MeshCapabilityRegistry.cs (RT-12 v1).
 *
 * Pure C11 + libc. Linear array of entries keyed by peer id (ordinal). List /
 * Find snapshot deep copies; Find sorts by spare KV budget descending with a
 * STABLE insertion sort (LINQ OrderByDescending is stable).
 */

#include "circle_ai/mesh_capability.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>

/* --- helpers ------------------------------------------------------------- */

static char *dup_str(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static bool is_null_or_whitespace(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (!isspace((unsigned char)*p)) return false;
    return true;
}

static bool ieq_ordinal(const char *a, const char *b) {
    if (!a || !b) return false;
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b))
            return false;
        a++; b++;
    }
    return *a == *b;
}

/* ===========================================================================
 * MeshCapabilityAdvertisement
 * =========================================================================== */

ca_mesh_capability_advertisement_t *ca_mesh_capability_advertisement_create(
    const char *peer_id, const char *model_id, int free_kv_tokens,
    ca_device_tier_t tier, int context_window_tokens, int64_t advertised_at_ms,
    bool has_latency_hint, int latency_hint_ms) {
    ca_mesh_capability_advertisement_t *ad =
        (ca_mesh_capability_advertisement_t *)calloc(1, sizeof(*ad));
    if (!ad) return NULL;
    ad->peer_id = dup_str(peer_id ? peer_id : "");
    ad->model_id = dup_str(model_id ? model_id : "");
    if (!ad->peer_id || !ad->model_id) {
        ca_mesh_capability_advertisement_destroy(ad);
        return NULL;
    }
    ad->free_kv_tokens = free_kv_tokens;
    ad->tier = tier;
    ad->context_window_tokens = context_window_tokens;
    ad->advertised_at_ms = advertised_at_ms;
    ad->has_latency_hint = has_latency_hint;
    ad->latency_hint_ms = latency_hint_ms;
    return ad;
}

void ca_mesh_capability_advertisement_destroy(
    ca_mesh_capability_advertisement_t *ad) {
    if (!ad) return;
    free(ad->peer_id);
    free(ad->model_id);
    free(ad);
}

ca_mesh_capability_advertisement_t *ca_mesh_capability_advertisement_copy(
    const ca_mesh_capability_advertisement_t *ad) {
    if (!ad) return NULL;
    return ca_mesh_capability_advertisement_create(
        ad->peer_id, ad->model_id, ad->free_kv_tokens, ad->tier,
        ad->context_window_tokens, ad->advertised_at_ms, ad->has_latency_hint,
        ad->latency_hint_ms);
}

void ca_mesh_capability_advertisement_list_free(
    ca_mesh_capability_advertisement_t **list, size_t count) {
    if (!list) return;
    for (size_t i = 0; i < count; ++i)
        ca_mesh_capability_advertisement_destroy(list[i]);
    free(list);
}

/* ===========================================================================
 * InMemoryMeshCapabilityRegistry
 * =========================================================================== */

struct ca_mesh_capability_registry {
    ca_mesh_capability_advertisement_t **entries; /* owned array of owned ads */
    size_t         count;
    size_t         cap;
    ca_mesh_now_fn now_fn;
    void          *now_user;
};

ca_mesh_capability_registry_t *ca_mesh_capability_registry_create(
    ca_mesh_now_fn now_fn, void *now_user) {
    ca_mesh_capability_registry_t *reg =
        (ca_mesh_capability_registry_t *)calloc(1, sizeof(*reg));
    if (!reg) return NULL;
    reg->now_fn = now_fn;
    reg->now_user = now_user;
    return reg;
}

void ca_mesh_capability_registry_destroy(ca_mesh_capability_registry_t *reg) {
    if (!reg) return;
    for (size_t i = 0; i < reg->count; ++i)
        ca_mesh_capability_advertisement_destroy(reg->entries[i]);
    free(reg->entries);
    free(reg);
}

/* Find the index of peer_id (ordinal), or -1. */
static long registry_index_of(const ca_mesh_capability_registry_t *reg,
                              const char *peer_id) {
    for (size_t i = 0; i < reg->count; ++i)
        if (strcmp(reg->entries[i]->peer_id, peer_id) == 0) return (long)i;
    return -1;
}

int ca_mesh_capability_registry_upsert(
    ca_mesh_capability_registry_t *reg,
    const ca_mesh_capability_advertisement_t *ad) {
    if (!reg || !ad) return -1;
    if (is_null_or_whitespace(ad->peer_id)) return -1; /* ThrowIfNullOrWhiteSpace */

    ca_mesh_capability_advertisement_t *copy =
        ca_mesh_capability_advertisement_copy(ad);
    if (!copy) return -1;

    long idx = registry_index_of(reg, ad->peer_id);
    if (idx >= 0) {
        ca_mesh_capability_advertisement_destroy(reg->entries[idx]);
        reg->entries[idx] = copy; /* replace */
        return 0;
    }
    if (reg->count == reg->cap) {
        size_t nc = reg->cap ? reg->cap * 2 : 8;
        ca_mesh_capability_advertisement_t **ne =
            (ca_mesh_capability_advertisement_t **)realloc(
                reg->entries, nc * sizeof(*ne));
        if (!ne) { ca_mesh_capability_advertisement_destroy(copy); return -1; }
        reg->entries = ne;
        reg->cap = nc;
    }
    reg->entries[reg->count++] = copy;
    return 0;
}

bool ca_mesh_capability_registry_remove(ca_mesh_capability_registry_t *reg,
                                        const char *peer_id) {
    if (!reg || is_null_or_whitespace(peer_id)) return false;
    long idx = registry_index_of(reg, peer_id);
    if (idx < 0) return false;
    ca_mesh_capability_advertisement_destroy(reg->entries[idx]);
    /* preserve insertion order of the rest (shift down) so List order is
     * deterministic — cheaper than a swap-remove for the array snapshot. */
    for (size_t i = (size_t)idx; i + 1 < reg->count; ++i)
        reg->entries[i] = reg->entries[i + 1];
    reg->count--;
    return true;
}

size_t ca_mesh_capability_registry_count(
    const ca_mesh_capability_registry_t *reg) {
    return reg ? reg->count : 0;
}

static int64_t registry_now(const ca_mesh_capability_registry_t *reg) {
    return reg->now_fn ? reg->now_fn(reg->now_user) : 0;
}

/* Build a deep-copied snapshot of entries passing a predicate, in insertion
 * order. Returns count; writes NULL/SIZE_MAX on OOM. */
static size_t snapshot_where(
    const ca_mesh_capability_registry_t *reg,
    bool (*pred)(const ca_mesh_capability_advertisement_t *, void *),
    void *pred_ctx,
    ca_mesh_capability_advertisement_t ***out_list) {
    if (out_list) *out_list = NULL;
    /* count first */
    size_t n = 0;
    for (size_t i = 0; i < reg->count; ++i)
        if (pred(reg->entries[i], pred_ctx)) n++;
    if (n == 0) return 0;
    ca_mesh_capability_advertisement_t **out =
        (ca_mesh_capability_advertisement_t **)calloc(n, sizeof(*out));
    if (!out) { return (size_t)-1; }
    size_t j = 0;
    for (size_t i = 0; i < reg->count; ++i) {
        if (!pred(reg->entries[i], pred_ctx)) continue;
        out[j] = ca_mesh_capability_advertisement_copy(reg->entries[i]);
        if (!out[j]) {
            ca_mesh_capability_advertisement_list_free(out, j);
            return (size_t)-1;
        }
        j++;
    }
    if (out_list) *out_list = out;
    else ca_mesh_capability_advertisement_list_free(out, n);
    return n;
}

/* --- List --- */

struct list_ctx { bool has_cutoff; int64_t cutoff; };

static bool list_pred(const ca_mesh_capability_advertisement_t *a, void *ctx) {
    struct list_ctx *c = (struct list_ctx *)ctx;
    if (!c->has_cutoff) return true;
    return a->advertised_at_ms >= c->cutoff; /* AdvertisedAtUtc >= cutoff */
}

size_t ca_mesh_capability_registry_list(
    const ca_mesh_capability_registry_t *reg,
    bool has_stale_after, int64_t stale_after_ms,
    ca_mesh_capability_advertisement_t ***out_list) {
    if (out_list) *out_list = NULL;
    if (!reg) return (size_t)-1;
    struct list_ctx c;
    c.has_cutoff = has_stale_after;
    c.cutoff = has_stale_after ? (registry_now(reg) - stale_after_ms) : 0;
    return snapshot_where(reg, list_pred, &c, out_list);
}

/* --- Find --- */

struct find_ctx {
    const char *model_id;
    int         min_free;
    bool        has_cutoff;
    int64_t     cutoff;
};

static bool find_pred(const ca_mesh_capability_advertisement_t *a, void *ctx) {
    struct find_ctx *c = (struct find_ctx *)ctx;
    if (!ieq_ordinal(a->model_id, c->model_id)) return false;
    if (a->free_kv_tokens < c->min_free) return false;
    if (c->has_cutoff && a->advertised_at_ms < c->cutoff) return false;
    return true;
}

/* Stable insertion sort by free_kv_tokens DESCENDING (ties keep input order,
 * matching LINQ OrderByDescending). */
static void sort_by_free_kv_desc(ca_mesh_capability_advertisement_t **arr,
                                 size_t n) {
    for (size_t i = 1; i < n; ++i) {
        ca_mesh_capability_advertisement_t *key = arr[i];
        size_t j = i;
        while (j > 0 && arr[j - 1]->free_kv_tokens < key->free_kv_tokens) {
            arr[j] = arr[j - 1];
            j--;
        }
        arr[j] = key;
    }
}

size_t ca_mesh_capability_registry_find(
    const ca_mesh_capability_registry_t *reg, const char *model_id,
    int min_free_kv_tokens, bool has_stale_after, int64_t stale_after_ms,
    ca_mesh_capability_advertisement_t ***out_list) {
    if (out_list) *out_list = NULL;
    if (!reg || is_null_or_whitespace(model_id)) return (size_t)-1;
    struct find_ctx c;
    c.model_id = model_id;
    c.min_free = min_free_kv_tokens;
    c.has_cutoff = has_stale_after;
    /* C#: cutoff = staleAfter ? Now - staleAfter : DateTimeOffset.MinValue.
     * When no staleAfter, every entry passes — modelled as has_cutoff=false. */
    c.cutoff = has_stale_after ? (registry_now(reg) - stale_after_ms) : 0;

    ca_mesh_capability_advertisement_t **out = NULL;
    size_t n = snapshot_where(reg, find_pred, &c, &out);
    if (n == (size_t)-1) return (size_t)-1;
    if (n > 0) sort_by_free_kv_desc(out, n);
    if (out_list) *out_list = out;
    else ca_mesh_capability_advertisement_list_free(out, n);
    return n;
}

/* ===========================================================================
 * Broadcasters
 * =========================================================================== */

static int null_broadcast(void *self,
                          const ca_mesh_capability_advertisement_t *ad) {
    (void)self; (void)ad;
    return 0; /* ValueTask.CompletedTask */
}

ca_mesh_capability_broadcaster_t ca_null_mesh_capability_broadcaster(void) {
    ca_mesh_capability_broadcaster_t b;
    b.self = NULL;
    b.broadcast = null_broadcast;
    return b;
}

struct ca_capturing_broadcaster {
    ca_mesh_capability_advertisement_t *last; /* owned; NULL until first call */
    int count;
};

ca_capturing_broadcaster_t *ca_capturing_broadcaster_create(void) {
    return (ca_capturing_broadcaster_t *)calloc(
        1, sizeof(ca_capturing_broadcaster_t));
}

void ca_capturing_broadcaster_destroy(ca_capturing_broadcaster_t *b) {
    if (!b) return;
    ca_mesh_capability_advertisement_destroy(b->last);
    free(b);
}

static int capturing_broadcast(void *self,
                               const ca_mesh_capability_advertisement_t *ad) {
    ca_capturing_broadcaster_t *b = (ca_capturing_broadcaster_t *)self;
    if (!b || !ad) return -1;
    ca_mesh_capability_advertisement_t *copy =
        ca_mesh_capability_advertisement_copy(ad);
    if (!copy) return -1;
    ca_mesh_capability_advertisement_destroy(b->last);
    b->last = copy;
    b->count++;
    return 0;
}

ca_mesh_capability_broadcaster_t ca_capturing_broadcaster_as_broadcaster(
    ca_capturing_broadcaster_t *b) {
    ca_mesh_capability_broadcaster_t v;
    v.self = b;
    v.broadcast = capturing_broadcast;
    return v;
}

int ca_capturing_broadcaster_count(const ca_capturing_broadcaster_t *b) {
    return b ? b->count : 0;
}

const ca_mesh_capability_advertisement_t *ca_capturing_broadcaster_last(
    const ca_capturing_broadcaster_t *b) {
    return b ? b->last : NULL;
}
