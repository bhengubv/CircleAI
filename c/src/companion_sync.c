/*
 * companion_sync.c — CircleAI.Memory.Sync (C11 port).
 *
 * Ports the companion-state sync layer 1:1 from the C# spec. Async collapses to
 * synchronous calls; the convergence protocol, apply/tiebreak rules, HLC
 * bit-layout, and SHA-256 content hashing match the C# byte-for-byte.
 *
 * Pure C11 + libc. SHA-256 is reused from multimodal.c via ca_sha256_hex.
 */

#include "circle_ai/companion_sync.h"
#include "circle_ai/multimodal.h"   /* ca_sha256_hex */

#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdio.h>
#include <time.h>
#include <inttypes.h>

/* =====================================================================
 * Small utilities
 * ===================================================================== */

static char *cs_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static bool cs_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p)
        if (!isspace(*p)) return false;
    return true;
}

/* Default physical/wall clock: Unix ms UTC (DateTimeOffset.UtcNow). Matches the
 * codebase convention (multimodal.c mm_now_ms) — second resolution is fine; the
 * HLC's logical counter disambiguates writes within the same millisecond. */
static int64_t cs_now_ms_default(void *user) {
    (void)user;
    return (int64_t)time(NULL) * 1000;
}

/* Growable char buffer. */
typedef struct { char *buf; size_t len; size_t cap; } cs_sb;
static void cs_sb_ensure(cs_sb *b, size_t extra) {
    if (b->len + extra + 1 > b->cap) {
        size_t nc = b->cap ? b->cap : 64;
        while (b->len + extra + 1 > nc) nc *= 2;
        char *nb = (char *)realloc(b->buf, nc);
        if (!nb) return;
        b->buf = nb; b->cap = nc;
    }
}
static void cs_sb_append(cs_sb *b, const char *s) {
    if (!s) return;
    size_t n = strlen(s);
    cs_sb_ensure(b, n);
    if (!b->buf) return;
    memcpy(b->buf + b->len, s, n);
    b->len += n;
    b->buf[b->len] = '\0';
}
static void cs_sb_append_char(cs_sb *b, char c) {
    cs_sb_ensure(b, 1);
    if (!b->buf) return;
    b->buf[b->len++] = c;
    b->buf[b->len] = '\0';
}

/* JSON string escape matching System.Text.Json JavaScriptEncoder.Default —
 * identical to companion_reason.c / herjarvis.c so payloads are byte-stable. */
static void cs_json_emit_u(cs_sb *b, unsigned cp) {
    char tmp[8];
    snprintf(tmp, sizeof(tmp), "\\u%04X", cp & 0xFFFF);
    cs_sb_append(b, tmp);
}
static void cs_json_escape(cs_sb *b, const char *s) {
    const unsigned char *p = (const unsigned char *)s;
    while (p && *p) {
        unsigned char c = *p;
        if (c < 0x80) {
            switch (c) {
                case '\\': cs_sb_append(b, "\\\\"); ++p; continue;
                case '\b': cs_sb_append(b, "\\b");  ++p; continue;
                case '\t': cs_sb_append(b, "\\t");  ++p; continue;
                case '\n': cs_sb_append(b, "\\n");  ++p; continue;
                case '\f': cs_sb_append(b, "\\f");  ++p; continue;
                case '\r': cs_sb_append(b, "\\r");  ++p; continue;
                default: break;
            }
            if (c < 0x20 || c == '"' || c == '<' || c == '>' || c == '&' ||
                c == '\'' || c == '`' || c == '+') {
                cs_json_emit_u(b, c);
            } else {
                cs_sb_append_char(b, (char)c);
            }
            ++p;
            continue;
        }
        unsigned cp; int adv;
        if ((c & 0xE0) == 0xC0 && p[1]) { cp = ((c & 0x1Fu) << 6) | (p[1] & 0x3Fu); adv = 2; }
        else if ((c & 0xF0) == 0xE0 && p[1] && p[2]) {
            cp = ((c & 0x0Fu) << 12) | ((p[1] & 0x3Fu) << 6) | (p[2] & 0x3Fu); adv = 3;
        } else if ((c & 0xF8) == 0xF0 && p[1] && p[2] && p[3]) {
            cp = ((c & 0x07u) << 18) | ((p[1] & 0x3Fu) << 12) |
                 ((p[2] & 0x3Fu) << 6) | (p[3] & 0x3Fu); adv = 4;
        } else { cp = c; adv = 1; }
        if (cp <= 0xFFFF) cs_json_emit_u(b, cp);
        else {
            unsigned v = cp - 0x10000u;
            cs_json_emit_u(b, 0xD800u | (v >> 10));
            cs_json_emit_u(b, 0xDC00u | (v & 0x3FFu));
        }
        p += adv;
    }
}

/* ISO-8601 "O"-style UTC timestamp from Unix ms (DateTimeOffset "O"). */
static void cs_iso8601(int64_t unix_ms, char out[48]) {
    int64_t secs = unix_ms / 1000;
    int ms = (int)(unix_ms % 1000);
    if (ms < 0) { ms += 1000; secs -= 1; }
    int64_t z = secs / 86400;
    int64_t rem = secs % 86400;
    if (rem < 0) { rem += 86400; z -= 1; }
    z += 719468;
    int64_t era = (z >= 0 ? z : z - 146096) / 146097;
    unsigned doe = (unsigned)(z - era * 146097);
    unsigned yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    int64_t y = (int64_t)yoe + era * 400;
    unsigned doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    unsigned mp = (5 * doy + 2) / 153;
    unsigned d = doy - (153 * mp + 2) / 5 + 1;
    unsigned m = mp < 10 ? mp + 3 : mp - 9;
    if (m <= 2) y += 1;
    unsigned hh = (unsigned)(rem / 3600) % 24u;
    unsigned mm = (unsigned)((rem % 3600) / 60) % 60u;
    unsigned ss = (unsigned)(rem % 60) % 60u;
    unsigned frac = ((unsigned)ms % 1000u) * 10000u;
    char tmp[64];
    snprintf(tmp, sizeof(tmp), "%04lld-%02u-%02uT%02u:%02u:%02u.%07u+00:00",
             (long long)y, m, d, hh, mm, ss, frac);
    tmp[47] = '\0';
    memcpy(out, tmp, strlen(tmp) + 1);
}

/* Parse ISO-8601 "O" UTC timestamp back to Unix ms (inverse of cs_iso8601).
 * Tolerant: accepts a trailing 'Z' or +hh:mm offset (offset assumed 00:00 for
 * our own payloads). Returns 0 and *ok=false on malformed input. */
