/*
 * memory_sync_service.c — CircleAI.Sync (C11 port).
 *
 * Ports MemorySyncService.cs (+ the SyncDelta seam it uses) and SyncPrimitives.cs
 * (VersionVector + SyncReconciliation). No threads: the receive loop is a delta
 * callback the channel invokes per inbound delta.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/memory_sync_service.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <time.h>

static char *ms_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static bool ms_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p)
        if (!isspace(*p)) return false;
    return true;
}
static int64_t ms_now_ms(void) {
    return (int64_t)time(NULL) * 1000;
}

/* =====================================================================
 * SyncDelta
 * ===================================================================== */

void ca_sync_delta_full_free(ca_sync_delta_full_t *d) {
    if (!d) return;
    free(d->owner_id);
    free(d->source_device_id);
    free(d->target_device_id);
    free(d->domain_key);
    free(d->payload);
    d->owner_id = d->source_device_id = d->target_device_id = d->domain_key = NULL;
    d->payload = NULL; d->payload_len = 0;
}
ca_sync_delta_full_t *ca_sync_delta_full_copy(ca_sync_delta_full_t *dst,
                                              const ca_sync_delta_full_t *src) {
    if (!dst || !src) return dst;
    dst->owner_id         = ms_strdup(src->owner_id);
    dst->source_device_id = ms_strdup(src->source_device_id);
    dst->target_device_id = ms_strdup(src->target_device_id);
    dst->domain_key       = ms_strdup(src->domain_key);
    dst->payload_len      = src->payload_len;
    if (src->payload && src->payload_len) {
        dst->payload = (uint8_t *)malloc(src->payload_len);
        if (dst->payload) memcpy(dst->payload, src->payload, src->payload_len);
    } else {
        dst->payload = NULL;
    }
    dst->sequence         = src->sequence;
    dst->delivery_mode    = src->delivery_mode;
    dst->has_ttl          = src->has_ttl;
    dst->ttl_ms           = src->ttl_ms;
    dst->created_at_ms    = src->created_at_ms;
    dst->scheduling_hint  = src->scheduling_hint;
    return dst;
}

/* =====================================================================
 * VersionVector + SyncReconciliation — SyncPrimitives.cs
 * ===================================================================== */

ca_version_vector_t *ca_version_vector_create(const char *const *keys,
                                              const int64_t *clocks, size_t count) {
    ca_version_vector_t *v = (ca_version_vector_t *)calloc(1, sizeof(*v));
    if (!v) return NULL;
    if (count == 0) return v;
    v->keys = (char **)calloc(count, sizeof(char *));
    v->clocks = (int64_t *)calloc(count, sizeof(int64_t));
    if (!v->keys || !v->clocks) { ca_version_vector_destroy(v); return NULL; }
    for (size_t i = 0; i < count; ++i) {
        v->keys[i] = ms_strdup(keys ? keys[i] : NULL);
        v->clocks[i] = clocks ? clocks[i] : 0;
    }
    v->count = count;
    return v;
}
void ca_version_vector_destroy(ca_version_vector_t *v) {
    if (!v) return;
    if (v->keys) for (size_t i = 0; i < v->count; ++i) free(v->keys[i]);
    free(v->keys);
    free(v->clocks);
    free(v);
}
int64_t ca_version_vector_get(const ca_version_vector_t *v, const char *key) {
    if (!v || !key) return 0;
    for (size_t i = 0; i < v->count; ++i)
        if (v->keys[i] && strcmp(v->keys[i], key) == 0) return v->clocks[i];
    return 0;
}

/* union of keys as a temp string array (borrowed pointers into a,b). */
static const char **ms_union_keys(const ca_version_vector_t *a,
                                  const ca_version_vector_t *b, size_t *out_n) {
    size_t cap = a->count + b->count;
    const char **u = (const char **)malloc((cap ? cap : 1) * sizeof(char *));
    if (!u) { *out_n = 0; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < a->count; ++i) {
        bool seen = false;
        for (size_t j = 0; j < n; ++j) if (strcmp(u[j], a->keys[i]) == 0) { seen = true; break; }
        if (!seen) u[n++] = a->keys[i];
    }
    for (size_t i = 0; i < b->count; ++i) {
        bool seen = false;
        for (size_t j = 0; j < n; ++j) if (strcmp(u[j], b->keys[i]) == 0) { seen = true; break; }
        if (!seen) u[n++] = b->keys[i];
    }
    *out_n = n;
    return u;
}

ca_version_vector_t *ca_sync_reconciliation_merge(const ca_version_vector_t *a,
                                                  const ca_version_vector_t *b) {
    if (!a || !b) return NULL;
    size_t n = 0;
    const char **keys = ms_union_keys(a, b, &n);
    if (!keys && n) return NULL;
    int64_t *clocks = (int64_t *)malloc((n ? n : 1) * sizeof(int64_t));
    if (!clocks) { free(keys); return NULL; }
    for (size_t i = 0; i < n; ++i) {
        int64_t av = ca_version_vector_get(a, keys[i]);
        int64_t bv = ca_version_vector_get(b, keys[i]);
        clocks[i] = av > bv ? av : bv;
    }
    ca_version_vector_t *merged = ca_version_vector_create(keys, clocks, n);
    free(keys);
    free(clocks);
    return merged;
}

