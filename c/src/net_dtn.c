/*
 * net_dtn.c — CircleAI.Networking.Dtn (C11 port).
 *
 * DtnBundle / DtnCustodyRecord records, DtnPriority, InMemoryDtnBundleStore, and
 * DtnSyncChannel (ISyncChannel over store-and-forward with first-available-
 * transport delivery + local queueing + per-owner/domain sequence tracking).
 *
 * Pure C11 + libc.
 */

#include "circle_ai/net_dtn.h"

#include <stdlib.h>
#include <string.h>

/* ---- helpers ---- */

static char *dup_or_null(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *dup_or_empty(const char *s) { return dup_or_null(s ? s : ""); }
static uint8_t *dup_bytes(const uint8_t *src, size_t len) {
    uint8_t *p = (uint8_t *)malloc(len ? len : 1);
    if (!p) return NULL;
    if (len && src) memcpy(p, src, len);
    return p;
}

/* ===========================================================================
 * DtnBundle
 * =========================================================================== */

ca_dtn_bundle_t *ca_dtn_bundle_new(
    const char *bundle_id, const char *source_node_id,
    const char *destination_node_id, const uint8_t *payload,
    size_t payload_len, int64_t expires_at_unix_ms, bool custody_required,
    int hop_count, int64_t created_at_unix_ms) {
    ca_dtn_bundle_t *b = (ca_dtn_bundle_t *)calloc(1, sizeof(*b));
    if (!b) return NULL;
    b->bundle_id = dup_or_empty(bundle_id);
    b->source_node_id = dup_or_empty(source_node_id);
    b->destination_node_id = dup_or_empty(destination_node_id);
    if (!b->bundle_id || !b->source_node_id || !b->destination_node_id)
        goto fail;
    b->payload = dup_bytes(payload, payload_len);
    if (!b->payload) goto fail;
    b->payload_len = payload_len;
    b->expires_at_unix_ms = expires_at_unix_ms;
    b->custody_required = custody_required;
    b->hop_count = hop_count;
    b->created_at_unix_ms = created_at_unix_ms;
    return b;
fail:
    ca_dtn_bundle_destroy(b);
    return NULL;
}

void ca_dtn_bundle_destroy(ca_dtn_bundle_t *b) {
    if (!b) return;
    free(b->bundle_id);
    free(b->source_node_id);
    free(b->destination_node_id);
    free(b->payload);
    free(b);
}

ca_dtn_bundle_t *ca_dtn_bundle_copy(const ca_dtn_bundle_t *b) {
    if (!b) return NULL;
    return ca_dtn_bundle_new(b->bundle_id, b->source_node_id,
                             b->destination_node_id, b->payload, b->payload_len,
                             b->expires_at_unix_ms, b->custody_required,
                             b->hop_count, b->created_at_unix_ms);
}

void ca_dtn_bundle_free_array(ca_dtn_bundle_t **arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_dtn_bundle_destroy(arr[i]);
    free(arr);
}

/* ===========================================================================
 * DtnCustodyRecord
 * =========================================================================== */

ca_dtn_custody_record_t *ca_dtn_custody_record_new(
    const char *bundle_id, const char *custodian_node,
    int64_t accepted_at_unix_ms) {
    ca_dtn_custody_record_t *r =
        (ca_dtn_custody_record_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->bundle_id = dup_or_empty(bundle_id);
    r->custodian_node = dup_or_empty(custodian_node);
    if (!r->bundle_id || !r->custodian_node) {
        ca_dtn_custody_record_destroy(r);
        return NULL;
    }
    r->accepted_at_unix_ms = accepted_at_unix_ms;
    return r;
}

void ca_dtn_custody_record_destroy(ca_dtn_custody_record_t *r) {
    if (!r) return;
    free(r->bundle_id);
    free(r->custodian_node);
    free(r);
}

ca_dtn_custody_record_t *ca_dtn_custody_record_copy(
    const ca_dtn_custody_record_t *r) {
    if (!r) return NULL;
    return ca_dtn_custody_record_new(r->bundle_id, r->custodian_node,
                                     r->accepted_at_unix_ms);
}

/* ===========================================================================
 * InMemoryDtnBundleStore
 * =========================================================================== */

struct ca_dtn_bundle_store {
    ca_dtn_bundle_t        **bundles; /* owned array (LWW by BundleId) */
    size_t                   bundle_count;
    size_t                   bundle_cap;
    ca_dtn_custody_record_t **custody; /* owned array (LWW by BundleId) */
    size_t                   custody_count;
    size_t                   custody_cap;
};

ca_dtn_bundle_store_t *ca_dtn_bundle_store_create(void) {
    return (ca_dtn_bundle_store_t *)calloc(1, sizeof(ca_dtn_bundle_store_t));
}

void ca_dtn_bundle_store_destroy(ca_dtn_bundle_store_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->bundle_count; ++i)
        ca_dtn_bundle_destroy(s->bundles[i]);
    free(s->bundles);
    for (size_t i = 0; i < s->custody_count; ++i)
        ca_dtn_custody_record_destroy(s->custody[i]);
    free(s->custody);
    free(s);
}

static ptrdiff_t store_bundle_index(const ca_dtn_bundle_store_t *s,
                                     const char *id) {
    for (size_t i = 0; i < s->bundle_count; ++i)
        if (strcmp(s->bundles[i]->bundle_id, id) == 0) return (ptrdiff_t)i;
    return -1;
}
static ptrdiff_t store_custody_index(const ca_dtn_bundle_store_t *s,
                                     const char *id) {
    for (size_t i = 0; i < s->custody_count; ++i)
        if (strcmp(s->custody[i]->bundle_id, id) == 0) return (ptrdiff_t)i;
    return -1;
}

int ca_dtn_bundle_store_store(ca_dtn_bundle_store_t *s,
                              const ca_dtn_bundle_t *b) {
    if (!s || !b) return -1;
    ca_dtn_bundle_t *copy = ca_dtn_bundle_copy(b);
    if (!copy) return -1;
    ptrdiff_t idx = store_bundle_index(s, b->bundle_id);
    if (idx >= 0) {
        ca_dtn_bundle_destroy(s->bundles[idx]);
        s->bundles[idx] = copy;
        return 0;
    }
    if (s->bundle_count == s->bundle_cap) {
        size_t nc = s->bundle_cap ? s->bundle_cap * 2 : 4;
        ca_dtn_bundle_t **nb =
            (ca_dtn_bundle_t **)realloc(s->bundles, nc * sizeof(*nb));
        if (!nb) { ca_dtn_bundle_destroy(copy); return -1; }
        s->bundles = nb;
        s->bundle_cap = nc;
    }
    s->bundles[s->bundle_count++] = copy;
    return 0;
}

ca_dtn_bundle_t *ca_dtn_bundle_store_get(const ca_dtn_bundle_store_t *s,
                                         const char *bundle_id) {
    if (!s || !bundle_id) return NULL;
    ptrdiff_t idx = store_bundle_index(s, bundle_id);
    return idx < 0 ? NULL : ca_dtn_bundle_copy(s->bundles[idx]);
}

int ca_dtn_bundle_store_all(const ca_dtn_bundle_store_t *s,
                            ca_dtn_bundle_t ***out, size_t *count) {
    if (!s || !out || !count) { if (out) *out = NULL; if (count) *count = SIZE_MAX; return -1; }
    if (s->bundle_count == 0) { *out = NULL; *count = 0; return 0; }
    ca_dtn_bundle_t **arr =
        (ca_dtn_bundle_t **)calloc(s->bundle_count, sizeof(*arr));
    if (!arr) { *out = NULL; *count = SIZE_MAX; return -1; }
    for (size_t i = 0; i < s->bundle_count; ++i) {
        arr[i] = ca_dtn_bundle_copy(s->bundles[i]);
        if (!arr[i]) {
            ca_dtn_bundle_free_array(arr, i);
            *out = NULL; *count = SIZE_MAX;
            return -1;
        }
    }
    *out = arr;
    *count = s->bundle_count;
    return 0;
}

int ca_dtn_bundle_store_accept_custody(ca_dtn_bundle_store_t *s,
                                       const ca_dtn_custody_record_t *r) {
    if (!s || !r) return -1;
    ca_dtn_custody_record_t *copy = ca_dtn_custody_record_copy(r);
    if (!copy) return -1;
    ptrdiff_t idx = store_custody_index(s, r->bundle_id);
    if (idx >= 0) {
        ca_dtn_custody_record_destroy(s->custody[idx]);
        s->custody[idx] = copy;
        return 0;
    }
    if (s->custody_count == s->custody_cap) {
        size_t nc = s->custody_cap ? s->custody_cap * 2 : 4;
        ca_dtn_custody_record_t **nc2 =
            (ca_dtn_custody_record_t **)realloc(s->custody, nc * sizeof(*nc2));
        if (!nc2) { ca_dtn_custody_record_destroy(copy); return -1; }
        s->custody = nc2;
        s->custody_cap = nc;
    }
    s->custody[s->custody_count++] = copy;
    return 0;
}

ca_dtn_custody_record_t *ca_dtn_bundle_store_get_custody(
    const ca_dtn_bundle_store_t *s, const char *bundle_id) {
    if (!s || !bundle_id) return NULL;
    ptrdiff_t idx = store_custody_index(s, bundle_id);
    return idx < 0 ? NULL : ca_dtn_custody_record_copy(s->custody[idx]);
}

bool ca_dtn_bundle_store_is_expired(const ca_dtn_bundle_store_t *s,
                                    const char *bundle_id,
                                    int64_t now_unix_ms) {
    if (!s || !bundle_id) return true;
    ptrdiff_t idx = store_bundle_index(s, bundle_id);
    if (idx < 0) return true; /* absent => treated as expired */
    return now_unix_ms > s->bundles[idx]->expires_at_unix_ms;
}

/* Remove custody record for a bundle id (if any). */
static void store_remove_custody(ca_dtn_bundle_store_t *s, const char *id) {
    ptrdiff_t idx = store_custody_index(s, id);
    if (idx < 0) return;
    ca_dtn_custody_record_destroy(s->custody[idx]);
    s->custody[idx] = s->custody[--s->custody_count];
}

int ca_dtn_bundle_store_purge(ca_dtn_bundle_store_t *s, int64_t now_unix_ms) {
    if (!s) return 0;
    int removed = 0;
    size_t i = 0;
    while (i < s->bundle_count) {
        if (now_unix_ms > s->bundles[i]->expires_at_unix_ms) {
            char *id = s->bundles[i]->bundle_id; /* borrow before destroy */
            store_remove_custody(s, id);
            ca_dtn_bundle_destroy(s->bundles[i]);
            s->bundles[i] = s->bundles[--s->bundle_count];
            removed++;
            /* do not advance i: the swapped-in element must be checked */
        } else {
            i++;
        }
    }
    return removed;
}

int ca_dtn_bundle_store_in_flight_to(const ca_dtn_bundle_store_t *s,
                                     const char *destination_node_id,
                                     ca_dtn_bundle_t ***out, size_t *count) {
    if (!s || !destination_node_id || !out || !count) {
        if (out) *out = NULL;
        if (count) *count = SIZE_MAX;
        return -1;
    }
    size_t n = 0;
    for (size_t i = 0; i < s->bundle_count; ++i)
        if (strcmp(s->bundles[i]->destination_node_id, destination_node_id) == 0)
            n++;
    if (n == 0) { *out = NULL; *count = 0; return 0; }
    ca_dtn_bundle_t **arr = (ca_dtn_bundle_t **)calloc(n, sizeof(*arr));
    if (!arr) { *out = NULL; *count = SIZE_MAX; return -1; }
    size_t j = 0;
    for (size_t i = 0; i < s->bundle_count; ++i) {
        if (strcmp(s->bundles[i]->destination_node_id,
                   destination_node_id) == 0) {
            arr[j] = ca_dtn_bundle_copy(s->bundles[i]);
            if (!arr[j]) {
                ca_dtn_bundle_free_array(arr, j);
                *out = NULL; *count = SIZE_MAX;
                return -1;
            }
            j++;
        }
    }
    *out = arr;
    *count = n;
    return 0;
}

/* ===========================================================================
 * Unbounded FIFO of SyncDelta* (delivered channel)
 * =========================================================================== */

typedef struct {
    ca_net_sync_delta_t **items;
    size_t head, count, cap;
} delta_fifo_t;

static bool df_push(delta_fifo_t *q, ca_net_sync_delta_t *owned) {
    if (q->count == q->cap) {
        if (q->head > 0) {
            size_t live = q->count - q->head;
            memmove(q->items, q->items + q->head, live * sizeof(*q->items));
            q->count = live;
            q->head = 0;
        }
        if (q->count == q->cap) {
            size_t nc = q->cap ? q->cap * 2 : 4;
            ca_net_sync_delta_t **ni =
                (ca_net_sync_delta_t **)realloc(q->items, nc * sizeof(*ni));
            if (!ni) return false;
            q->items = ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = owned;
    return true;
}
static ca_net_sync_delta_t *df_pop(delta_fifo_t *q) {
    if (q->head >= q->count) return NULL;
    ca_net_sync_delta_t *d = q->items[q->head];
    q->items[q->head++] = NULL;
    if (q->head == q->count) { q->head = 0; q->count = 0; }
    return d;
}
static void df_free(delta_fifo_t *q) {
    for (size_t i = q->head; i < q->count; ++i)
        ca_net_sync_delta_destroy(q->items[i]);
    free(q->items);
    q->items = NULL;
    q->head = q->count = q->cap = 0;
}

/* ===========================================================================
 * DtnSyncChannel
 * =========================================================================== */

typedef struct {
    char   *owner_id;   /* owned */
    char   *domain_key; /* owned */
    int64_t sequence;
} seq_entry_t;

struct ca_dtn_sync_channel {
    ca_network_transport_t *transports; /* owned array of borrowed vtables */
    size_t                  transport_count;

    delta_fifo_t            delivered;

    seq_entry_t            *seqs;
    size_t                  seq_count;
    size_t                  seq_cap;

    /* Bundles queued locally because no transport was available. */
    ca_dtn_bundle_t       **queued;
    size_t                  queued_count;
    size_t                  queued_cap;
};

ca_dtn_sync_channel_t *ca_dtn_sync_channel_create(
    const ca_network_transport_t *transports, size_t count) {
    ca_dtn_sync_channel_t *c =
        (ca_dtn_sync_channel_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    if (count > 0) {
        c->transports =
            (ca_network_transport_t *)calloc(count, sizeof(*c->transports));
        if (!c->transports) { free(c); return NULL; }
        if (transports)
            memcpy(c->transports, transports,
                   count * sizeof(*c->transports));
        c->transport_count = count;
    }
    return c;
}

void ca_dtn_sync_channel_destroy(ca_dtn_sync_channel_t *c) {
    if (!c) return;
    free(c->transports);
    df_free(&c->delivered);
    for (size_t i = 0; i < c->seq_count; ++i) {
        free(c->seqs[i].owner_id);
        free(c->seqs[i].domain_key);
    }
    free(c->seqs);
    for (size_t i = 0; i < c->queued_count; ++i)
        ca_dtn_bundle_destroy(c->queued[i]);
    free(c->queued);
    free(c);
}

static int queue_bundle(ca_dtn_sync_channel_t *c, ca_dtn_bundle_t *owned) {
    if (c->queued_count == c->queued_cap) {
        size_t nc = c->queued_cap ? c->queued_cap * 2 : 4;
        ca_dtn_bundle_t **nq =
            (ca_dtn_bundle_t **)realloc(c->queued, nc * sizeof(*nq));
        if (!nq) return -1;
        c->queued = nq;
        c->queued_cap = nc;
    }
    c->queued[c->queued_count++] = owned;
    return 0;
}

int ca_dtn_sync_channel_push_delta(ca_dtn_sync_channel_t *c,
                                   const ca_net_sync_delta_t *delta,
                                   const char *bundle_id_n,
                                   int64_t now_unix_ms) {
    if (!c || !delta) return -1;

    int64_t ttl_ms = delta->has_ttl ? delta->ttl_ms : CA_DTN_DEFAULT_TTL_MS;
    bool custody = delta->delivery_mode == CA_NET_DELIVERY_GUARANTEED;

    ca_dtn_bundle_t *bundle = ca_dtn_bundle_new(
        bundle_id_n, delta->source_device_id, delta->target_device_id,
        delta->payload, delta->payload_len, now_unix_ms + ttl_ms, custody,
        /*HopCount*/ 0, now_unix_ms);
    if (!bundle) return -1;

    /* var available = _transports.Where(t => t.IsAvailable).ToList();
     * if (available.Count > 0) send over available[0]. */
    ca_network_transport_t *chosen = NULL;
    for (size_t i = 0; i < c->transport_count; ++i) {
        ca_network_transport_t *t = &c->transports[i];
        if (t->is_available && t->is_available(t->self)) { chosen = t; break; }
    }

    if (chosen) {
        ca_message_priority_t prio =
            delta->delivery_mode == CA_NET_DELIVERY_URGENT
                ? CA_MSG_PRIORITY_URGENT
                : CA_MSG_PRIORITY_NORMAL;
        /* NetworkPayload.Create(payload, target, prio, "application/dtn-bundle").
         * Create() generates a Guid "N" id + CreatedAt=now; we pass now_unix_ms
         * and a fresh guid id for parity with the C# static factory. */
        char id[33];
        ca_net_new_guid_n(id);
        ca_network_payload_t *payload = ca_network_payload_create(
            delta->payload, delta->payload_len, delta->target_device_id, prio,
            "application/dtn-bundle", /*has_ttl*/ false, /*ttl_ms*/ 0,
            now_unix_ms, id);
        if (!payload) { ca_dtn_bundle_destroy(bundle); return -1; }
        int rc = chosen->send ? chosen->send(chosen->self, payload) : -1;
        ca_network_payload_destroy(payload);
        /* The bundle was handed to a live transport; it is not locally queued. */
        ca_dtn_bundle_destroy(bundle);
        return rc == 0 ? 0 : -1;
    }

    /* No transport available: queue locally for later delivery. */
    if (queue_bundle(c, bundle) != 0) {
        ca_dtn_bundle_destroy(bundle);
        return -1;
    }
    return 0;
}

int ca_dtn_sync_channel_deliver(ca_dtn_sync_channel_t *c,
                                const ca_net_sync_delta_t *delta) {
    if (!c || !delta) return -1;
    ca_net_sync_delta_t *copy = ca_net_sync_delta_copy(delta);
    if (!copy) return -1;
    if (!df_push(&c->delivered, copy)) {
        ca_net_sync_delta_destroy(copy);
        return -1;
    }
    return 0;
}

bool ca_dtn_sync_channel_receive_next(ca_dtn_sync_channel_t *c,
                                      const char *owner_id,
                                      ca_net_sync_delta_t **out) {
    /* ReadAllAsync — ownerId is not a filter in the C# (the whole channel is
     * drained); kept for signature parity. */
    (void)owner_id;
    if (!c || !out) return false;
    ca_net_sync_delta_t *d = df_pop(&c->delivered);
    if (!d) return false;
    *out = d;
    return true;
}

static ptrdiff_t seq_index(const ca_dtn_sync_channel_t *c,
                           const char *owner_id, const char *domain_key) {
    for (size_t i = 0; i < c->seq_count; ++i)
        if (strcmp(c->seqs[i].owner_id, owner_id) == 0 &&
            strcmp(c->seqs[i].domain_key, domain_key) == 0)
            return (ptrdiff_t)i;
    return -1;
}

int64_t ca_dtn_sync_channel_last_sequence(const ca_dtn_sync_channel_t *c,
                                          const char *owner_id,
                                          const char *domain_key) {
    if (!c || !owner_id || !domain_key) return 0;
    ptrdiff_t idx = seq_index(c, owner_id, domain_key);
    return idx < 0 ? 0 : c->seqs[idx].sequence;
}

int ca_dtn_sync_channel_set_sequence(ca_dtn_sync_channel_t *c,
                                     const char *owner_id,
                                     const char *domain_key, int64_t seq) {
    if (!c || !owner_id || !domain_key) return -1;
    ptrdiff_t idx = seq_index(c, owner_id, domain_key);
    if (idx >= 0) { c->seqs[idx].sequence = seq; return 0; }
    if (c->seq_count == c->seq_cap) {
        size_t nc = c->seq_cap ? c->seq_cap * 2 : 4;
        seq_entry_t *ne = (seq_entry_t *)realloc(c->seqs, nc * sizeof(*ne));
        if (!ne) return -1;
        c->seqs = ne;
        c->seq_cap = nc;
    }
    seq_entry_t *e = &c->seqs[c->seq_count];
    e->owner_id = dup_or_empty(owner_id);
    e->domain_key = dup_or_empty(domain_key);
    if (!e->owner_id || !e->domain_key) {
        free(e->owner_id); free(e->domain_key);
        return -1;
    }
    e->sequence = seq;
    c->seq_count++;
    return 0;
}

size_t ca_dtn_sync_channel_queued(const ca_dtn_sync_channel_t *c) {
    return c ? c->queued_count : 0;
}

size_t ca_dtn_sync_channel_pending(const ca_dtn_sync_channel_t *c) {
    return c ? (c->delivered.count - c->delivered.head) : 0;
}