static int64_t cs_iso8601_parse(const char *s, bool *ok) {
    if (ok) *ok = false;
    if (!s) return 0;
    int Y, Mo, D, H, Mi, S; unsigned frac = 0;
    int consumed = 0;
    if (sscanf(s, "%d-%d-%dT%d:%d:%d%n", &Y, &Mo, &D, &H, &Mi, &S, &consumed) < 6)
        return 0;
    const char *p = s + consumed;
    if (*p == '.') {
        ++p;
        int digits = 0; unsigned f = 0;
        while (isdigit((unsigned char)*p) && digits < 7) { f = f * 10 + (unsigned)(*p - '0'); ++p; ++digits; }
        while (isdigit((unsigned char)*p)) ++p; /* drop beyond 7 */
        while (digits < 7) { f *= 10; ++digits; }
        frac = f; /* 100ns ticks */
    }
    /* days-from-civil (Hinnant) */
    int64_t y = Y; unsigned m = (unsigned)Mo, d = (unsigned)D;
    y -= (m <= 2);
    int64_t era = (y >= 0 ? y : y - 399) / 400;
    unsigned yoe = (unsigned)(y - era * 400);
    unsigned doy = (153u * (m > 2 ? m - 3 : m + 9) + 2) / 5 + d - 1;
    unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    int64_t days = era * 146097 + (int64_t)doe - 719468;
    int64_t secs = days * 86400 + H * 3600 + Mi * 60 + S;
    int64_t ms = secs * 1000 + (int64_t)(frac / 10000u);
    if (ok) *ok = true;
    return ms;
}

/* =====================================================================
 * HybridLogicalClock — HybridLogicalClock.cs
 * ===================================================================== */

struct ca_hybrid_logical_clock {
    ca_hlc_now_fn now;
    void         *now_user;
    int64_t       node_short_id;
    int64_t       last_physical;
    int64_t       logical;
};

int64_t ca_hlc_compose(int64_t physical_ms, int64_t logical, int64_t node_short_id) {
    return (physical_ms << 16) | ((logical & 0x3FF) << 6) | (node_short_id & 0x3F);
}
void ca_hlc_decompose(int64_t version, int64_t *out_physical_ms,
                      int64_t *out_logical, int64_t *out_node_short_id) {
    if (out_physical_ms)  *out_physical_ms  = version >> 16;
    if (out_logical)      *out_logical      = (version >> 6) & 0x3FF;
    if (out_node_short_id)*out_node_short_id= version & 0x3F;
}

ca_hybrid_logical_clock_t *ca_hlc_create(int64_t node_short_id,
                                         ca_hlc_now_fn now, void *now_user) {
    if (node_short_id < 0 || node_short_id > 63) return NULL; /* ArgumentOutOfRange */
    ca_hybrid_logical_clock_t *c =
        (ca_hybrid_logical_clock_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->now = now ? now : cs_now_ms_default;
    c->now_user = now_user;
    c->node_short_id = node_short_id;
    c->last_physical = c->now(c->now_user);
    c->logical = 0;
    return c;
}
void ca_hlc_destroy(ca_hybrid_logical_clock_t *clock) { free(clock); }

int64_t ca_hlc_tick(ca_hybrid_logical_clock_t *clock) {
    if (!clock) return 0;
    int64_t now = clock->now(clock->now_user);
    if (now > clock->last_physical) {
        clock->last_physical = now;
        clock->logical = 0;
    } else {
        clock->logical++;
        if (clock->logical >= 1024) {
            clock->last_physical++;
            clock->logical = 0;
        }
    }
    return ca_hlc_compose(clock->last_physical, clock->logical, clock->node_short_id);
}

int64_t ca_hlc_observe(ca_hybrid_logical_clock_t *clock, int64_t incoming) {
    if (!clock) return 0;
    int64_t inc_physical, inc_logical;
    ca_hlc_decompose(incoming, &inc_physical, &inc_logical, NULL);
    int64_t now = clock->now(clock->now_user);
    int64_t max_physical = clock->last_physical;
    if (inc_physical > max_physical) max_physical = inc_physical;
    if (now > max_physical) max_physical = now;

    if (max_physical == clock->last_physical && max_physical == inc_physical) clock->logical++;
    else if (max_physical == clock->last_physical) clock->logical++;
    else if (max_physical == inc_physical) clock->logical = inc_logical + 1;
    else clock->logical = 0;

    clock->last_physical = max_physical;
    return ca_hlc_compose(clock->last_physical, clock->logical, clock->node_short_id);
}

/* =====================================================================
 * SyncableEntry
 * ===================================================================== */

void ca_syncable_entry_free(ca_syncable_entry_t *e) {
    if (!e) return;
    free(e->entity_type);
    free(e->entity_id);
    free(e->content_hash);
    free(e->payload);
    free(e->source_node_id);
    e->entity_type = e->entity_id = e->content_hash = e->payload = e->source_node_id = NULL;
}
void ca_syncable_entry_free_array(ca_syncable_entry_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_syncable_entry_free(&arr[i]);
    free(arr);
}
ca_syncable_entry_t *ca_syncable_entry_copy(ca_syncable_entry_t *dst,
                                            const ca_syncable_entry_t *src) {
    if (!dst || !src) return dst;
    dst->entity_type    = cs_strdup(src->entity_type);
    dst->entity_id      = cs_strdup(src->entity_id);
    dst->version        = src->version;
    dst->is_tombstone   = src->is_tombstone;
    dst->content_hash   = cs_strdup(src->content_hash);
    dst->payload        = cs_strdup(src->payload);
    dst->source_node_id = cs_strdup(src->source_node_id);
    dst->authored_at_ms = src->authored_at_ms;
    return dst;
}

/* =====================================================================
 * SyncEnvelope
 * ===================================================================== */

void ca_state_vector_free_array(ca_state_vector_entry_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) free(arr[i].entity_type);
    free(arr);
}
static void cs_request_free_array(ca_request_item_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) free(arr[i].entity_type);
    free(arr);
}
void ca_sync_envelope_free(ca_sync_envelope_t *env) {
    if (!env) return;
    free(env->from_node_id);
    ca_state_vector_free_array(env->state_vector, env->state_vector_count);
    cs_request_free_array(env->requests, env->requests_count);
    ca_syncable_entry_free_array(env->entries, env->entries_count);
    env->from_node_id = NULL;
    env->state_vector = NULL; env->state_vector_count = 0;
    env->requests = NULL; env->requests_count = 0;
    env->entries = NULL; env->entries_count = 0;
}

