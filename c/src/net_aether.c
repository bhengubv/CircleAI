/*
 * net_aether.c — CircleAI.Networking.AetherNet (C11 port).
 *
 * AetherPeer / AetherHopTelemetry / AetherPacketSummary records, the
 * InMemoryAetherNetRegistry, the injected IAetherContext seam + an in-memory
 * implementation, and the three bindings: AetherNetworkTransport (INetworkTransport),
 * AetherPeerDiscovery (IPeerDiscovery), AetherSyncChannel (ISyncChannel).
 *
 * Pure C11 + libc.
 */

#include "circle_ai/net_aether.h"

#include <stdlib.h>
#include <string.h>

/* ---------------------------------------------------------------------------
 * small helpers
 * --------------------------------------------------------------------------- */

static char *dup_or_null(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *dup_or_empty(const char *s) { return dup_or_null(s ? s : ""); }

static char **dup_str_array(const char *const *src, size_t count, bool *ok) {
    *ok = true;
    if (count == 0) return NULL;
    char **out = (char **)calloc(count, sizeof(*out));
    if (!out) { *ok = false; return NULL; }
    for (size_t i = 0; i < count; ++i) {
        out[i] = dup_or_empty(src ? src[i] : NULL);
        if (!out[i]) {
            for (size_t j = 0; j < i; ++j) free(out[j]);
            free(out);
            *ok = false;
            return NULL;
        }
    }
    return out;
}
static void free_str_array(char **a, size_t n) {
    if (!a) return;
    for (size_t i = 0; i < n; ++i) free(a[i]);
    free(a);
}

/* ===========================================================================
 * AetherPeer
 * =========================================================================== */

ca_aether_peer_t *ca_aether_peer_new(
    const char *peer_id, ca_aether_peer_kind_t kind, const char *friendly_name,
    const char *const *capabilities, size_t capability_count) {
    ca_aether_peer_t *p = (ca_aether_peer_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->peer_id = dup_or_empty(peer_id);
    if (!p->peer_id) { free(p); return NULL; }
    p->kind = kind;
    if (friendly_name) {
        p->friendly_name = dup_or_null(friendly_name);
        if (!p->friendly_name) { ca_aether_peer_destroy(p); return NULL; }
    }
    bool ok = true;
    p->capabilities = dup_str_array(capabilities, capability_count, &ok);
    if (!ok) { ca_aether_peer_destroy(p); return NULL; }
    p->capability_count = capability_count;
    return p;
}

void ca_aether_peer_destroy(ca_aether_peer_t *p) {
    if (!p) return;
    free(p->peer_id);
    free(p->friendly_name);
    free_str_array(p->capabilities, p->capability_count);
    free(p);
}

ca_aether_peer_t *ca_aether_peer_copy(const ca_aether_peer_t *p) {
    if (!p) return NULL;
    return ca_aether_peer_new(p->peer_id, p->kind, p->friendly_name,
                              (const char *const *)p->capabilities,
                              p->capability_count);
}

/* ===========================================================================
 * AetherPacketSummary free helpers
 * =========================================================================== */

void ca_aether_packet_summary_free(ca_aether_packet_summary_t *p) {
    if (!p) return;
    free(p->packet_id);
    free(p->from_peer);
    free(p->to_peer);
    free(p->packet_kind);
    p->packet_id = p->from_peer = p->to_peer = p->packet_kind = NULL;
}

void ca_aether_packet_summary_free_array(ca_aether_packet_summary_t *arr,
                                         size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_aether_packet_summary_free(&arr[i]);
    free(arr);
}

/* ===========================================================================
 * InMemoryAetherNetRegistry
 * =========================================================================== */

struct ca_aethernet_registry {
    ca_aether_peer_t          **peers;      /* owned array of owned peers (LWW) */
    size_t                      peer_count;
    size_t                      peer_cap;

    ca_aether_hop_telemetry_t  *hops;       /* owned array */
    size_t                      hop_count;
    size_t                      hop_cap;

    ca_aether_packet_summary_t *packets;    /* owned array */
    size_t                      packet_count;
    size_t                      packet_cap;
};

ca_aethernet_registry_t *ca_aethernet_registry_create(void) {
    return (ca_aethernet_registry_t *)calloc(1, sizeof(ca_aethernet_registry_t));
}

void ca_aethernet_registry_destroy(ca_aethernet_registry_t *r) {
    if (!r) return;
    for (size_t i = 0; i < r->peer_count; ++i)
        ca_aether_peer_destroy(r->peers[i]);
    free(r->peers);
    for (size_t i = 0; i < r->hop_count; ++i) free(r->hops[i].peer_id);
    free(r->hops);
    for (size_t i = 0; i < r->packet_count; ++i)
        ca_aether_packet_summary_free(&r->packets[i]);
    free(r->packets);
    free(r);
}

/* Find index of a peer by id, or -1. */
static ptrdiff_t reg_peer_index(const ca_aethernet_registry_t *r,
                                const char *peer_id) {
    for (size_t i = 0; i < r->peer_count; ++i)
        if (strcmp(r->peers[i]->peer_id, peer_id) == 0) return (ptrdiff_t)i;
    return -1;
}

int ca_aethernet_registry_register(ca_aethernet_registry_t *r,
                                   const ca_aether_peer_t *peer) {
    if (!r || !peer) return -1;
    ca_aether_peer_t *copy = ca_aether_peer_copy(peer);
    if (!copy) return -1;
    ptrdiff_t idx = reg_peer_index(r, peer->peer_id);
    if (idx >= 0) { /* LWW: replace */
        ca_aether_peer_destroy(r->peers[idx]);
        r->peers[idx] = copy;
        return 0;
    }
    if (r->peer_count == r->peer_cap) {
        size_t nc = r->peer_cap ? r->peer_cap * 2 : 4;
        ca_aether_peer_t **np =
            (ca_aether_peer_t **)realloc(r->peers, nc * sizeof(*np));
        if (!np) { ca_aether_peer_destroy(copy); return -1; }
        r->peers = np;
        r->peer_cap = nc;
    }
    r->peers[r->peer_count++] = copy;
    return 0;
}

ca_aether_peer_t *ca_aethernet_registry_get_peer(
    const ca_aethernet_registry_t *r, const char *peer_id) {
    if (!r || !peer_id) return NULL;
    ptrdiff_t idx = reg_peer_index(r, peer_id);
    if (idx < 0) return NULL;
    return ca_aether_peer_copy(r->peers[idx]);
}

/* qsort comparator: by peer_id ascending (ordinal). */
static int cmp_peer_by_id(const void *a, const void *b) {
    const ca_aether_peer_t *pa = *(const ca_aether_peer_t *const *)a;
    const ca_aether_peer_t *pb = *(const ca_aether_peer_t *const *)b;
    return strcmp(pa->peer_id, pb->peer_id);
}

int ca_aethernet_registry_peers(const ca_aethernet_registry_t *r,
                                ca_aether_peer_t ***out, size_t *count) {
    if (!r || !out || !count) { if (out) *out = NULL; if (count) *count = SIZE_MAX; return -1; }
    if (r->peer_count == 0) { *out = NULL; *count = 0; return 0; }
    ca_aether_peer_t **arr =
        (ca_aether_peer_t **)calloc(r->peer_count, sizeof(*arr));
    if (!arr) { *out = NULL; *count = SIZE_MAX; return -1; }
    for (size_t i = 0; i < r->peer_count; ++i) {
        arr[i] = ca_aether_peer_copy(r->peers[i]);
        if (!arr[i]) {
            for (size_t j = 0; j < i; ++j) ca_aether_peer_destroy(arr[j]);
            free(arr);
            *out = NULL; *count = SIZE_MAX;
            return -1;
        }
    }
    qsort(arr, r->peer_count, sizeof(*arr), cmp_peer_by_id);
    *out = arr;
    *count = r->peer_count;
    return 0;
}

int ca_aethernet_registry_record_hop(ca_aethernet_registry_t *r,
                                     const char *peer_id, int hop_count,
                                     double round_trip_ms, int64_t at_unix_ms) {
    if (!r) return -1;
    if (r->hop_count == r->hop_cap) {
        size_t nc = r->hop_cap ? r->hop_cap * 2 : 4;
        ca_aether_hop_telemetry_t *nh =
            (ca_aether_hop_telemetry_t *)realloc(r->hops, nc * sizeof(*nh));
        if (!nh) return -1;
        r->hops = nh;
        r->hop_cap = nc;
    }
    ca_aether_hop_telemetry_t *t = &r->hops[r->hop_count];
    t->peer_id = dup_or_empty(peer_id);
    if (!t->peer_id) return -1;
    t->hop_count = hop_count;
    t->round_trip_ms = round_trip_ms;
    t->at_unix_ms = at_unix_ms;
    r->hop_count++;
    return 0;
}

int ca_aethernet_registry_record_packet(ca_aethernet_registry_t *r,
                                        const char *packet_id,
                                        const char *from_peer,
                                        const char *to_peer, int bytes,
                                        const char *packet_kind,
                                        int64_t at_unix_ms) {
    if (!r) return -1;
    if (r->packet_count == r->packet_cap) {
        size_t nc = r->packet_cap ? r->packet_cap * 2 : 4;
        ca_aether_packet_summary_t *np =
            (ca_aether_packet_summary_t *)realloc(r->packets, nc * sizeof(*np));
        if (!np) return -1;
        r->packets = np;
        r->packet_cap = nc;
    }
    ca_aether_packet_summary_t *p = &r->packets[r->packet_count];
    memset(p, 0, sizeof(*p));
    p->packet_id = dup_or_empty(packet_id);
    p->from_peer = dup_or_empty(from_peer);
    p->to_peer = dup_or_empty(to_peer);
    p->packet_kind = dup_or_empty(packet_kind);
    if (!p->packet_id || !p->from_peer || !p->to_peer || !p->packet_kind) {
        ca_aether_packet_summary_free(p);
        return -1;
    }
    p->bytes = bytes;
    p->at_unix_ms = at_unix_ms;
    r->packet_count++;
    return 0;
}

/* Stable descending-by-at_unix_ms comparator that preserves insertion order for
 * ties (mirrors LINQ OrderByDescending stability). Uses the packing of the index
 * into a paired struct below. */
typedef struct { const ca_aether_packet_summary_t *p; size_t ord; } pkt_ref_t;
static int cmp_pkt_desc(const void *a, const void *b) {
    const pkt_ref_t *ra = (const pkt_ref_t *)a;
    const pkt_ref_t *rb = (const pkt_ref_t *)b;
    if (ra->p->at_unix_ms > rb->p->at_unix_ms) return -1;
    if (ra->p->at_unix_ms < rb->p->at_unix_ms) return 1;
    /* stable: earlier insertion first */
    if (ra->ord < rb->ord) return -1;
    if (ra->ord > rb->ord) return 1;
    return 0;
}

ca_aether_packet_summary_t *ca_aethernet_registry_recent_packets(
    const ca_aethernet_registry_t *r, int limit, size_t *count) {
    if (!r || !count) { if (count) *count = SIZE_MAX; return NULL; }
    if (limit < 0) limit = 0;
    size_t take = (size_t)limit < r->packet_count ? (size_t)limit
                                                  : r->packet_count;
    if (take == 0) { *count = 0; return NULL; }

    pkt_ref_t *refs = (pkt_ref_t *)calloc(r->packet_count, sizeof(*refs));
    if (!refs) { *count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < r->packet_count; ++i) {
        refs[i].p = &r->packets[i];
        refs[i].ord = i;
    }
    qsort(refs, r->packet_count, sizeof(*refs), cmp_pkt_desc);

    ca_aether_packet_summary_t *out =
        (ca_aether_packet_summary_t *)calloc(take, sizeof(*out));
    if (!out) { free(refs); *count = SIZE_MAX; return NULL; }
    for (size_t i = 0; i < take; ++i) {
        const ca_aether_packet_summary_t *s = refs[i].p;
        out[i].packet_id = dup_or_empty(s->packet_id);
        out[i].from_peer = dup_or_empty(s->from_peer);
        out[i].to_peer = dup_or_empty(s->to_peer);
        out[i].packet_kind = dup_or_empty(s->packet_kind);
        out[i].bytes = s->bytes;
        out[i].at_unix_ms = s->at_unix_ms;
        if (!out[i].packet_id || !out[i].from_peer || !out[i].to_peer ||
            !out[i].packet_kind) {
            ca_aether_packet_summary_free_array(out, i + 1);
            free(refs);
            *count = SIZE_MAX;
            return NULL;
        }
    }
    free(refs);
    *count = take;
    return out;
}

double ca_aethernet_registry_avg_round_trip_ms(
    const ca_aethernet_registry_t *r, const char *peer_id) {
    /* Where(t=>t.PeerId==peerId).Select(RoundTripMs).DefaultIfEmpty(0).Average() */
    if (!r || !peer_id) return 0.0;
    double sum = 0.0;
    size_t n = 0;
    for (size_t i = 0; i < r->hop_count; ++i) {
        if (strcmp(r->hops[i].peer_id, peer_id) == 0) {
            sum += r->hops[i].round_trip_ms;
            n++;
        }
    }
    return n == 0 ? 0.0 : sum / (double)n;
}

int ca_aethernet_registry_total_bytes_between(
    const ca_aethernet_registry_t *r, const char *from_peer,
    const char *to_peer) {
    if (!r || !from_peer || !to_peer) return 0;
    int total = 0;
    for (size_t i = 0; i < r->packet_count; ++i) {
        if (strcmp(r->packets[i].from_peer, from_peer) == 0 &&
            strcmp(r->packets[i].to_peer, to_peer) == 0)
            total += r->packets[i].bytes;
    }
    return total;
}

/* ===========================================================================
 * IAetherContext — reused from aether.h (ca_aether_context_t vtable +
 * ca_aether_context_impl_* in-memory impl). No local definition needed.
 * =========================================================================== */

/* ===========================================================================
 * Unbounded FIFO of NetworkPayload* (shared by the transport inbound channel)
 * =========================================================================== */

typedef struct {
    ca_network_payload_t **items;
    size_t head;
    size_t count;
    size_t cap;
} payload_fifo_t;

static bool pf_push(payload_fifo_t *q, ca_network_payload_t *owned) {
    if (q->count == q->cap) {
        if (q->head > 0) {
            size_t live = q->count - q->head;
            memmove(q->items, q->items + q->head, live * sizeof(*q->items));
            q->count = live;
            q->head = 0;
        }
        if (q->count == q->cap) {
            size_t nc = q->cap ? q->cap * 2 : 4;
            ca_network_payload_t **ni =
                (ca_network_payload_t **)realloc(q->items, nc * sizeof(*ni));
            if (!ni) return false;
            q->items = ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = owned;
    return true;
}
static ca_network_payload_t *pf_pop(payload_fifo_t *q) {
    if (q->head >= q->count) return NULL;
    ca_network_payload_t *p = q->items[q->head];
    q->items[q->head++] = NULL;
    if (q->head == q->count) { q->head = 0; q->count = 0; }
    return p;
}
static void pf_free(payload_fifo_t *q) {
    for (size_t i = q->head; i < q->count; ++i)
        ca_network_payload_destroy(q->items[i]);
    free(q->items);
    q->items = NULL;
    q->head = q->count = q->cap = 0;
}

/* ===========================================================================
 * AetherNetworkTransport
 * =========================================================================== */

struct ca_aether_transport {
    ca_aether_context_t context;   /* borrowed vtable */
    payload_fifo_t      inbound;
    bool                inbound_open; /* channel completed by StopAsync */
};

ca_aether_transport_t *ca_aether_transport_create(ca_aether_context_t context) {
    ca_aether_transport_t *t = (ca_aether_transport_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->context = context;
    t->inbound_open = true;
    return t;
}

void ca_aether_transport_destroy(ca_aether_transport_t *t) {
    if (!t) return;
    pf_free(&t->inbound);
    free(t);
}

static ca_transport_kind_t at_kind(void *self) {
    (void)self;
    return CA_TRANSPORT_AETHER;
}
static bool at_available(void *self) {
    ca_aether_transport_t *t = (ca_aether_transport_t *)self;
    return t->context.is_available ? t->context.is_available(t->context.self)
                                   : false;
}
static int at_start(void *self) { (void)self; return 0; /* Task.CompletedTask */ }
static int at_stop(void *self) {
    /* _inbound.Writer.TryComplete() */
    ((ca_aether_transport_t *)self)->inbound_open = false;
    return 0;
}
static int at_send(void *self, const ca_network_payload_t *payload) {
    /* Routing delegated to aether-protocol; bridge is a completed no-op. */
    (void)self;
    if (!payload) return -1;
    (void)payload->priority; /* _ = payload.Priority; */
    return 0;
}
static bool at_receive_next(void *self, ca_network_payload_t **out) {
    ca_aether_transport_t *t = (ca_aether_transport_t *)self;
    if (!out) return false;
    ca_network_payload_t *p = pf_pop(&t->inbound);
    if (!p) return false;
    *out = p;
    return true;
}

ca_network_transport_t ca_aether_transport_as_transport(
    ca_aether_transport_t *t) {
    ca_network_transport_t v;
    v.self = t;
    v.kind = at_kind;
    v.is_available = at_available;
    v.start = at_start;
    v.stop = at_stop;
    v.send = at_send;
    v.receive_next = at_receive_next;
    return v;
}

int ca_aether_transport_inject(ca_aether_transport_t *t,
                               const ca_network_payload_t *payload) {
    if (!t || !payload) return -1;
    if (!t->inbound_open) return -1; /* channel completed */
    ca_network_payload_t *copy = ca_network_payload_copy(payload);
    if (!copy) return -1;
    if (!pf_push(&t->inbound, copy)) {
        ca_network_payload_destroy(copy);
        return -1;
    }
    return 0;
}

size_t ca_aether_transport_pending(const ca_aether_transport_t *t) {
    return t ? (t->inbound.count - t->inbound.head) : 0;
}

/* ===========================================================================
 * AetherPeerDiscovery
 * =========================================================================== */

struct ca_aether_discovery {
    ca_aether_context_t context;       /* borrowed */
    ca_peer_info_t     *last_announced; /* owned, may be NULL */
};

ca_aether_discovery_t *ca_aether_discovery_create(ca_aether_context_t context) {
    ca_aether_discovery_t *d = (ca_aether_discovery_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->context = context;
    return d;
}

void ca_aether_discovery_destroy(ca_aether_discovery_t *d) {
    if (!d) return;
    ca_peer_info_destroy(d->last_announced);
    free(d);
}

bool ca_aether_discovery_discover_next(ca_aether_discovery_t *d,
                                       ca_peer_info_t **out) {
    /* DiscoverAsync yield break; — empty stream. */
    (void)d;
    if (out) *out = NULL;
    return false;
}

int ca_aether_discovery_announce(ca_aether_discovery_t *d,
                                 const ca_peer_info_t *local_info) {
    if (!d || !local_info) return -1;
    ca_peer_info_t *copy = ca_peer_info_copy(local_info);
    if (!copy) return -1;
    ca_peer_info_destroy(d->last_announced);
    d->last_announced = copy;
    return 0;
}

const ca_peer_info_t *ca_aether_discovery_last_announced(
    const ca_aether_discovery_t *d) {
    return d ? d->last_announced : NULL;
}

/* ===========================================================================
 * AetherSyncChannel — sequence map keyed by (ownerId, domainKey)
 * =========================================================================== */

typedef struct {
    char   *owner_id;   /* owned */
    char   *domain_key; /* owned */
    int64_t sequence;
} seq_entry_t;

struct ca_aether_sync_channel {
    ca_aether_context_t context;  /* borrowed */
    seq_entry_t        *seqs;      /* owned array */
    size_t              seq_count;
    size_t              seq_cap;
};

ca_aether_sync_channel_t *ca_aether_sync_channel_create(
    ca_aether_context_t context) {
    ca_aether_sync_channel_t *s =
        (ca_aether_sync_channel_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->context = context;
    return s;
}

void ca_aether_sync_channel_destroy(ca_aether_sync_channel_t *s) {
    if (!s) return;
    for (size_t i = 0; i < s->seq_count; ++i) {
        free(s->seqs[i].owner_id);
        free(s->seqs[i].domain_key);
    }
    free(s->seqs);
    free(s);
}

static ptrdiff_t seq_index(const ca_aether_sync_channel_t *s,
                           const char *owner_id, const char *domain_key) {
    for (size_t i = 0; i < s->seq_count; ++i)
        if (strcmp(s->seqs[i].owner_id, owner_id) == 0 &&
            strcmp(s->seqs[i].domain_key, domain_key) == 0)
            return (ptrdiff_t)i;
    return -1;
}

int ca_aether_sync_channel_push_delta(ca_aether_sync_channel_t *s,
                                      const ca_net_sync_delta_t *delta) {
    /* Serialise + hand to aether-protocol DTN — completed no-op bridge. */
    if (!s || !delta) return -1;
    return 0;
}

bool ca_aether_sync_channel_receive_next(ca_aether_sync_channel_t *s,
                                         const char *owner_id,
                                         ca_net_sync_delta_t **out) {
    /* ReceiveDeltasAsync yield break; — empty stream. */
    (void)s; (void)owner_id;
    if (out) *out = NULL;
    return false;
}

int64_t ca_aether_sync_channel_last_sequence(
    const ca_aether_sync_channel_t *s, const char *owner_id,
    const char *domain_key) {
    if (!s || !owner_id || !domain_key) return 0;
    ptrdiff_t idx = seq_index(s, owner_id, domain_key);
    return idx < 0 ? 0 : s->seqs[idx].sequence;
}

int ca_aether_sync_channel_set_sequence(ca_aether_sync_channel_t *s,
                                        const char *owner_id,
                                        const char *domain_key, int64_t seq) {
    if (!s || !owner_id || !domain_key) return -1;
    ptrdiff_t idx = seq_index(s, owner_id, domain_key);
    if (idx >= 0) { s->seqs[idx].sequence = seq; return 0; }
    if (s->seq_count == s->seq_cap) {
        size_t nc = s->seq_cap ? s->seq_cap * 2 : 4;
        seq_entry_t *ne = (seq_entry_t *)realloc(s->seqs, nc * sizeof(*ne));
        if (!ne) return -1;
        s->seqs = ne;
        s->seq_cap = nc;
    }
    seq_entry_t *e = &s->seqs[s->seq_count];
    e->owner_id = dup_or_empty(owner_id);
    e->domain_key = dup_or_empty(domain_key);
    if (!e->owner_id || !e->domain_key) {
        free(e->owner_id); free(e->domain_key);
        return -1;
    }
    e->sequence = seq;
    s->seq_count++;
    return 0;
}