bool ca_sync_reconciliation_a_dominates_b(const ca_version_vector_t *a,
                                          const ca_version_vector_t *b) {
    if (!a || !b) return false;
    size_t n = 0;
    const char **keys = ms_union_keys(a, b, &n);
    if (!keys && n) return false;
    bool any_strictly_greater = false;
    bool dominates = true;
    for (size_t i = 0; i < n; ++i) {
        int64_t av = ca_version_vector_get(a, keys[i]);
        int64_t bv = ca_version_vector_get(b, keys[i]);
        if (av < bv) { dominates = false; break; }
        if (av > bv) any_strictly_greater = true;
    }
    free(keys);
    return dominates && any_strictly_greater;
}

int64_t ca_sync_reconciliation_last_writer_wins_i64(
    int64_t a_at, int64_t a_val, int64_t b_at, int64_t b_val, int64_t *out_at) {
    if (a_at >= b_at) { if (out_at) *out_at = a_at; return a_val; }
    if (out_at) *out_at = b_at;
    return b_val;
}

/* =====================================================================
 * MemorySyncService — MemorySyncService.cs
 * ===================================================================== */

struct ca_memory_sync_service {
    ca_sync_channel_iface_t channel;
    ca_episodic_apply_cb    episodic_apply;
    void                   *episodic_apply_user;
    char                   *local_device_id;
    void                   *subscription;   /* receive token, or NULL */
    char                   *receiving_owner;/* owner being received, or NULL */
};

ca_memory_sync_service_t *ca_memory_sync_service_create(
    ca_sync_channel_iface_t channel,
    ca_episodic_apply_cb episodic_apply, void *episodic_apply_user,
    const char *local_device_id) {
    if (!channel.self || !channel.push_delta) return NULL;
    if (ms_blank(local_device_id)) return NULL;
    ca_memory_sync_service_t *svc = (ca_memory_sync_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->channel = channel;
    svc->episodic_apply = episodic_apply;
    svc->episodic_apply_user = episodic_apply_user;
    svc->local_device_id = ms_strdup(local_device_id);
    return svc;
}

void ca_memory_sync_service_destroy(ca_memory_sync_service_t *svc) {
    if (!svc) return;
    ca_memory_sync_service_stop_receiving(svc);
    free(svc->local_device_id);
    free(svc->receiving_owner);
    free(svc);
}

bool ca_memory_sync_service_push_delta(
    ca_memory_sync_service_t *svc,
    const char *owner_id, const char *domain_key,
    const uint8_t *delta, size_t delta_len,
    ca_mss_delivery_mode_t mode) {
    if (!svc) return false;
    ca_sync_delta_full_t sd; memset(&sd, 0, sizeof(sd));
    sd.owner_id         = (char *)owner_id;          /* borrowed for the push */
    sd.source_device_id = svc->local_device_id;      /* borrowed */
    sd.target_device_id = (char *)"";                /* broadcast to all owned devices */
    sd.domain_key       = (char *)domain_key;        /* borrowed */
    sd.payload          = (uint8_t *)delta;          /* borrowed */
    sd.payload_len      = delta_len;
    sd.sequence         = ms_now_ms();               /* DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() */
    sd.delivery_mode    = mode;
    sd.has_ttl          = false;                     /* Ttl: null */
    sd.created_at_ms    = ms_now_ms();
    sd.scheduling_hint  = NULL;
    return svc->channel.push_delta(svc->channel.self, &sd);
}

/* ReceiveLoop: skip own echoes, then dispatch on DomainKey. */
static void ca_mss_receive_cb(void *user, const ca_sync_delta_full_t *delta) {
    ca_memory_sync_service_t *svc = (ca_memory_sync_service_t *)user;
    if (!svc || !delta) return;
    /* skip own echoes */
    if (delta->source_device_id && svc->local_device_id &&
        strcmp(delta->source_device_id, svc->local_device_id) == 0)
        return;
    if (delta->domain_key &&
        strcmp(delta->domain_key, CA_SYNC_DOMAIN_EPISODIC_MEMORY) == 0) {
        /* Full wire: deserialise + upsert into the local episodic store. */
        if (svc->episodic_apply)
            svc->episodic_apply(svc->episodic_apply_user, delta->owner_id,
                                delta->payload, delta->payload_len);
    }
    /* Additional domain handlers (affect, persona, goals) go here. */
}

bool ca_memory_sync_service_start_receiving(ca_memory_sync_service_t *svc,
                                            const char *owner_id) {
    if (!svc) return false;
    if (!svc->channel.receive_start) return false;
    /* replace any prior subscription (CreateLinkedTokenSource fresh each call). */
    if (svc->subscription && svc->channel.receive_stop)
        svc->channel.receive_stop(svc->channel.self, svc->subscription);
    free(svc->receiving_owner);
    svc->receiving_owner = ms_strdup(owner_id);
    svc->subscription = svc->channel.receive_start(
        svc->channel.self, owner_id, ca_mss_receive_cb, svc);
    return true;
}

void ca_memory_sync_service_stop_receiving(ca_memory_sync_service_t *svc) {
    if (!svc) return;
    if (svc->subscription && svc->channel.receive_stop)
        svc->channel.receive_stop(svc->channel.self, svc->subscription);
    svc->subscription = NULL;
}