/* =====================================================================
 * InMemorySyncableEntryStore — ISyncableEntryStore.cs
 * ===================================================================== */

struct ca_syncable_entry_store {
    ca_syncable_entry_t *entries;   /* linear array keyed by (type,id) */
    size_t               count;
    size_t               cap;
    /* max version per type (linear map). */
    struct { char *type; int64_t max; } *vec;
    size_t               vec_count;
    size_t               vec_cap;
};

ca_syncable_entry_store_t *ca_inmem_syncable_store_create(void) {
    return (ca_syncable_entry_store_t *)calloc(1, sizeof(ca_syncable_entry_store_t));
}
void ca_inmem_syncable_store_destroy(ca_syncable_entry_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i) ca_syncable_entry_free(&store->entries[i]);
    free(store->entries);
    for (size_t i = 0; i < store->vec_count; ++i) free(store->vec[i].type);
    free(store->vec);
    free(store);
}

/* ShouldApply: higher Version wins; on tie tombstone-of-non-tombstone wins;
 * else higher ContentHash (ordinal). */
static bool cs_should_apply(const ca_syncable_entry_t *existing,
                            const ca_syncable_entry_t *incoming) {
    if (incoming->version > existing->version) return true;
    if (incoming->version < existing->version) return false;
    if (incoming->is_tombstone && !existing->is_tombstone) return true;
    if (!incoming->is_tombstone && existing->is_tombstone) return false;
    const char *a = incoming->content_hash ? incoming->content_hash : "";
    const char *b = existing->content_hash ? existing->content_hash : "";
    return strcmp(a, b) > 0;
}

static ca_syncable_entry_t *cs_store_find(ca_syncable_entry_store_t *store,
                                          const char *type, const char *id) {
    for (size_t i = 0; i < store->count; ++i) {
        if (strcmp(store->entries[i].entity_type, type) == 0 &&
            strcmp(store->entries[i].entity_id, id) == 0)
            return &store->entries[i];
    }
    return NULL;
}

static void cs_store_bump_vector(ca_syncable_entry_store_t *store,
                                 const char *type, int64_t version) {
    for (size_t i = 0; i < store->vec_count; ++i) {
        if (strcmp(store->vec[i].type, type) == 0) {
            if (version > store->vec[i].max) store->vec[i].max = version;
            return;
        }
    }
    if (store->vec_count == store->vec_cap) {
        size_t nc = store->vec_cap ? store->vec_cap * 2 : 8;
        void *nv = realloc(store->vec, nc * sizeof(*store->vec));
        if (!nv) return;
        store->vec = nv; store->vec_cap = nc;
    }
    store->vec[store->vec_count].type = cs_strdup(type);
    store->vec[store->vec_count].max = version;
    store->vec_count++;
}

bool ca_inmem_syncable_store_apply(ca_syncable_entry_store_t *store,
                                   const ca_syncable_entry_t *entry) {
    if (!store || !entry) return false;
    bool applied = false;
    ca_syncable_entry_t *existing = cs_store_find(store, entry->entity_type, entry->entity_id);
    if (!existing) {
        if (store->count == store->cap) {
            size_t nc = store->cap ? store->cap * 2 : 8;
            void *ne = realloc(store->entries, nc * sizeof(*store->entries));
            if (!ne) return false;
            store->entries = ne; store->cap = nc;
        }
        ca_syncable_entry_copy(&store->entries[store->count], entry);
        store->count++;
        applied = true;
    } else if (cs_should_apply(existing, entry)) {
        ca_syncable_entry_t copy; memset(&copy, 0, sizeof(copy));
        ca_syncable_entry_copy(&copy, entry);
        ca_syncable_entry_free(existing);
        *existing = copy;
        applied = true;
    }
    if (applied) cs_store_bump_vector(store, entry->entity_type, entry->version);
    return applied;
}

bool ca_inmem_syncable_store_get(ca_syncable_entry_store_t *store,
                                 const char *entity_type, const char *entity_id,
                                 ca_syncable_entry_t *out) {
    if (!store || !entity_type || !entity_id || !out) return false;
    ca_syncable_entry_t *e = cs_store_find(store, entity_type, entity_id);
    if (!e) return false;
    ca_syncable_entry_copy(out, e);
    return true;
}

/* insertion sort by version ascending (stable, small arrays). */
static void cs_sort_entries_by_version(ca_syncable_entry_t *a, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        ca_syncable_entry_t key = a[i];
        size_t j = i;
        while (j > 0 && a[j - 1].version > key.version) { a[j] = a[j - 1]; --j; }
        a[j] = key;
    }
}

ca_syncable_entry_t *ca_inmem_syncable_store_get_since(
    ca_syncable_entry_store_t *store,
    const char *entity_type, int64_t since_version, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store || !entity_type) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < store->count; ++i)
        if (strcmp(store->entries[i].entity_type, entity_type) == 0 &&
            store->entries[i].version > since_version) ++n;
    if (n == 0) return NULL;
    ca_syncable_entry_t *res = (ca_syncable_entry_t *)calloc(n, sizeof(*res));
    if (!res) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    size_t k = 0;
    for (size_t i = 0; i < store->count; ++i)
        if (strcmp(store->entries[i].entity_type, entity_type) == 0 &&
            store->entries[i].version > since_version)
            ca_syncable_entry_copy(&res[k++], &store->entries[i]);
    cs_sort_entries_by_version(res, n);
    if (out_count) *out_count = n;
    return res;
}

static int cs_cmp_vector(const void *a, const void *b) {
    const ca_state_vector_entry_t *x = (const ca_state_vector_entry_t *)a;
    const ca_state_vector_entry_t *y = (const ca_state_vector_entry_t *)b;
    return strcmp(x->entity_type, y->entity_type); /* ordinal ascending */
}

ca_state_vector_entry_t *ca_inmem_syncable_store_get_state_vector(
    ca_syncable_entry_store_t *store, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!store) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    if (store->vec_count == 0) return NULL;
    ca_state_vector_entry_t *res =
        (ca_state_vector_entry_t *)calloc(store->vec_count, sizeof(*res));
    if (!res) { if (out_count) *out_count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < store->vec_count; ++i) {
        res[i].entity_type = cs_strdup(store->vec[i].type);
        res[i].max_known_version = store->vec[i].max;
    }
    qsort(res, store->vec_count, sizeof(*res), cs_cmp_vector);
    if (out_count) *out_count = store->vec_count;
    return res;
}

