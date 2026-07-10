/*
 * networking.c — CircleAI.Networking core abstraction (C11 port).
 *
 * Enums, immutable records (NetworkPayload / NetworkContext / PeerInfo), the
 * INetworkPolicy vtable + DefaultNetworkPolicy + NetworkPolicyBuilder, and
 * working, deterministic, in-memory implementations of every stateful interface
 * (INetworkTransport / IMeshNetwork / IMessageChannel / IConnectivityMonitor /
 * ITransportSelector). Ported 1:1 from CircleAI.Networking.
 *
 * Pure C11 + libc (+ ca_uuid_v4 from security.c for Guid "N").
 */

#include "circle_ai/networking.h"
#include "circle_ai/security.h"  /* ca_uuid_v4 / CA_UUID_STR_LEN */

#include <stdlib.h>
#include <string.h>

/* ---------------------------------------------------------------------------
 * small string / array helpers (mirror aether.c)
 * --------------------------------------------------------------------------- */

static char *dup_or_null(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* strdup that turns NULL into an empty owned string (never NULL unless OOM).
 * Mirrors C# string fields that are non-null. */
static char *dup_or_empty(const char *s) {
    return dup_or_null(s ? s : "");
}

/* Deep-copy a byte buffer. len==0 yields a 1-byte allocation (never NULL) so an
 * empty payload still round-trips with a distinct owned pointer. NULL on OOM. */
static uint8_t *dup_bytes(const uint8_t *src, size_t len) {
    uint8_t *p = (uint8_t *)malloc(len ? len : 1);
    if (!p) return NULL;
    if (len && src) memcpy(p, src, len);
    return p;
}

/* Deep-copy an array of TransportKind. count==0 -> NULL. NULL on OOM (*ok=false). */
static ca_transport_kind_t *dup_kind_array(const ca_transport_kind_t *src,
                                           size_t count, bool *ok) {
    if (ok) *ok = true;
    if (count == 0) return NULL;
    ca_transport_kind_t *out =
        (ca_transport_kind_t *)malloc(count * sizeof(*out));
    if (!out) { if (ok) *ok = false; return NULL; }
    if (src) memcpy(out, src, count * sizeof(*out));
    else memset(out, 0, count * sizeof(*out));
    return out;
}

/* ===========================================================================
 * Guid "N" — 32 lowercase hex, no dashes (v4 GUID, dashes stripped)
 * =========================================================================== */

char *ca_net_new_guid_n(char out[33]) {
    char dashed[CA_UUID_STR_LEN];
    ca_uuid_v4(dashed);
    size_t j = 0;
    for (size_t i = 0; dashed[i] && j < 32; ++i)
        if (dashed[i] != '-') out[j++] = dashed[i];
    out[j] = '\0';
    return out;
}

/* ===========================================================================
 * NetworkPayload
 * =========================================================================== */

static void free_net_metadata(ca_net_metadata_pair_t *m, size_t n) {
    if (!m) return;
    for (size_t i = 0; i < n; ++i) { free(m[i].key); free(m[i].value); }
    free(m);
}

ca_network_payload_t *ca_network_payload_new(
    const char *id, const char *source_id, const char *destination_id,
    const uint8_t *data, size_t data_len, ca_message_priority_t priority,
    bool has_ttl, int64_t ttl_ms, const char *content_type,
    const ca_net_metadata_pair_t *metadata, size_t metadata_count,
    int64_t created_at_ms) {
    ca_network_payload_t *p =
        (ca_network_payload_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;

    p->id = dup_or_empty(id);
    p->content_type = dup_or_empty(content_type);
    if (!p->id || !p->content_type) goto fail;

    if (source_id) {
        p->source_id = dup_or_null(source_id);
        if (!p->source_id) goto fail;
    }
    if (destination_id) {
        p->destination_id = dup_or_null(destination_id);
        if (!p->destination_id) goto fail;
    }

    p->data = dup_bytes(data, data_len);
    if (!p->data) goto fail;
    p->data_len = data_len;

    p->priority = priority;
    p->has_ttl = has_ttl;
    p->ttl_ms = ttl_ms;
    p->created_at_ms = created_at_ms;

    if (metadata_count > 0) {
        p->metadata = (ca_net_metadata_pair_t *)calloc(
            metadata_count, sizeof(*p->metadata));
        if (!p->metadata) goto fail;
        for (size_t i = 0; i < metadata_count; ++i) {
            p->metadata[i].key = dup_or_empty(metadata ? metadata[i].key : NULL);
            p->metadata[i].value =
                dup_or_empty(metadata ? metadata[i].value : NULL);
            if (!p->metadata[i].key || !p->metadata[i].value) {
                p->metadata_count = i + 1; /* free what we built */
                goto fail;
            }
        }
        p->metadata_count = metadata_count;
    }
    return p;
fail:
    ca_network_payload_destroy(p);
    return NULL;
}

ca_network_payload_t *ca_network_payload_create(
    const uint8_t *data, size_t data_len, const char *destination_id,
    ca_message_priority_t priority, const char *content_type,
    bool has_ttl, int64_t ttl_ms, int64_t created_at_ms, const char *id) {
    char gen[33];
    const char *use_id = id;
    if (!use_id) use_id = ca_net_new_guid_n(gen);
    const char *ct = content_type ? content_type : "application/octet-stream";
    /* SourceId is null; metadata empty. */
    return ca_network_payload_new(use_id, NULL, destination_id, data, data_len,
                                  priority, has_ttl, ttl_ms, ct, NULL, 0,
                                  created_at_ms);
}

void ca_network_payload_destroy(ca_network_payload_t *p) {
    if (!p) return;
    free(p->id);
    free(p->source_id);
    free(p->destination_id);
    free(p->data);
    free(p->content_type);
    free_net_metadata(p->metadata, p->metadata_count);
    free(p);
}

ca_network_payload_t *ca_network_payload_copy(const ca_network_payload_t *p) {
    if (!p) return NULL;
    return ca_network_payload_new(
        p->id, p->source_id, p->destination_id, p->data, p->data_len,
        p->priority, p->has_ttl, p->ttl_ms, p->content_type, p->metadata,
        p->metadata_count, p->created_at_ms);
}

const char *ca_network_payload_metadata(const ca_network_payload_t *p,
                                        const char *key) {
    if (!p || !key) return NULL;
    for (size_t i = 0; i < p->metadata_count; ++i)
        if (strcmp(p->metadata[i].key, key) == 0) return p->metadata[i].value;
    return NULL;
}

/* ===========================================================================
 * NetworkContext
 * =========================================================================== */

ca_network_context_t *ca_network_context_new(
    ca_connectivity_state_t state, ca_transport_kind_t preferred_transport,
    const ca_transport_kind_t *available_transports, size_t available_count,
    bool has_signal_strength, int signal_strength_dbm,
    bool has_bandwidth, int64_t estimated_bandwidth_bps,
    bool has_latency, int64_t latency_ms,
    int nearby_peer_count, int64_t snapshot_at_ms) {
    ca_network_context_t *c =
        (ca_network_context_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    bool ok = true;
    c->available_transports =
        dup_kind_array(available_transports, available_count, &ok);
    if (!ok) { free(c); return NULL; }
    c->available_count = available_count;
    c->state = state;
    c->preferred_transport = preferred_transport;
    c->has_signal_strength = has_signal_strength;
    c->signal_strength_dbm = signal_strength_dbm;
    c->has_bandwidth = has_bandwidth;
    c->estimated_bandwidth_bps = estimated_bandwidth_bps;
    c->has_latency = has_latency;
    c->latency_ms = latency_ms;
    c->nearby_peer_count = nearby_peer_count;
    c->snapshot_at_ms = snapshot_at_ms;
    return c;
}

void ca_network_context_destroy(ca_network_context_t *c) {
    if (!c) return;
    free(c->available_transports);
    free(c);
}

ca_network_context_t *ca_network_context_copy(const ca_network_context_t *c) {
    if (!c) return NULL;
    return ca_network_context_new(
        c->state, c->preferred_transport, c->available_transports,
        c->available_count, c->has_signal_strength, c->signal_strength_dbm,
        c->has_bandwidth, c->estimated_bandwidth_bps, c->has_latency,
        c->latency_ms, c->nearby_peer_count, c->snapshot_at_ms);
}

ca_network_context_t *ca_network_context_offline(int64_t now_ms) {
    return ca_network_context_new(
        CA_CONNECTIVITY_OFFLINE, CA_TRANSPORT_LOCAL_STORE, NULL, 0,
        false, 0, false, 0, false, 0, 0, now_ms);
}

bool ca_network_context_supports(const ca_network_context_t *c,
                                 ca_transport_kind_t t) {
    if (!c) return false;
    for (size_t i = 0; i < c->available_count; ++i)
        if (c->available_transports[i] == t) return true;
    return false;
}

/* ===========================================================================
 * PeerInfo
 * =========================================================================== */

ca_peer_info_t *ca_peer_info_new(
    const char *node_id, const char *display_name,
    const ca_transport_kind_t *supported_transports, size_t supported_count,
    ca_peer_role_t role, bool has_signal_strength, int signal_strength_dbm,
    int64_t last_seen_ms) {
    ca_peer_info_t *p = (ca_peer_info_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->node_id = dup_or_empty(node_id);
    if (!p->node_id) { free(p); return NULL; }
    if (display_name) {
        p->display_name = dup_or_null(display_name);
        if (!p->display_name) { free(p->node_id); free(p); return NULL; }
    }
    bool ok = true;
    p->supported_transports =
        dup_kind_array(supported_transports, supported_count, &ok);
    if (!ok) {
        free(p->node_id);
        free(p->display_name);
        free(p);
        return NULL;
    }
    p->supported_count = supported_count;
    p->role = role;
    p->has_signal_strength = has_signal_strength;
    p->signal_strength_dbm = signal_strength_dbm;
    p->last_seen_ms = last_seen_ms;
    return p;
}

void ca_peer_info_destroy(ca_peer_info_t *p) {
    if (!p) return;
    free(p->node_id);
    free(p->display_name);
    free(p->supported_transports);
    free(p);
}

ca_peer_info_t *ca_peer_info_copy(const ca_peer_info_t *p) {
    if (!p) return NULL;
    return ca_peer_info_new(p->node_id, p->display_name, p->supported_transports,
                            p->supported_count, p->role, p->has_signal_strength,
                            p->signal_strength_dbm, p->last_seen_ms);
}

/* ===========================================================================
 * DefaultNetworkPolicy — permissive singleton
 * =========================================================================== */

static bool defpol_permits(void *self, ca_transport_kind_t t,
                           const ca_network_payload_t *payload) {
    (void)self; (void)t; (void)payload;
    return true;
}
static bool defpol_force(void *self, ca_transport_kind_t *out) {
    (void)self; (void)out;
    return false; /* ForceTransport => null */
}
static bool defpol_mesh_first(void *self)          { (void)self; return false; }
static bool defpol_queue_enabled(void *self)       { (void)self; return true; }
static bool defpol_allow_cloud(void *self)         { (void)self; return true; }

ca_network_policy_t ca_default_network_policy(void) {
    ca_network_policy_t v;
    v.self = NULL;
    v.permits = defpol_permits;
    v.force_transport = defpol_force;
    v.mesh_first = defpol_mesh_first;
    v.offline_queue_enabled = defpol_queue_enabled;
    v.allow_cloud_transports = defpol_allow_cloud;
    return v;
}

/* ===========================================================================
 * NetworkPolicyBuilder + built Policy impl
 * =========================================================================== */

/* There are 12 TransportKind values; a fixed bool set indexed by kind models the
 * builder's HashSet<TransportKind> without a hashtable. */
#define CA_TK_COUNT 12

struct ca_network_policy_builder {
    bool allowed[CA_TK_COUNT];
    bool has_any_allowed;   /* mirrors "_allowed.Count > 0 ? ... : null" */
    bool mesh_first;
    bool no_cloud;
    bool queue_enabled;     /* default true */
    bool has_force;
    ca_transport_kind_t force;
};

struct ca_network_policy_impl {
    bool allowed[CA_TK_COUNT];
    bool has_allow_set;     /* false => allowed is null (permit-all past no-cloud) */
    bool mesh_first;
    bool no_cloud;
    bool queue_enabled;
    bool has_force;
    ca_transport_kind_t force;
};

ca_network_policy_builder_t *ca_network_policy_builder_create(void) {
    ca_network_policy_builder_t *b =
        (ca_network_policy_builder_t *)calloc(1, sizeof(*b));
    if (!b) return NULL;
    b->queue_enabled = true; /* _queueEnabled = true */
    return b;
}

void ca_network_policy_builder_destroy(ca_network_policy_builder_t *b) {
    free(b);
}

ca_network_policy_builder_t *ca_network_policy_builder_mesh_first(
    ca_network_policy_builder_t *b) {
    if (b) b->mesh_first = true;
    return b;
}
ca_network_policy_builder_t *ca_network_policy_builder_no_cloud(
    ca_network_policy_builder_t *b) {
    if (b) b->no_cloud = true;
    return b;
}
ca_network_policy_builder_t *ca_network_policy_builder_disable_queue(
    ca_network_policy_builder_t *b) {
    if (b) b->queue_enabled = false;
    return b;
}
ca_network_policy_builder_t *ca_network_policy_builder_force(
    ca_network_policy_builder_t *b, ca_transport_kind_t t) {
    if (b) { b->has_force = true; b->force = t; }
    return b;
}
ca_network_policy_builder_t *ca_network_policy_builder_allow(
    ca_network_policy_builder_t *b, const ca_transport_kind_t *kinds,
    size_t count) {
    if (!b || !kinds) return b;
    for (size_t i = 0; i < count; ++i) {
        int k = (int)kinds[i];
        if (k >= 0 && k < CA_TK_COUNT) {
            b->allowed[k] = true;
            b->has_any_allowed = true;
        }
    }
    return b;
}

static bool is_cloud_kind(ca_transport_kind_t t) {
    return t == CA_TRANSPORT_HTTP || t == CA_TRANSPORT_WEBSOCKET ||
           t == CA_TRANSPORT_GRPC || t == CA_TRANSPORT_MQTT;
}

ca_network_policy_impl_t *ca_network_policy_builder_build(
    const ca_network_policy_builder_t *b) {
    if (!b) return NULL;
    ca_network_policy_impl_t *p =
        (ca_network_policy_impl_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->has_allow_set = b->has_any_allowed; /* allowed non-null iff Count>0 */
    if (p->has_allow_set)
        memcpy(p->allowed, b->allowed, sizeof(p->allowed));
    p->mesh_first = b->mesh_first;
    p->no_cloud = b->no_cloud;
    p->queue_enabled = b->queue_enabled;
    p->has_force = b->has_force;
    p->force = b->force;
    return p;
}

void ca_network_policy_impl_destroy(ca_network_policy_impl_t *p) { free(p); }

static bool polimpl_permits(void *self, ca_transport_kind_t t,
                            const ca_network_payload_t *payload) {
    (void)payload;
    ca_network_policy_impl_t *p = (ca_network_policy_impl_t *)self;
    /* if (noCloud && t is cloud) return false; */
    if (p->no_cloud && is_cloud_kind(t)) return false;
    /* return allowed is null || allowed.Contains(t); */
    if (!p->has_allow_set) return true;
    int k = (int)t;
    if (k < 0 || k >= CA_TK_COUNT) return false;
    return p->allowed[k];
}
static bool polimpl_force(void *self, ca_transport_kind_t *out) {
    ca_network_policy_impl_t *p = (ca_network_policy_impl_t *)self;
    if (!p->has_force) return false;
    if (out) *out = p->force;
    return true;
}
static bool polimpl_mesh_first(void *self) {
    return ((ca_network_policy_impl_t *)self)->mesh_first;
}
static bool polimpl_queue_enabled(void *self) {
    return ((ca_network_policy_impl_t *)self)->queue_enabled;
}
static bool polimpl_allow_cloud(void *self) {
    return !((ca_network_policy_impl_t *)self)->no_cloud;
}

ca_network_policy_t ca_network_policy_impl_as_policy(
    ca_network_policy_impl_t *p) {
    ca_network_policy_t v;
    v.self = p;
    v.permits = polimpl_permits;
    v.force_transport = polimpl_force;
    v.mesh_first = polimpl_mesh_first;
    v.offline_queue_enabled = polimpl_queue_enabled;
    v.allow_cloud_transports = polimpl_allow_cloud;
    return v;
}

/* ===========================================================================
 * Unbounded FIFO of NetworkPayload* (shared by loopback transport)
 * =========================================================================== */

typedef struct {
    ca_network_payload_t **items; /* owned array of owned payloads */
    size_t head;                  /* next to drain */
    size_t count;                 /* live items = count - head */
    size_t cap;
} payload_fifo_t;

static bool fifo_push(payload_fifo_t *q, ca_network_payload_t *owned) {
    if (q->count == q->cap) {
        /* Compact if the dead prefix is large, else grow. */
        if (q->head > 0) {
            size_t live = q->count - q->head;
            memmove(q->items, q->items + q->head,
                    live * sizeof(*q->items));
            q->count = live;
            q->head = 0;
        }
        if (q->count == q->cap) {
            size_t nc = q->cap ? q->cap * 2 : 4;
            ca_network_payload_t **ni = (ca_network_payload_t **)realloc(
                q->items, nc * sizeof(*ni));
            if (!ni) return false;
            q->items = ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = owned;
    return true;
}

static ca_network_payload_t *fifo_pop(payload_fifo_t *q) {
    if (q->head >= q->count) return NULL;
    ca_network_payload_t *p = q->items[q->head];
    q->items[q->head++] = NULL;
    if (q->head == q->count) { q->head = 0; q->count = 0; } /* fully drained */
    return p;
}

static void fifo_free(payload_fifo_t *q) {
    for (size_t i = q->head; i < q->count; ++i)
        ca_network_payload_destroy(q->items[i]);
    free(q->items);
    q->items = NULL;
    q->head = q->count = q->cap = 0;
}

/* ===========================================================================
 * Loopback INetworkTransport
 * =========================================================================== */

struct ca_loopback_transport {
    ca_transport_kind_t kind;
    bool                started;
    payload_fifo_t      queue;
};

ca_loopback_transport_t *ca_loopback_transport_create(ca_transport_kind_t kind) {
    ca_loopback_transport_t *t =
        (ca_loopback_transport_t *)calloc(1, sizeof(*t));
    if (!t) return NULL;
    t->kind = kind;
    t->started = false;
    return t;
}

void ca_loopback_transport_destroy(ca_loopback_transport_t *t) {
    if (!t) return;
    fifo_free(&t->queue);
    free(t);
}

static ca_transport_kind_t lb_kind(void *self) {
    return ((ca_loopback_transport_t *)self)->kind;
}
static bool lb_is_available(void *self) {
    return ((ca_loopback_transport_t *)self)->started;
}
static int lb_start(void *self) {
    ((ca_loopback_transport_t *)self)->started = true;
    return 0;
}
static int lb_stop(void *self) {
    ((ca_loopback_transport_t *)self)->started = false;
    return 0;
}
static int lb_send(void *self, const ca_network_payload_t *payload) {
    ca_loopback_transport_t *t = (ca_loopback_transport_t *)self;
    if (!payload) return -1;
    if (!t->started) return -1; /* transport must be started to send */
    ca_network_payload_t *copy = ca_network_payload_copy(payload);
    if (!copy) return -1;
    if (!fifo_push(&t->queue, copy)) {
        ca_network_payload_destroy(copy);
        return -1;
    }
    return 0;
}
static bool lb_receive_next(void *self, ca_network_payload_t **out) {
    ca_loopback_transport_t *t = (ca_loopback_transport_t *)self;
    if (!out) return false;
    if (!t->started) return false;
    ca_network_payload_t *p = fifo_pop(&t->queue);
    if (!p) return false;
    *out = p;
    return true;
}

ca_network_transport_t ca_loopback_transport_as_transport(
    ca_loopback_transport_t *t) {
    ca_network_transport_t v;
    v.self = t;
    v.kind = lb_kind;
    v.is_available = lb_is_available;
    v.start = lb_start;
    v.stop = lb_stop;
    v.send = lb_send;
    v.receive_next = lb_receive_next;
    return v;
}

size_t ca_loopback_transport_pending(const ca_loopback_transport_t *t) {
    return t ? (t->queue.count - t->queue.head) : 0;
}
bool ca_loopback_transport_is_started(const ca_loopback_transport_t *t) {
    return t && t->started;
}

/* ===========================================================================
 * In-memory IMeshNetwork
 * =========================================================================== */

struct ca_mem_mesh {
    char                 *local_id;    /* owned */
    char                **peers;       /* owned array of owned ids */
    size_t                peer_count;
    size_t                peer_cap;
    ca_network_context_t *health;      /* owned (may be NULL until set) */
};

ca_mem_mesh_t *ca_mem_mesh_create(const char *local_node_id) {
    ca_mem_mesh_t *m = (ca_mem_mesh_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    m->local_id = dup_or_empty(local_node_id);
    if (!m->local_id) { free(m); return NULL; }
    return m;
}

void ca_mem_mesh_destroy(ca_mem_mesh_t *m) {
    if (!m) return;
    free(m->local_id);
    for (size_t i = 0; i < m->peer_count; ++i) free(m->peers[i]);
    free(m->peers);
    ca_network_context_destroy(m->health);
    free(m);
}

static bool mesh_has_peer(const ca_mem_mesh_t *m, const char *peer_id) {
    for (size_t i = 0; i < m->peer_count; ++i)
        if (strcmp(m->peers[i], peer_id) == 0) return true;
    return false;
}

int ca_mem_mesh_add_peer(ca_mem_mesh_t *m, const char *peer_id) {
    if (!m || !peer_id) return -1;
    if (mesh_has_peer(m, peer_id)) return 0; /* set semantics */
    if (m->peer_count == m->peer_cap) {
        size_t nc = m->peer_cap ? m->peer_cap * 2 : 4;
        char **np = (char **)realloc(m->peers, nc * sizeof(*np));
        if (!np) return -1;
        m->peers = np;
        m->peer_cap = nc;
    }
    char *id = dup_or_null(peer_id);
    if (!id) return -1;
    m->peers[m->peer_count++] = id;
    return 0;
}

void ca_mem_mesh_remove_peer(ca_mem_mesh_t *m, const char *peer_id) {
    if (!m || !peer_id) return;
    for (size_t i = 0; i < m->peer_count; ++i) {
        if (strcmp(m->peers[i], peer_id) == 0) {
            free(m->peers[i]);
            m->peers[i] = m->peers[--m->peer_count];
            return;
        }
    }
}

int ca_mem_mesh_set_health(ca_mem_mesh_t *m,
                           const ca_network_context_t *health) {
    if (!m) return -1;
    ca_network_context_t *copy = NULL;
    if (health) {
        copy = ca_network_context_copy(health);
        if (!copy) return -1;
    }
    ca_network_context_destroy(m->health);
    m->health = copy;
    return 0;
}

static const char *mesh_local_id(void *self) {
    return ((ca_mem_mesh_t *)self)->local_id;
}
static int mesh_peer_ids(void *self, char ***out, size_t *count) {
    ca_mem_mesh_t *m = (ca_mem_mesh_t *)self;
    if (!out || !count) return -1;
    if (m->peer_count == 0) { *out = NULL; *count = 0; return 0; }
    char **arr = (char **)calloc(m->peer_count, sizeof(*arr));
    if (!arr) { *out = NULL; *count = SIZE_MAX; return -1; }
    for (size_t i = 0; i < m->peer_count; ++i) {
        arr[i] = dup_or_null(m->peers[i]);
        if (!arr[i]) {
            for (size_t j = 0; j < i; ++j) free(arr[j]);
            free(arr);
            *out = NULL; *count = SIZE_MAX;
            return -1;
        }
    }
    *out = arr;
    *count = m->peer_count;
    return 0;
}
static int mesh_health(void *self, ca_network_context_t **out) {
    ca_mem_mesh_t *m = (ca_mem_mesh_t *)self;
    if (!out) return -1;
    /* GetMeshHealthAsync — default to Offline when none set. */
    if (m->health) {
        ca_network_context_t *copy = ca_network_context_copy(m->health);
        if (!copy) return -1;
        *out = copy;
    } else {
        ca_network_context_t *off = ca_network_context_offline(0);
        if (!off) return -1;
        *out = off;
    }
    return 0;
}

ca_mesh_network_t ca_mem_mesh_as_mesh(ca_mem_mesh_t *m) {
    ca_mesh_network_t v;
    v.self = m;
    v.local_node_id = mesh_local_id;
    v.peer_ids = mesh_peer_ids;
    v.mesh_health = mesh_health;
    return v;
}

/* ===========================================================================
 * In-memory IMessageChannel (unbounded pub/sub with backlog retention)
 * =========================================================================== */

/* A per-subscription FIFO of channel messages. */
typedef struct {
    ca_channel_message_t *items; /* owned array; each item owns its fields */
    size_t head;
    size_t count;
    size_t cap;
} chanmsg_fifo_t;

static void chanmsg_move_destroy(ca_channel_message_t *m) {
    if (!m) return;
    free(m->destination_id);
    free(m->data);
    free(m->content_type);
    m->destination_id = NULL;
    m->data = NULL;
    m->content_type = NULL;
    m->len = 0;
}

static bool chanmsg_fifo_push(chanmsg_fifo_t *q, ca_channel_message_t item) {
    if (q->count == q->cap) {
        if (q->head > 0) {
            size_t live = q->count - q->head;
            memmove(q->items, q->items + q->head, live * sizeof(*q->items));
            q->count = live;
            q->head = 0;
        }
        if (q->count == q->cap) {
            size_t nc = q->cap ? q->cap * 2 : 4;
            ca_channel_message_t *ni = (ca_channel_message_t *)realloc(
                q->items, nc * sizeof(*ni));
            if (!ni) return false;
            q->items = ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = item;
    return true;
}

static bool chanmsg_fifo_pop(chanmsg_fifo_t *q, ca_channel_message_t *out) {
    if (q->head >= q->count) return false;
    *out = q->items[q->head];
    memset(&q->items[q->head], 0, sizeof(q->items[q->head]));
    q->head++;
    if (q->head == q->count) { q->head = 0; q->count = 0; }
    return true;
}

static void chanmsg_fifo_free(chanmsg_fifo_t *q) {
    for (size_t i = q->head; i < q->count; ++i)
        chanmsg_move_destroy(&q->items[i]);
    free(q->items);
    q->items = NULL;
    q->head = q->count = q->cap = 0;
}

struct ca_mem_channel_sub {
    ca_mem_channel_t *owner;
    chanmsg_fifo_t    queue;
    bool              live;
};

struct ca_mem_channel {
    ca_mem_channel_sub_t **subs; /* owned array of owned subs */
    size_t                 count;
    size_t                 cap;
    /* Backlog of every message ever sent, so a late subscriber still sees them
     * (unbounded Channel retains writes until read). */
    chanmsg_fifo_t         backlog;
};

ca_mem_channel_t *ca_mem_channel_create(void) {
    return (ca_mem_channel_t *)calloc(1, sizeof(ca_mem_channel_t));
}

void ca_mem_channel_destroy(ca_mem_channel_t *c) {
    if (!c) return;
    for (size_t i = 0; i < c->count; ++i) {
        chanmsg_fifo_free(&c->subs[i]->queue);
        free(c->subs[i]);
    }
    free(c->subs);
    chanmsg_fifo_free(&c->backlog);
    free(c);
}

/* Build an owned channel message (deep copy of the caller's bytes). */
static bool chanmsg_make(ca_channel_message_t *out, const char *destination_id,
                         const uint8_t *data, size_t len,
                         const char *content_type) {
    memset(out, 0, sizeof(*out));
    if (destination_id) {
        out->destination_id = dup_or_null(destination_id);
        if (!out->destination_id) return false;
    }
    out->data = dup_bytes(data, len);
    if (!out->data) { free(out->destination_id); return false; }
    out->len = len;
    out->content_type = dup_or_empty(content_type);
    if (!out->content_type) {
        free(out->destination_id);
        free(out->data);
        memset(out, 0, sizeof(*out));
        return false;
    }
    return true;
}

static int chan_send(void *self, const char *destination_id,
                     const uint8_t *data, size_t len, const char *content_type) {
    ca_mem_channel_t *c = (ca_mem_channel_t *)self;
    if (!c) return -1;

    /* Retain in the backlog first (deep copy). */
    ca_channel_message_t backlog_item;
    if (!chanmsg_make(&backlog_item, destination_id, data, len, content_type))
        return -1;
    if (!chanmsg_fifo_push(&c->backlog, backlog_item)) {
        chanmsg_move_destroy(&backlog_item);
        return -1;
    }

    /* Snapshot the live subscription pointers, then fan out. A subscriber's
     * queue push allocates its own deep copy; a failed push is skipped for that
     * subscriber (best-effort fan-out) but the send still succeeds because the
     * backlog holds the canonical copy. */
    for (size_t i = 0; i < c->count; ++i) {
        ca_mem_channel_sub_t *s = c->subs[i];
        if (!s->live) continue;
        ca_channel_message_t item;
        if (!chanmsg_make(&item, destination_id, data, len, content_type))
            continue;
        if (!chanmsg_fifo_push(&s->queue, item))
            chanmsg_move_destroy(&item);
    }
    return 0;
}

ca_message_channel_t ca_mem_channel_as_channel(ca_mem_channel_t *c) {
    ca_message_channel_t v;
    v.self = c;
    v.send = chan_send;
    return v;
}

ca_mem_channel_sub_t *ca_mem_channel_subscribe(ca_mem_channel_t *c) {
    if (!c) return NULL;
    if (c->count == c->cap) {
        size_t nc = c->cap ? c->cap * 2 : 4;
        ca_mem_channel_sub_t **ns = (ca_mem_channel_sub_t **)realloc(
            c->subs, nc * sizeof(*ns));
        if (!ns) return NULL;
        c->subs = ns;
        c->cap = nc;
    }
    ca_mem_channel_sub_t *s =
        (ca_mem_channel_sub_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->owner = c;
    s->live = true;

    /* Deliver the entire backlog to the new subscriber so a message published
     * BEFORE this subscribe is not lost (unbounded-channel retention). */
    for (size_t i = c->backlog.head; i < c->backlog.count; ++i) {
        ca_channel_message_t *src = &c->backlog.items[i];
        ca_channel_message_t item;
        if (!chanmsg_make(&item, src->destination_id, src->data, src->len,
                          src->content_type)) {
            chanmsg_fifo_free(&s->queue);
            free(s);
            return NULL;
        }
        if (!chanmsg_fifo_push(&s->queue, item)) {
            chanmsg_move_destroy(&item);
            chanmsg_fifo_free(&s->queue);
            free(s);
            return NULL;
        }
    }

    c->subs[c->count++] = s;
    return s;
}

void ca_mem_channel_unsubscribe(ca_mem_channel_t *c, ca_mem_channel_sub_t *sub) {
    if (!c || !sub) return;
    for (size_t i = 0; i < c->count; ++i) {
        if (c->subs[i] == sub) {
            chanmsg_fifo_free(&sub->queue);
            free(sub);
            c->subs[i] = c->subs[--c->count];
            return;
        }
    }
}

bool ca_mem_channel_receive_next(ca_mem_channel_sub_t *sub,
                                 ca_channel_message_t *out) {
    if (!sub || !out) return false;
    return chanmsg_fifo_pop(&sub->queue, out);
}

size_t ca_mem_channel_sub_pending(const ca_mem_channel_sub_t *sub) {
    return sub ? (sub->queue.count - sub->queue.head) : 0;
}

void ca_channel_message_destroy(ca_channel_message_t *m) {
    chanmsg_move_destroy(m);
}

/* ===========================================================================
 * In-memory IConnectivityMonitor (WatchAsync fan-out)
 * =========================================================================== */

/* A per-watcher FIFO of NetworkContext*. */
typedef struct {
    ca_network_context_t **items; /* owned array of owned contexts */
    size_t head;
    size_t count;
    size_t cap;
} ctx_fifo_t;

static bool ctx_fifo_push(ctx_fifo_t *q, ca_network_context_t *owned) {
    if (q->count == q->cap) {
        if (q->head > 0) {
            size_t live = q->count - q->head;
            memmove(q->items, q->items + q->head, live * sizeof(*q->items));
            q->count = live;
            q->head = 0;
        }
        if (q->count == q->cap) {
            size_t nc = q->cap ? q->cap * 2 : 4;
            ca_network_context_t **ni = (ca_network_context_t **)realloc(
                q->items, nc * sizeof(*ni));
            if (!ni) return false;
            q->items = ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = owned;
    return true;
}

static ca_network_context_t *ctx_fifo_pop(ctx_fifo_t *q) {
    if (q->head >= q->count) return NULL;
    ca_network_context_t *c = q->items[q->head];
    q->items[q->head++] = NULL;
    if (q->head == q->count) { q->head = 0; q->count = 0; }
    return c;
}

static void ctx_fifo_free(ctx_fifo_t *q) {
    for (size_t i = q->head; i < q->count; ++i)
        ca_network_context_destroy(q->items[i]);
    free(q->items);
    q->items = NULL;
    q->head = q->count = q->cap = 0;
}

struct ca_mem_connectivity_sub {
    ctx_fifo_t queue;
    bool       live;
};

struct ca_mem_connectivity {
    ca_network_context_t          *current; /* owned; latest snapshot */
    ca_mem_connectivity_sub_t    **subs;    /* owned array of owned watchers */
    size_t                         count;
    size_t                         cap;
};

ca_mem_connectivity_t *ca_mem_connectivity_create(
    const ca_network_context_t *initial) {
    if (!initial) return NULL;
    ca_mem_connectivity_t *m =
        (ca_mem_connectivity_t *)calloc(1, sizeof(*m));
    if (!m) return NULL;
    m->current = ca_network_context_copy(initial);
    if (!m->current) { free(m); return NULL; }
    return m;
}

void ca_mem_connectivity_destroy(ca_mem_connectivity_t *m) {
    if (!m) return;
    ca_network_context_destroy(m->current);
    for (size_t i = 0; i < m->count; ++i) {
        ctx_fifo_free(&m->subs[i]->queue);
        free(m->subs[i]);
    }
    free(m->subs);
    free(m);
}

static ca_connectivity_state_t conn_current_state(void *self) {
    ca_mem_connectivity_t *m = (ca_mem_connectivity_t *)self;
    return m->current->state;
}
static int conn_snapshot(void *self, ca_network_context_t **out) {
    ca_mem_connectivity_t *m = (ca_mem_connectivity_t *)self;
    if (!out) return -1;
    ca_network_context_t *copy = ca_network_context_copy(m->current);
    if (!copy) return -1;
    *out = copy;
    return 0;
}

ca_connectivity_monitor_t ca_mem_connectivity_as_monitor(
    ca_mem_connectivity_t *m) {
    ca_connectivity_monitor_t v;
    v.self = m;
    v.current_state = conn_current_state;
    v.snapshot = conn_snapshot;
    return v;
}

int ca_mem_connectivity_push(ca_mem_connectivity_t *m,
                             const ca_network_context_t *ctx) {
    if (!m || !ctx) return -1;
    ca_network_context_t *new_current = ca_network_context_copy(ctx);
    if (!new_current) return -1;
    ca_network_context_destroy(m->current);
    m->current = new_current;

    /* Fan out a fresh copy to every live watcher. A per-watcher OOM is skipped
     * (best effort) but the current snapshot is already updated. */
    for (size_t i = 0; i < m->count; ++i) {
        ca_mem_connectivity_sub_t *s = m->subs[i];
        if (!s->live) continue;
        ca_network_context_t *copy = ca_network_context_copy(ctx);
        if (!copy) continue;
        if (!ctx_fifo_push(&s->queue, copy))
            ca_network_context_destroy(copy);
    }
    return 0;
}

ca_mem_connectivity_sub_t *ca_mem_connectivity_watch(
    ca_mem_connectivity_t *m) {
    if (!m) return NULL;
    if (m->count == m->cap) {
        size_t nc = m->cap ? m->cap * 2 : 4;
        ca_mem_connectivity_sub_t **ns =
            (ca_mem_connectivity_sub_t **)realloc(m->subs, nc * sizeof(*ns));
        if (!ns) return NULL;
        m->subs = ns;
        m->cap = nc;
    }
    ca_mem_connectivity_sub_t *s =
        (ca_mem_connectivity_sub_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->live = true;
    m->subs[m->count++] = s;
    return s;
}

void ca_mem_connectivity_unwatch(ca_mem_connectivity_t *m,
                                 ca_mem_connectivity_sub_t *sub) {
    if (!m || !sub) return;
    for (size_t i = 0; i < m->count; ++i) {
        if (m->subs[i] == sub) {
            ctx_fifo_free(&sub->queue);
            free(sub);
            m->subs[i] = m->subs[--m->count];
            return;
        }
    }
}

bool ca_mem_connectivity_watch_next(ca_mem_connectivity_sub_t *sub,
                                    ca_network_context_t **out) {
    if (!sub || !out) return false;
    ca_network_context_t *c = ctx_fifo_pop(&sub->queue);
    if (!c) return false;
    *out = c;
    return true;
}

/* ===========================================================================
 * Default ITransportSelector (documented cascade)
 * =========================================================================== */

struct ca_default_selector {
    ca_network_policy_t policy; /* borrowed vtable (self must outlive selector) */
};

ca_default_selector_t *ca_default_selector_create(ca_network_policy_t policy) {
    ca_default_selector_t *s =
        (ca_default_selector_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->policy = policy;
    return s;
}

void ca_default_selector_destroy(ca_default_selector_t *s) { free(s); }

/* Base cascade order (interface doc):
 *   gRPC, WebSocket, HTTP, MQTT, TCP, UDP, WiFi, Bluetooth, NearLink, Aether,
 *   DTN, LocalStore. */
static const ca_transport_kind_t CASCADE_BASE[] = {
    CA_TRANSPORT_GRPC, CA_TRANSPORT_WEBSOCKET, CA_TRANSPORT_HTTP,
    CA_TRANSPORT_MQTT, CA_TRANSPORT_TCP, CA_TRANSPORT_UDP, CA_TRANSPORT_WIFI,
    CA_TRANSPORT_BLUETOOTH, CA_TRANSPORT_NEARLINK, CA_TRANSPORT_AETHER,
    CA_TRANSPORT_DTN, CA_TRANSPORT_LOCAL_STORE
};
static const size_t CASCADE_BASE_N =
    sizeof(CASCADE_BASE) / sizeof(CASCADE_BASE[0]);

/* Mesh/local kinds hoisted to the front under MeshFirst (relative order kept). */
static bool is_mesh_kind(ca_transport_kind_t t) {
    return t == CA_TRANSPORT_WIFI || t == CA_TRANSPORT_BLUETOOTH ||
           t == CA_TRANSPORT_NEARLINK || t == CA_TRANSPORT_AETHER;
}
/* Kinds that need no live path (always eligible as terminal fallbacks). */
static bool is_offline_floor_kind(ca_transport_kind_t t) {
    return t == CA_TRANSPORT_DTN || t == CA_TRANSPORT_LOCAL_STORE;
}

/* Build the ordered cascade. Returns an owned array (NULL + *count=SIZE_MAX on
 * OOM; a legitimately empty cascade yields NULL + *count=0). */
static ca_transport_kind_t *build_cascade(ca_default_selector_t *s,
                                          const ca_network_payload_t *payload,
                                          const ca_network_context_t *context,
                                          size_t *count) {
    ca_network_policy_t *pol = &s->policy;

    ca_transport_kind_t buf[16];
    size_t n = 0;

    /* Availability gate: if the context lists NO available transports, treat all
     * live kinds as available (mirrors "no explicit list => don't over-filter").
     * Offline-floor kinds (DTN/LocalStore) are always availability-eligible. */
    bool have_avail_list = context && context->available_count > 0;

    /* ForceTransport short-circuit. */
    ca_transport_kind_t forced;
    if (pol->force_transport(pol->self, &forced)) {
        if (pol->permits(pol->self, forced, payload))
            buf[n++] = forced;
        /* Terminal offline floor still appended if the queue is enabled and the
         * forced kind wasn't already a floor kind. */
        if (pol->offline_queue_enabled(pol->self) &&
            (n == 0 || buf[0] != CA_TRANSPORT_LOCAL_STORE) &&
            pol->permits(pol->self, CA_TRANSPORT_LOCAL_STORE, payload))
            buf[n++] = CA_TRANSPORT_LOCAL_STORE;
    } else {
        for (size_t i = 0; i < CASCADE_BASE_N; ++i) {
            ca_transport_kind_t k = CASCADE_BASE[i];

            /* LocalStore is gated by OfflineQueueEnabled. */
            if (k == CA_TRANSPORT_LOCAL_STORE &&
                !pol->offline_queue_enabled(pol->self))
                continue;

            if (!pol->permits(pol->self, k, payload))
                continue;

            /* Availability filter for live kinds only. */
            if (have_avail_list && !is_offline_floor_kind(k) &&
                !ca_network_context_supports(context, k))
                continue;

            buf[n++] = k;
        }
    }

    /* MeshFirst hoist: stable partition — mesh kinds first, then the rest. */
    if (pol->mesh_first(pol->self) && n > 1) {
        ca_transport_kind_t tmp[16];
        size_t t = 0;
        for (size_t i = 0; i < n; ++i)
            if (is_mesh_kind(buf[i])) tmp[t++] = buf[i];
        for (size_t i = 0; i < n; ++i)
            if (!is_mesh_kind(buf[i])) tmp[t++] = buf[i];
        memcpy(buf, tmp, n * sizeof(buf[0]));
    }

    if (count) *count = n;
    if (n == 0) return NULL;
    ca_transport_kind_t *out =
        (ca_transport_kind_t *)malloc(n * sizeof(*out));
    if (!out) { if (count) *count = SIZE_MAX; return NULL; }
    memcpy(out, buf, n * sizeof(*out));
    return out;
}

static ca_transport_kind_t *sel_get_cascade(void *self,
                                            const ca_network_payload_t *payload,
                                            const ca_network_context_t *context,
                                            size_t *count) {
    ca_default_selector_t *s = (ca_default_selector_t *)self;
    if (!count) {
        size_t ignore;
        return build_cascade(s, payload, context, &ignore);
    }
    return build_cascade(s, payload, context, count);
}

static ca_transport_kind_t sel_select_best(void *self,
                                           const ca_network_payload_t *payload,
                                           const ca_network_context_t *context) {
    ca_default_selector_t *s = (ca_default_selector_t *)self;
    size_t n = 0;
    ca_transport_kind_t *c = build_cascade(s, payload, context, &n);
    ca_transport_kind_t best = CA_TRANSPORT_LOCAL_STORE; /* store-and-forward floor */
    if (c && n > 0 && n != SIZE_MAX) best = c[0];
    free(c);
    return best;
}

ca_transport_selector_t ca_default_selector_as_selector(
    ca_default_selector_t *s) {
    ca_transport_selector_t v;
    v.self = s;
    v.select_best = sel_select_best;
    v.get_cascade = sel_get_cascade;
    return v;
}