/* vtable adapters */
static bool cs_iface_apply(void *self, const ca_syncable_entry_t *e) {
    return ca_inmem_syncable_store_apply((ca_syncable_entry_store_t *)self, e);
}
static bool cs_iface_get(void *self, const char *t, const char *i, ca_syncable_entry_t *o) {
    return ca_inmem_syncable_store_get((ca_syncable_entry_store_t *)self, t, i, o);
}
static ca_syncable_entry_t *cs_iface_get_since(void *self, const char *t, int64_t sv, size_t *oc) {
    return ca_inmem_syncable_store_get_since((ca_syncable_entry_store_t *)self, t, sv, oc);
}
static ca_state_vector_entry_t *cs_iface_get_vec(void *self, size_t *oc) {
    return ca_inmem_syncable_store_get_state_vector((ca_syncable_entry_store_t *)self, oc);
}
ca_syncable_store_iface_t ca_inmem_syncable_store_iface(ca_syncable_entry_store_t *store) {
    ca_syncable_store_iface_t v;
    v.self = store;
    v.apply = cs_iface_apply;
    v.get = cs_iface_get;
    v.get_since = cs_iface_get_since;
    v.get_state_vector = cs_iface_get_vec;
    return v;
}

/* =====================================================================
 * InProcessSyncHub + InProcessCompanionStateChannel
 * ===================================================================== */

typedef struct cs_handler_node {
    ca_channel_handler_fn handler;
    void                 *user;
} cs_handler_node;

struct ca_companion_state_channel {
    ca_inproc_sync_hub_t *hub;
    char                 *local_node_id;
    cs_handler_node      *handlers;
    size_t                handler_count;
    size_t                handler_cap;
    bool                  disposed;
};

struct ca_inproc_sync_hub {
    ca_companion_state_channel_t **channels;
    size_t                         count;
    size_t                         cap;
};

ca_inproc_sync_hub_t *ca_inproc_sync_hub_create(void) {
    return (ca_inproc_sync_hub_t *)calloc(1, sizeof(ca_inproc_sync_hub_t));
}
void ca_inproc_sync_hub_destroy(ca_inproc_sync_hub_t *hub) {
    if (!hub) return;
    free(hub->channels);
    free(hub);
}

static void cs_hub_join(ca_inproc_sync_hub_t *hub, ca_companion_state_channel_t *ch) {
    /* replace by node id if present (ConcurrentDictionary[id] = channel). */
    for (size_t i = 0; i < hub->count; ++i)
        if (strcmp(hub->channels[i]->local_node_id, ch->local_node_id) == 0) {
            hub->channels[i] = ch; return;
        }
    if (hub->count == hub->cap) {
        size_t nc = hub->cap ? hub->cap * 2 : 4;
        void *n = realloc(hub->channels, nc * sizeof(*hub->channels));
        if (!n) return;
        hub->channels = n; hub->cap = nc;
    }
    hub->channels[hub->count++] = ch;
}
static void cs_hub_leave(ca_inproc_sync_hub_t *hub, const char *node_id) {
    for (size_t i = 0; i < hub->count; ++i)
        if (strcmp(hub->channels[i]->local_node_id, node_id) == 0) {
            memmove(&hub->channels[i], &hub->channels[i + 1],
                    (hub->count - i - 1) * sizeof(*hub->channels));
            hub->count--;
            return;
        }
}

static void cs_channel_deliver(ca_companion_state_channel_t *ch, const ca_sync_envelope_t *env) {
    /* snapshot handlers then fire (matches C# handler-list snapshot). */
    size_t n = ch->handler_count;
    cs_handler_node *snapshot = NULL;
    if (n) {
        snapshot = (cs_handler_node *)malloc(n * sizeof(*snapshot));
        if (!snapshot) return;
        memcpy(snapshot, ch->handlers, n * sizeof(*snapshot));
    }
    for (size_t i = 0; i < n; ++i)
        if (snapshot[i].handler) snapshot[i].handler(snapshot[i].user, env);
    free(snapshot);
}

static void cs_hub_broadcast(ca_inproc_sync_hub_t *hub, const ca_sync_envelope_t *env,
                             const char *sender) {
    /* snapshot peers != sender then deliver. */
    size_t n = hub->count;
    ca_companion_state_channel_t **peers =
        (ca_companion_state_channel_t **)malloc(n * sizeof(*peers));
    if (!peers && n) return;
    size_t pc = 0;
    for (size_t i = 0; i < n; ++i)
        if (strcmp(hub->channels[i]->local_node_id, sender) != 0)
            peers[pc++] = hub->channels[i];
    for (size_t i = 0; i < pc; ++i) cs_channel_deliver(peers[i], env);
    free(peers);
}

char **ca_inproc_sync_hub_connected(const ca_inproc_sync_hub_t *hub, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!hub || hub->count == 0) return NULL;
    char **arr = (char **)calloc(hub->count, sizeof(char *));
    if (!arr) return NULL;
    for (size_t i = 0; i < hub->count; ++i) arr[i] = cs_strdup(hub->channels[i]->local_node_id);
    if (out_count) *out_count = hub->count;
    return arr;
}
/* ca_string_array_free is provided by companion_brain.c (shared). */

ca_companion_state_channel_t *ca_inproc_channel_create(
    ca_inproc_sync_hub_t *hub, const char *local_node_id) {
    if (!hub || cs_blank(local_node_id)) return NULL;
    ca_companion_state_channel_t *ch =
        (ca_companion_state_channel_t *)calloc(1, sizeof(*ch));
    if (!ch) return NULL;
    ch->hub = hub;
    ch->local_node_id = cs_strdup(local_node_id);
    cs_hub_join(hub, ch);
    return ch;
}
void ca_inproc_channel_destroy(ca_companion_state_channel_t *channel) {
    if (!channel) return;
    if (!channel->disposed) {
        channel->disposed = true;
        cs_hub_leave(channel->hub, channel->local_node_id);
    }
    free(channel->handlers);
    free(channel->local_node_id);
    free(channel);
}

const char *ca_inproc_channel_local_node_id(const ca_companion_state_channel_t *channel) {
    return channel ? channel->local_node_id : NULL;
}

void ca_inproc_channel_send(ca_companion_state_channel_t *channel,
                            const ca_sync_envelope_t *env) {
    if (!channel || channel->disposed || !env) return;
    cs_hub_broadcast(channel->hub, env, channel->local_node_id);
}

void *ca_inproc_channel_subscribe(ca_companion_state_channel_t *channel,
                                  ca_channel_handler_fn handler, void *user) {
    if (!channel || channel->disposed || !handler) return NULL;
    if (channel->handler_count == channel->handler_cap) {
        size_t nc = channel->handler_cap ? channel->handler_cap * 2 : 4;
        void *n = realloc(channel->handlers, nc * sizeof(*channel->handlers));
        if (!n) return NULL;
        channel->handlers = n; channel->handler_cap = nc;
    }
    channel->handlers[channel->handler_count].handler = handler;
    channel->handlers[channel->handler_count].user = user;
    channel->handler_count++;
    /* subscription token = 1-based index encoded as pointer (stable enough for
     * remove-by-identity: we match on (handler,user)). We return a small heap
     * token holding the pair so unsubscribe is O(n) but exact. */
    cs_handler_node *token = (cs_handler_node *)malloc(sizeof(*token));
    if (token) { token->handler = handler; token->user = user; }
    return token;
}

void ca_inproc_channel_unsubscribe(ca_companion_state_channel_t *channel,
                                   void *subscription) {
    if (!channel || !subscription) return;
    cs_handler_node *tok = (cs_handler_node *)subscription;
    for (size_t i = 0; i < channel->handler_count; ++i) {
        if (channel->handlers[i].handler == tok->handler &&
            channel->handlers[i].user == tok->user) {
            memmove(&channel->handlers[i], &channel->handlers[i + 1],
                    (channel->handler_count - i - 1) * sizeof(*channel->handlers));
            channel->handler_count--;
            break;
        }
    }
    free(tok);
}

/* vtable adapters */
static const char *cs_ch_local(void *self) {
    return ca_inproc_channel_local_node_id((ca_companion_state_channel_t *)self);
}
static void cs_ch_send(void *self, const ca_sync_envelope_t *env) {
    ca_inproc_channel_send((ca_companion_state_channel_t *)self, env);
}
static void *cs_ch_subscribe(void *self, ca_channel_handler_fn h, void *u) {
    return ca_inproc_channel_subscribe((ca_companion_state_channel_t *)self, h, u);
}
static void cs_ch_unsubscribe(void *self, void *sub) {
    ca_inproc_channel_unsubscribe((ca_companion_state_channel_t *)self, sub);
}
ca_companion_state_channel_iface_t ca_inproc_channel_iface(
    ca_companion_state_channel_t *channel) {
    ca_companion_state_channel_iface_t v;
    v.self = channel;
    v.local_node_id = cs_ch_local;
    v.send = cs_ch_send;
    v.subscribe = cs_ch_subscribe;
    v.unsubscribe = cs_ch_unsubscribe;
    return v;
}

/* =====================================================================
 * CompanionStateSyncEngine — CompanionStateSyncEngine.cs
 * ===================================================================== */

struct ca_companion_state_sync_engine {
    ca_companion_state_channel_iface_t channel;
    ca_syncable_store_iface_t          store;
    ca_hybrid_logical_clock_t         *clock;   /* borrowed */
    ca_engine_wallclock_fn             wall;
    void                              *wall_user;
    void                              *subscription;   /* NULL until StartAsync */
    bool                               disposed;
};

static int64_t cs_engine_wall(ca_companion_state_sync_engine_t *e) {
    return e->wall ? e->wall(e->wall_user) : cs_now_ms_default(NULL);
}

/* content hash = lowercase SHA-256 hex of the UTF-8 payload. */
static char *cs_compute_hash(const char *payload) {
    const char *p = payload ? payload : "";
    char hex[65];
    ca_sha256_hex((const uint8_t *)p, strlen(p), hex);
    return cs_strdup(hex);
}

/* Forward decl for the static handler trampoline. */
static void cs_engine_handle(void *user, const ca_sync_envelope_t *env);

ca_companion_state_sync_engine_t *ca_sync_engine_create(
    ca_companion_state_channel_iface_t channel,
    ca_syncable_store_iface_t store,
    ca_hybrid_logical_clock_t *clock,
    ca_engine_wallclock_fn wall_clock, void *wall_clock_user) {
    if (!channel.self || !channel.send || !store.self || !clock) return NULL;
    ca_companion_state_sync_engine_t *e =
        (ca_companion_state_sync_engine_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->channel = channel;
    e->store = store;
    e->clock = clock;
    e->wall = wall_clock;
    e->wall_user = wall_clock_user;
    return e;
}

void ca_sync_engine_destroy(ca_companion_state_sync_engine_t *engine) {
    if (!engine) return;
    if (!engine->disposed) {
        engine->disposed = true;
        if (engine->subscription && engine->channel.unsubscribe)
            engine->channel.unsubscribe(engine->channel.self, engine->subscription);
        engine->subscription = NULL;
    }
    free(engine);
}

bool ca_sync_engine_start(ca_companion_state_sync_engine_t *engine) {
    if (!engine || engine->disposed) return false;
    if (!engine->subscription && engine->channel.subscribe)
        engine->subscription = engine->channel.subscribe(engine->channel.self, cs_engine_handle, engine);
    return true;
}

bool ca_sync_engine_sync_now(ca_companion_state_sync_engine_t *engine) {
    if (!engine || engine->disposed) return false;
    size_t n = 0;
    ca_state_vector_entry_t *vec = engine->store.get_state_vector(engine->store.self, &n);
    if (n == SIZE_MAX) return false;
    ca_sync_envelope_t env; memset(&env, 0, sizeof(env));
    env.kind = CA_SYNC_ENVELOPE_ANNOUNCE;
    env.from_node_id = cs_strdup(engine->channel.local_node_id(engine->channel.self));
    env.state_vector = vec;             /* move ownership into envelope */
    env.state_vector_count = n;
    engine->channel.send(engine->channel.self, &env);
    ca_sync_envelope_free(&env);
    return true;
}

bool ca_sync_engine_write_local(
    ca_companion_state_sync_engine_t *engine,
    const char *entity_type, const char *entity_id, const char *payload,
    bool is_tombstone, ca_syncable_entry_t *out_entry) {
    if (!engine || engine->disposed) return false;
    if (cs_blank(entity_type) || cs_blank(entity_id)) return false;
    const char *pl = payload ? payload : "";

    ca_syncable_entry_t entry; memset(&entry, 0, sizeof(entry));
    entry.entity_type    = cs_strdup(entity_type);
    entry.entity_id      = cs_strdup(entity_id);
    entry.version        = ca_hlc_tick(engine->clock);
    entry.is_tombstone   = is_tombstone;
    entry.content_hash   = cs_compute_hash(pl);
    entry.payload        = cs_strdup(pl);
    entry.source_node_id = cs_strdup(engine->channel.local_node_id(engine->channel.self));
    entry.authored_at_ms = cs_engine_wall(engine);

    engine->store.apply(engine->store.self, &entry);

    if (engine->subscription) {
        ca_sync_envelope_t env; memset(&env, 0, sizeof(env));
        env.kind = CA_SYNC_ENVELOPE_PUSH;
        env.from_node_id = cs_strdup(entry.source_node_id);
        env.entries = (ca_syncable_entry_t *)calloc(1, sizeof(ca_syncable_entry_t));
        if (env.entries) {
            ca_syncable_entry_copy(&env.entries[0], &entry);
            env.entries_count = 1;
        }
        engine->channel.send(engine->channel.self, &env);
        ca_sync_envelope_free(&env);
    }

    if (out_entry) ca_syncable_entry_copy(out_entry, &entry);
    ca_syncable_entry_free(&entry);
    return true;
}

/* ── inbound envelope handling ─────────────────────────────────────── */

static void cs_engine_handle_announce(ca_companion_state_sync_engine_t *e,
                                       const ca_sync_envelope_t *env) {
    if (!env->state_vector) return;
    size_t ln = 0;
    ca_state_vector_entry_t *local = e->store.get_state_vector(e->store.self, &ln);
    if (ln == SIZE_MAX) return;

    ca_request_item_t *requests = NULL; size_t rc = 0, rcap = 0;
    for (size_t i = 0; i < env->state_vector_count; ++i) {
        int64_t our_max = 0;
        for (size_t j = 0; j < ln; ++j)
            if (strcmp(local[j].entity_type, env->state_vector[i].entity_type) == 0) {
                our_max = local[j].max_known_version; break;
            }
        if (env->state_vector[i].max_known_version > our_max) {
            if (rc == rcap) {
                size_t nc = rcap ? rcap * 2 : 4;
                void *n = realloc(requests, nc * sizeof(*requests));
                if (!n) break;
                requests = n; rcap = nc;
            }
            requests[rc].entity_type = cs_strdup(env->state_vector[i].entity_type);
            requests[rc].since_version = our_max;
            rc++;
        }
    }
    ca_state_vector_free_array(local, ln);
    if (rc == 0) { free(requests); return; }

    ca_sync_envelope_t reply; memset(&reply, 0, sizeof(reply));
    reply.kind = CA_SYNC_ENVELOPE_REQUEST;
    reply.from_node_id = cs_strdup(e->channel.local_node_id(e->channel.self));
    reply.requests = requests;         /* move ownership */
    reply.requests_count = rc;
    e->channel.send(e->channel.self, &reply);
    ca_sync_envelope_free(&reply);
}

static void cs_engine_handle_request(ca_companion_state_sync_engine_t *e,
                                      const ca_sync_envelope_t *env) {
    if (!env->requests || env->requests_count == 0) return;
    ca_syncable_entry_t *collected = NULL; size_t cc = 0, ccap = 0;
    for (size_t i = 0; i < env->requests_count; ++i) {
        size_t n = 0;
        ca_syncable_entry_t *newer = e->store.get_since(
            e->store.self, env->requests[i].entity_type, env->requests[i].since_version, &n);
        if (n == SIZE_MAX) { continue; }
        for (size_t k = 0; k < n; ++k) {
            if (cc == ccap) {
                size_t nc = ccap ? ccap * 2 : 8;
                void *nn = realloc(collected, nc * sizeof(*collected));
                if (!nn) break;
                collected = nn; ccap = nc;
            }
            collected[cc++] = newer[k]; /* move each (shallow) */
        }
        free(newer);                    /* free container only; elements moved */
    }
    if (cc == 0) { free(collected); return; }

    ca_sync_envelope_t push; memset(&push, 0, sizeof(push));
    push.kind = CA_SYNC_ENVELOPE_PUSH;
    push.from_node_id = cs_strdup(e->channel.local_node_id(e->channel.self));
    push.entries = collected;           /* move ownership */
    push.entries_count = cc;
    e->channel.send(e->channel.self, &push);
    ca_sync_envelope_free(&push);
}

static void cs_engine_handle_push(ca_companion_state_sync_engine_t *e,
                                  const ca_sync_envelope_t *env) {
    if (!env->entries) return;
    bool any_applied = false;
    for (size_t i = 0; i < env->entries_count; ++i) {
        ca_hlc_observe(e->clock, env->entries[i].version);
        if (e->store.apply(e->store.self, &env->entries[i])) any_applied = true;
    }
    if (any_applied) ca_sync_engine_sync_now(e);
}

static void cs_engine_handle(void *user, const ca_sync_envelope_t *env) {
    ca_companion_state_sync_engine_t *e = (ca_companion_state_sync_engine_t *)user;
    if (!e || e->disposed || !env) return;
    switch (env->kind) {
        case CA_SYNC_ENVELOPE_ANNOUNCE: cs_engine_handle_announce(e, env); break;
        case CA_SYNC_ENVELOPE_REQUEST:  cs_engine_handle_request(e, env);  break;
        case CA_SYNC_ENVELOPE_PUSH:     cs_engine_handle_push(e, env);     break;
    }
}

/* =====================================================================
 * PersonaStateSyncBridge — PersonaStateSyncBridge.cs
 * ===================================================================== */

bool ca_persona_sync_bridge_save(ca_companion_state_sync_engine_t *engine,
                                 const char *user_id, const char *persona_json) {
    if (!engine || cs_blank(user_id)) return false;
    return ca_sync_engine_write_local(engine, CA_PERSONA_SYNC_ENTITY_TYPE, user_id,
                                      persona_json ? persona_json : "", false, NULL);
}

char *ca_persona_sync_bridge_try_decode(const ca_syncable_entry_t *entry) {
    if (!entry || entry->is_tombstone) return NULL;
    if (!entry->entity_type || strcmp(entry->entity_type, CA_PERSONA_SYNC_ENTITY_TYPE) != 0)
        return NULL;
    return cs_strdup(entry->payload ? entry->payload : "");
}

/* Base64 (ca_base64_encode / ca_base64_decode) is provided by compression.c
 * (shared, RFC 4648 with '=' padding — identical semantics). */

/* =====================================================================
 * Minimal JSON field extraction (for our self-produced payloads)
 * ===================================================================== */

/* Locate the value token following "key": in a JSON object. Returns a pointer
 * just after the colon (skipping whitespace), or NULL. Matches at object level
 * naively (sufficient for our flat records). */
static const char *cs_json_find(const char *json, const char *key) {
    if (!json || !key) return NULL;
    size_t klen = strlen(key);
    const char *p = json;
    while ((p = strchr(p, '"')) != NULL) {
        const char *ks = p + 1;
        if (strncmp(ks, key, klen) == 0 && ks[klen] == '"') {
            const char *q = ks + klen + 1;
            while (*q && isspace((unsigned char)*q)) ++q;
            if (*q == ':') {
                ++q;
                while (*q && isspace((unsigned char)*q)) ++q;
                return q;
            }
        }
        p = ks; /* advance past this quote */
    }
    return NULL;
}

/* Decode a JSON string token (starting at the opening quote) into a fresh
 * malloc'd C string with escapes resolved. Returns NULL on malformed. */
static char *cs_json_read_string(const char *p) {
    if (!p || *p != '"') return NULL;
    ++p;
    cs_sb sb; memset(&sb, 0, sizeof(sb));
    while (*p && *p != '"') {
        if (*p == '\\') {
            ++p;
            switch (*p) {
                case '"':  cs_sb_append_char(&sb, '"');  break;
                case '\\': cs_sb_append_char(&sb, '\\'); break;
                case '/':  cs_sb_append_char(&sb, '/');  break;
                case 'b':  cs_sb_append_char(&sb, '\b'); break;
                case 'f':  cs_sb_append_char(&sb, '\f'); break;
                case 'n':  cs_sb_append_char(&sb, '\n'); break;
                case 'r':  cs_sb_append_char(&sb, '\r'); break;
                case 't':  cs_sb_append_char(&sb, '\t'); break;
                case 'u': {
                    if (!p[1] || !p[2] || !p[3] || !p[4]) { free(sb.buf); return NULL; }
                    char hex[5] = { p[1], p[2], p[3], p[4], 0 };
                    unsigned cp = (unsigned)strtoul(hex, NULL, 16);
                    /* surrogate pair */
                    if (cp >= 0xD800 && cp <= 0xDBFF && p[5] == '\\' && p[6] == 'u') {
                        char hex2[5] = { p[7], p[8], p[9], p[10], 0 };
                        unsigned lo = (unsigned)strtoul(hex2, NULL, 16);
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                        p += 6;
                    }
                    /* UTF-8 encode */
                    if (cp < 0x80) cs_sb_append_char(&sb, (char)cp);
                    else if (cp < 0x800) {
                        cs_sb_append_char(&sb, (char)(0xC0 | (cp >> 6)));
                        cs_sb_append_char(&sb, (char)(0x80 | (cp & 0x3F)));
                    } else if (cp < 0x10000) {
                        cs_sb_append_char(&sb, (char)(0xE0 | (cp >> 12)));
                        cs_sb_append_char(&sb, (char)(0x80 | ((cp >> 6) & 0x3F)));
                        cs_sb_append_char(&sb, (char)(0x80 | (cp & 0x3F)));
                    } else {
                        cs_sb_append_char(&sb, (char)(0xF0 | (cp >> 18)));
                        cs_sb_append_char(&sb, (char)(0x80 | ((cp >> 12) & 0x3F)));
                        cs_sb_append_char(&sb, (char)(0x80 | ((cp >> 6) & 0x3F)));
                        cs_sb_append_char(&sb, (char)(0x80 | (cp & 0x3F)));
                    }
                    p += 4;
                    break;
                }
                default: cs_sb_append_char(&sb, *p); break;
            }
            if (*p) ++p;
        } else {
            cs_sb_append_char(&sb, *p);
            ++p;
        }
    }
    if (!sb.buf) return cs_strdup("");
    return sb.buf;
}

/* Read a JSON string field by key → fresh string (or NULL). */
static char *cs_json_str_field(const char *json, const char *key) {
    const char *v = cs_json_find(json, key);
    if (!v || *v != '"') return NULL;
    return cs_json_read_string(v);
}
/* Read a JSON integer field by key. Returns fallback when absent. */
static int64_t cs_json_int_field(const char *json, const char *key, int64_t fallback) {
    const char *v = cs_json_find(json, key);
    if (!v) return fallback;
    return (int64_t)strtoll(v, NULL, 10);
}
/* Read a JSON bool field by key. */
static bool cs_json_bool_field(const char *json, const char *key, bool fallback) {
    const char *v = cs_json_find(json, key);
    if (!v) return fallback;
    if (strncmp(v, "true", 4) == 0) return true;
    if (strncmp(v, "false", 5) == 0) return false;
    return fallback;
}
/* Read a JSON string field, parse as ISO-8601 date → Unix ms. */
static int64_t cs_json_date_field(const char *json, const char *key, int64_t fallback) {
    char *s = cs_json_str_field(json, key);
    if (!s) return fallback;
    bool ok = false;
    int64_t ms = cs_iso8601_parse(s, &ok);
    free(s);
    return ok ? ms : fallback;
}

/* =====================================================================
 * LoraAdapterSyncBridge — LoraAdapterSyncBridge.cs
 * ===================================================================== */

void ca_lora_adapter_snapshot_free(ca_lora_adapter_snapshot_t *s) {
    if (!s) return;
    free(s->adapter_id);
    free(s->base64_bytes);
    s->adapter_id = s->base64_bytes = NULL;
}

/* Serialise a snapshot to JSON (System.Text.Json defaults: PascalCase). */
static char *cs_lora_snapshot_json(const ca_lora_adapter_snapshot_t *s) {
    cs_sb sb; memset(&sb, 0, sizeof(sb));
    char iso[48]; cs_iso8601(s->trained_at_ms, iso);
    char num[32];
    cs_sb_append(&sb, "{\"AdapterId\":\"");
    cs_json_escape(&sb, s->adapter_id ? s->adapter_id : "");
    cs_sb_append(&sb, "\",\"Base64Bytes\":\"");
    cs_json_escape(&sb, s->base64_bytes ? s->base64_bytes : "");
    cs_sb_append(&sb, "\",\"TrainedAtUtc\":\"");
    cs_sb_append(&sb, iso);
    cs_sb_append(&sb, "\",\"StepCount\":");
    snprintf(num, sizeof(num), "%" PRId64, s->step_count);
    cs_sb_append(&sb, num);
    cs_sb_append(&sb, "}");
    return sb.buf ? sb.buf : cs_strdup("{}");
}

bool ca_lora_sync_bridge_publish(ca_companion_state_sync_engine_t *engine,
                                 const char *adapter_id,
                                 const uint8_t *adapter_bytes, size_t adapter_len,
                                 int64_t trained_at_ms, int64_t step_count) {
    if (!engine || cs_blank(adapter_id)) return false;
    if (!adapter_bytes && adapter_len > 0) return false;
    char *b64 = ca_base64_encode(adapter_bytes ? adapter_bytes : (const uint8_t *)"", adapter_len);
    if (!b64) return false;
    ca_lora_adapter_snapshot_t snap;
    snap.adapter_id = (char *)adapter_id; /* borrowed for serialisation only */
    snap.base64_bytes = b64;
    snap.trained_at_ms = trained_at_ms;
    snap.step_count = step_count;
    char *json = cs_lora_snapshot_json(&snap);
    free(b64);
    if (!json) return false;
    bool ok = ca_sync_engine_write_local(engine, CA_LORA_SYNC_ENTITY_TYPE,
                                         adapter_id, json, false, NULL);
    free(json);
    return ok;
}

bool ca_lora_sync_bridge_try_write(const ca_syncable_entry_t *entry,
                                   ca_lora_adapter_snapshot_t *out_snapshot,
                                   uint8_t **out_bytes, size_t *out_len) {
    if (out_bytes) *out_bytes = NULL;
    if (out_len) *out_len = 0;
    if (!entry || entry->is_tombstone) return false;
    if (!entry->entity_type || strcmp(entry->entity_type, CA_LORA_SYNC_ENTITY_TYPE) != 0)
        return false;
    const char *json = entry->payload ? entry->payload : "";
    char *adapter_id = cs_json_str_field(json, "AdapterId");
    char *b64 = cs_json_str_field(json, "Base64Bytes");
    if (!adapter_id && !b64) { free(adapter_id); free(b64); return false; }
    ca_lora_adapter_snapshot_t snap;
    snap.adapter_id = adapter_id;
    snap.base64_bytes = b64 ? b64 : cs_strdup(""); /* Base64Bytes ?? "" */
    snap.trained_at_ms = cs_json_date_field(json, "TrainedAtUtc", 0);
    snap.step_count = cs_json_int_field(json, "StepCount", 0);
    if (out_snapshot) *out_snapshot = snap;

    if (snap.base64_bytes[0] != '\0' && out_bytes) {
        size_t blen = 0;
        uint8_t *bytes = ca_base64_decode(snap.base64_bytes, &blen);
        if (bytes) { *out_bytes = bytes; if (out_len) *out_len = blen; }
    }
    if (!out_snapshot) ca_lora_adapter_snapshot_free(&snap);
    return true;
}

/* =====================================================================
 * CompanionConversationSyncBridge — CompanionConversationSyncBridge.cs
 * ===================================================================== */

void ca_conversation_state_delta_free(ca_conversation_state_delta_t *d) {
    if (!d) return;
    free(d->session_id);
    free(d->user_text);
    free(d->assistant_text);
    d->session_id = d->user_text = d->assistant_text = NULL;
}

static char *cs_conversation_json(const ca_conversation_state_delta_t *d) {
    cs_sb sb; memset(&sb, 0, sizeof(sb));
    char started[48], updated[48];
    cs_iso8601(d->started_at_ms, started);
    cs_iso8601(d->updated_at_ms, updated);
    cs_sb_append(&sb, "{\"SessionId\":\"");
    cs_json_escape(&sb, d->session_id ? d->session_id : "");
    cs_sb_append(&sb, "\",\"UserText\":\"");
    cs_json_escape(&sb, d->user_text ? d->user_text : "");
    cs_sb_append(&sb, "\",\"AssistantText\":\"");
    cs_json_escape(&sb, d->assistant_text ? d->assistant_text : "");
    cs_sb_append(&sb, "\",\"IsTurnComplete\":");
    cs_sb_append(&sb, d->is_turn_complete ? "true" : "false");
    cs_sb_append(&sb, ",\"StartedAtUtc\":\"");
    cs_sb_append(&sb, started);
    cs_sb_append(&sb, "\",\"UpdatedAtUtc\":\"");
    cs_sb_append(&sb, updated);
    cs_sb_append(&sb, "\"}");
    return sb.buf ? sb.buf : cs_strdup("{}");
}

bool ca_conversation_sync_bridge_publish(
    ca_companion_state_sync_engine_t *engine,
    const ca_conversation_state_delta_t *delta) {
    if (!engine || !delta || cs_blank(delta->session_id)) return false;
    char *json = cs_conversation_json(delta);
    if (!json) return false;
    bool ok = ca_sync_engine_write_local(engine, CA_CONVERSATION_SYNC_ENTITY_TYPE,
                                         delta->session_id, json, false, NULL);
    free(json);
    return ok;
}

bool ca_conversation_sync_bridge_terminate(
    ca_companion_state_sync_engine_t *engine, const char *session_id) {
    if (!engine || cs_blank(session_id)) return false;
    return ca_sync_engine_write_local(engine, CA_CONVERSATION_SYNC_ENTITY_TYPE,
                                      session_id, "", true, NULL);
}

bool ca_conversation_sync_bridge_try_decode(
    const ca_syncable_entry_t *entry, ca_conversation_state_delta_t *out_delta) {
    if (!entry || !out_delta) return false;
    if (entry->is_tombstone) return false;
    if (!entry->entity_type || strcmp(entry->entity_type, CA_CONVERSATION_SYNC_ENTITY_TYPE) != 0)
        return false;
    const char *json = entry->payload ? entry->payload : "";
    memset(out_delta, 0, sizeof(*out_delta));
    out_delta->session_id      = cs_json_str_field(json, "SessionId");
    out_delta->user_text       = cs_json_str_field(json, "UserText");
    out_delta->assistant_text  = cs_json_str_field(json, "AssistantText");
    out_delta->is_turn_complete= cs_json_bool_field(json, "IsTurnComplete", false);
    out_delta->started_at_ms   = cs_json_date_field(json, "StartedAtUtc", 0);
    out_delta->updated_at_ms   = cs_json_date_field(json, "UpdatedAtUtc", 0);
    /* require at least a session id to consider it decoded */
    if (!out_delta->session_id) { ca_conversation_state_delta_free(out_delta); return false; }
    return true;
}
