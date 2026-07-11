/*
 * agents_peer.c — CircleAI.Agents.Peer (C11 port).
 *
 * Ports AgentMessage.cs + PeerAgent.cs + AgentInvocationException.cs +
 * IAgentPeerProtocol.cs + AgentBus.cs + InMemoryAgentPeerProtocol.cs. Everything
 * uses the ca_peer_* prefix so it never collides with agents.h's ca_agent_*.
 *
 * The C# Task / Channel / pump-thread model collapses to synchronous, decoupled,
 * pump-driven delivery (see agents_peer.h). Pure C11 + libc; linear arrays (no
 * hashtable), no pthreads.
 */

#include "circle_ai/agents_peer.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>
#include <time.h>

/* ── shared helpers (copied from media.c house style) ───────────────────── */

static char *ap_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static char *ap_strdup_empty(const char *s) { return ap_strdup(s ? s : ""); }

/* string.IsNullOrWhiteSpace. */
static bool ap_is_ws(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (!isspace((unsigned char)*p)) return false;
    return true;
}

/* Deep-copy raw bytes into a fresh buffer. *out is NULL when len == 0. Returns
 * false only on OOM. src may be NULL when len == 0. */
static bool ap_dup_bytes(uint8_t **out, const uint8_t *src, size_t len) {
    if (len == 0) { *out = NULL; return true; }
    uint8_t *p = (uint8_t *)malloc(len);
    if (!p) { *out = NULL; return false; }
    if (src) memcpy(p, src, len);
    else memset(p, 0, len);
    *out = p;
    return true;
}

/* Portable, NON-cryptographic random fill (time + monotonic counter seeded). The
 * mesh only needs per-run uniqueness for message Ids / correlation IDs; this
 * provides it without sockets, threads, or platform RNG headers. */
static void ap_fill_random_bytes(uint8_t *out, size_t n) {
    static unsigned int seed = 0;
    static unsigned long counter = 0;
    if (seed == 0) {
        seed = (unsigned int)time(NULL) ^ 0x5BD1E995u;
        if (seed == 0) seed = 0x9E3779B9u;
        srand(seed);
    }
    for (size_t i = 0; i < n; ++i) {
        counter += 0x9E3779B97F4A7C15UL;   /* SplitMix-style stir */
        unsigned int r = (unsigned int)rand();
        out[i] = (uint8_t)((r ^ (counter >> 11) ^ (counter >> 3)) & 0xff);
    }
}

/* Fresh 32-lowercase-hex string (Guid.NewGuid().ToString("n")). NULL on OOM. */
static char *ap_synth_hex32(void) {
    uint8_t b[16];
    ap_fill_random_bytes(b, 16);
    char *out = (char *)malloc(33);
    if (!out) return NULL;
    for (size_t i = 0; i < 16; ++i) snprintf(out + i * 2, 3, "%02x", b[i]);
    out[32] = '\0';
    return out;
}

/* ===========================================================================
 * AgentMessage
 * =========================================================================== */

void ca_peer_message_free(ca_peer_message_t *m) {
    if (!m) return;
    free(m->from_uhid);
    free(m->to_uhid);
    free(m->content_type);
    free(m->payload);
    free(m->signature);
    free(m->correlation_id);
    m->from_uhid = m->to_uhid = m->content_type = NULL;
    m->payload = m->signature = NULL;
    m->correlation_id = NULL;
    m->payload_len = m->signature_len = 0;
}
void ca_peer_message_free_array(ca_peer_message_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_peer_message_free(&arr[i]);
    free(arr);
}

bool ca_peer_message_copy(ca_peer_message_t *dst, const ca_peer_message_t *src) {
    memset(dst, 0, sizeof(*dst));
    memcpy(dst->id, src->id, sizeof(dst->id));
    dst->kind          = src->kind;
    dst->from_uhid     = ap_strdup_empty(src->from_uhid);
    dst->to_uhid       = ap_strdup_empty(src->to_uhid);
    dst->content_type  = ap_strdup_empty(src->content_type);
    dst->sent_at_ms    = src->sent_at_ms;
    dst->correlation_id = src->correlation_id ? ap_strdup(src->correlation_id)
                                              : NULL;
    if (!dst->from_uhid || !dst->to_uhid || !dst->content_type ||
        (src->correlation_id && !dst->correlation_id)) {
        ca_peer_message_free(dst);
        return false;
    }
    if (!ap_dup_bytes(&dst->payload, src->payload, src->payload_len)) {
        ca_peer_message_free(dst);
        return false;
    }
    dst->payload_len = src->payload_len;
    if (!ap_dup_bytes(&dst->signature, src->signature, src->signature_len)) {
        ca_peer_message_free(dst);
        return false;
    }
    dst->signature_len = src->signature_len;
    return true;
}

int ca_peer_message_create(ca_peer_message_t *out,
                           ca_peer_message_kind_t kind,
                           const char *from_uhid, const char *to_uhid,
                           const char *content_type,
                           const uint8_t *payload, size_t payload_len,
                           const uint8_t *signature, size_t signature_len,
                           const char *correlation_id, int64_t now_ms) {
    if (!out || !from_uhid || !to_uhid || !content_type) return -1;
    memset(out, 0, sizeof(*out));

    ap_fill_random_bytes(out->id, sizeof(out->id));   /* new random Guid Id */
    out->kind         = kind;
    out->from_uhid    = ap_strdup(from_uhid);
    out->to_uhid      = ap_strdup(to_uhid);
    out->content_type = ap_strdup(content_type);
    out->sent_at_ms   = now_ms;                        /* SentAt = now */
    if (!out->from_uhid || !out->to_uhid || !out->content_type) {
        ca_peer_message_free(out);
        return -1;
    }
    /* CorrelationId = correlationId ?? Guid.NewGuid().ToString("n"). */
    out->correlation_id = correlation_id ? ap_strdup(correlation_id)
                                         : ap_synth_hex32();
    if (!out->correlation_id) { ca_peer_message_free(out); return -1; }

    if (!ap_dup_bytes(&out->payload, payload, payload_len)) {
        ca_peer_message_free(out);
        return -1;
    }
    out->payload_len = payload_len;
    if (!ap_dup_bytes(&out->signature, signature, signature_len)) {
        ca_peer_message_free(out);
        return -1;
    }
    out->signature_len = signature_len;
    return 0;
}

/* ===========================================================================
 * AgentCapability
 * =========================================================================== */

void ca_peer_capability_free(ca_peer_capability_t *c) {
    if (!c) return;
    free(c->name);
    free(c->version);
    free(c->cost_currency);
    c->name = c->version = c->cost_currency = NULL;
}
void ca_peer_capability_free_array(ca_peer_capability_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_peer_capability_free(&arr[i]);
    free(arr);
}

static bool capability_copy(ca_peer_capability_t *dst,
                            const ca_peer_capability_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->name          = ap_strdup_empty(src->name);
    dst->version       = ap_strdup_empty(src->version);
    dst->cost_currency = ap_strdup_empty(src->cost_currency);
    dst->cost_per_invocation = src->cost_per_invocation;
    if (!dst->name || !dst->version || !dst->cost_currency) {
        ca_peer_capability_free(dst);
        return false;
    }
    return true;
}

/* Deep-copy a capability array. false on OOM (out zeroed). */
static bool capability_array_copy(ca_peer_capability_t **out_arr, size_t *out_n,
                                  const ca_peer_capability_t *src, size_t n) {
    *out_arr = NULL;
    *out_n = 0;
    if (n == 0) return true;
    ca_peer_capability_t *arr =
        (ca_peer_capability_t *)calloc(n, sizeof(*arr));
    if (!arr) return false;
    for (size_t i = 0; i < n; ++i) {
        if (!capability_copy(&arr[i], &src[i])) {
            ca_peer_capability_free_array(arr, i);
            return false;
        }
    }
    *out_arr = arr;
    *out_n = n;
    return true;
}

/* ===========================================================================
 * PeerAgent
 * =========================================================================== */

void ca_peer_agent_free(ca_peer_agent_t *a) {
    if (!a) return;
    free(a->uhid_identity_id);
    free(a->display_name);
    ca_peer_capability_free_array(a->capabilities, a->capabilities_count);
    free(a->public_key_der);
    free(a->current_transport_id);
    a->uhid_identity_id = a->display_name = a->current_transport_id = NULL;
    a->capabilities = NULL;
    a->capabilities_count = 0;
    a->public_key_der = NULL;
    a->public_key_len = 0;
}
void ca_peer_agent_free_array(ca_peer_agent_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_peer_agent_free(&arr[i]);
    free(arr);
}

bool ca_peer_agent_copy(ca_peer_agent_t *dst, const ca_peer_agent_t *src) {
    memset(dst, 0, sizeof(*dst));
    memcpy(dst->id, src->id, sizeof(dst->id));
    dst->uhid_identity_id = ap_strdup_empty(src->uhid_identity_id);
    dst->display_name     = ap_strdup_empty(src->display_name);
    dst->current_transport_id = src->current_transport_id
        ? ap_strdup(src->current_transport_id) : NULL;
    dst->last_seen_at_ms  = src->last_seen_at_ms;
    if (!dst->uhid_identity_id || !dst->display_name ||
        (src->current_transport_id && !dst->current_transport_id)) {
        ca_peer_agent_free(dst);
        return false;
    }
    if (!capability_array_copy(&dst->capabilities, &dst->capabilities_count,
                               src->capabilities, src->capabilities_count)) {
        ca_peer_agent_free(dst);
        return false;
    }
    if (!ap_dup_bytes(&dst->public_key_der, src->public_key_der,
                      src->public_key_len)) {
        ca_peer_agent_free(dst);
        return false;
    }
    dst->public_key_len = src->public_key_len;
    return true;
}

/* ===========================================================================
 * AgentBus
 * =========================================================================== */

/* Unbounded FIFO of message copies (a peer inbox). Mirrors the unbounded C#
 * Channel: writes are retained until read. Same shape as media.c's pos_fifo_t. */
typedef struct {
    ca_peer_message_t *items;
    size_t head, count, cap;
} msg_fifo_t;

static bool msg_fifo_push(msg_fifo_t *q, ca_peer_message_t item) {
    if (q->count == q->cap) {
        if (q->head > 0) {
            size_t live = q->count - q->head;
            memmove(q->items, q->items + q->head, live * sizeof(*q->items));
            q->count = live; q->head = 0;
        }
        if (q->count == q->cap) {
            size_t nc = q->cap ? q->cap * 2 : 4;
            void *ni = realloc(q->items, nc * sizeof(*q->items));
            if (!ni) return false;
            q->items = (ca_peer_message_t *)ni;
            q->cap = nc;
        }
    }
    q->items[q->count++] = item;
    return true;
}
static bool msg_fifo_pop(msg_fifo_t *q, ca_peer_message_t *out) {
    if (q->head >= q->count) return false;
    *out = q->items[q->head];
    memset(&q->items[q->head], 0, sizeof(q->items[q->head]));
    q->head++;
    if (q->head == q->count) { q->head = 0; q->count = 0; }
    return true;
}
static void msg_fifo_free(msg_fifo_t *q) {
    for (size_t i = q->head; i < q->count; ++i)
        ca_peer_message_free(&q->items[i]);
    free(q->items);
    q->items = NULL;
    q->head = q->count = q->cap = 0;
}

/* One bus slot: a registered peer record + its inbox. */
typedef struct {
    ca_peer_agent_t peer;
    msg_fifo_t      inbox;
} bus_slot_t;

struct ca_peer_bus {
    bus_slot_t *slots;
    size_t      count, cap;
};

ca_peer_bus_t *ca_peer_bus_create(void) {
    return (ca_peer_bus_t *)calloc(1, sizeof(ca_peer_bus_t));
}
void ca_peer_bus_destroy(ca_peer_bus_t *bus) {
    if (!bus) return;
    for (size_t i = 0; i < bus->count; ++i) {
        ca_peer_agent_free(&bus->slots[i].peer);
        msg_fifo_free(&bus->slots[i].inbox);
    }
    free(bus->slots);
    free(bus);
}

static size_t bus_index_of(const ca_peer_bus_t *bus, const char *uhid) {
    for (size_t i = 0; i < bus->count; ++i)
        if (strcmp(bus->slots[i].peer.uhid_identity_id, uhid) == 0) return i;
    return (size_t)-1;
}

int ca_peer_bus_register(ca_peer_bus_t *bus, const ca_peer_agent_t *peer) {
    if (!bus || !peer || ap_is_ws(peer->uhid_identity_id)) return -1;

    ca_peer_agent_t copy;
    if (!ca_peer_agent_copy(&copy, peer)) return -1;

    size_t idx = bus_index_of(bus, peer->uhid_identity_id);
    if (idx != (size_t)-1) {
        /* Re-registering replaces the prior record; the inbox is preserved
         * (GetOrAdd keeps the existing channel). */
        ca_peer_agent_free(&bus->slots[idx].peer);
        bus->slots[idx].peer = copy;
        return 0;
    }
    if (bus->count == bus->cap) {
        size_t nc = bus->cap ? bus->cap * 2 : 4;
        void *n = realloc(bus->slots, nc * sizeof(*bus->slots));
        if (!n) { ca_peer_agent_free(&copy); return -1; }
        bus->slots = (bus_slot_t *)n;
        bus->cap = nc;
    }
    memset(&bus->slots[bus->count], 0, sizeof(bus->slots[bus->count]));
    bus->slots[bus->count].peer = copy;
    bus->count++;
    return 0;
}

int ca_peer_bus_unregister(ca_peer_bus_t *bus, const char *uhid) {
    if (!bus || ap_is_ws(uhid)) return -1;
    size_t idx = bus_index_of(bus, uhid);
    if (idx == (size_t)-1) return 0;   /* TryRemove miss — no-op */
    ca_peer_agent_free(&bus->slots[idx].peer);
    msg_fifo_free(&bus->slots[idx].inbox);
    /* swap-remove (order is irrelevant to the bus). */
    bus->slots[idx] = bus->slots[--bus->count];
    return 0;
}

bool ca_peer_bus_try_get_peer(const ca_peer_bus_t *bus, const char *uhid,
                             ca_peer_agent_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!bus || ap_is_ws(uhid) || !out) return false;
    size_t idx = bus_index_of(bus, uhid);
    if (idx == (size_t)-1) return false;
    return ca_peer_agent_copy(out, &bus->slots[idx].peer);
}

ca_peer_agent_t *ca_peer_bus_registered_peers(const ca_peer_bus_t *bus,
                                             size_t *out_count) {
    if (!out_count) return NULL;
    if (!bus) { *out_count = (size_t)-1; return NULL; }
    if (bus->count == 0) { *out_count = 0; return NULL; }

    ca_peer_agent_t *out = (ca_peer_agent_t *)calloc(bus->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < bus->count; ++i) {
        if (!ca_peer_agent_copy(&out[i], &bus->slots[i].peer)) {
            ca_peer_agent_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = bus->count;
    return out;
}

/* Enqueue a fresh copy of message into slot idx's inbox. false on OOM. */
static bool bus_enqueue(ca_peer_bus_t *bus, size_t idx,
                        const ca_peer_message_t *message) {
    ca_peer_message_t copy;
    if (!ca_peer_message_copy(&copy, message)) return false;
    if (!msg_fifo_push(&bus->slots[idx].inbox, copy)) {
        ca_peer_message_free(&copy);
        return false;
    }
    return true;
}

int ca_peer_bus_send(ca_peer_bus_t *bus, const ca_peer_message_t *message) {
    if (!bus || !message || !message->to_uhid || !message->from_uhid) return -1;

    if (strcmp(message->to_uhid, "*") == 0) {
        /* Broadcast to every inbox except the sender's own. */
        for (size_t i = 0; i < bus->count; ++i) {
            if (strcmp(bus->slots[i].peer.uhid_identity_id,
                       message->from_uhid) == 0)
                continue;
            if (!bus_enqueue(bus, i, message)) return -1;
        }
        return 0;
    }

    size_t idx = bus_index_of(bus, message->to_uhid);
    if (idx == (size_t)-1) return 0;   /* unknown target: dropped silently */
    return bus_enqueue(bus, idx, message) ? 0 : -1;
}

bool ca_peer_bus_try_receive(ca_peer_bus_t *bus, const char *uhid,
                            ca_peer_message_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!bus || ap_is_ws(uhid) || !out) return false;
    size_t idx = bus_index_of(bus, uhid);
    if (idx == (size_t)-1) return false;
    return msg_fifo_pop(&bus->slots[idx].inbox, out);
}

size_t ca_peer_bus_inbox_count(const ca_peer_bus_t *bus, const char *uhid) {
    if (!bus || !uhid) return 0;
    for (size_t i = 0; i < bus->count; ++i)
        if (strcmp(bus->slots[i].peer.uhid_identity_id, uhid) == 0)
            return bus->slots[i].inbox.count - bus->slots[i].inbox.head;
    return 0;
}

/* ===========================================================================
 * InMemoryAgentPeerProtocol
 * =========================================================================== */

/* last-seen map entry (ConcurrentDictionary<string,DateTimeOffset>). */
typedef struct {
    char   *uhid;        /* owned */
    int64_t last_seen_ms;
} last_seen_t;

/* pending invocation entry (ConcurrentDictionary<Guid,TCS>). Holds the reply once
 * it lands (completed==true), matched by the 16-byte Invoke.Id. */
typedef struct {
    uint8_t           invoke_id[16];
    bool              completed;
    ca_peer_message_t reply;      /* valid only when completed */
} pending_t;

struct ca_peer_protocol {
    char                         *own_uhid;      /* owned */
    ca_peer_bus_t                *bus;           /* borrowed */
    ca_peer_capability_t         *own_caps;      /* owned array */
    size_t                        own_caps_count;
    uint8_t                      *own_pubkey;    /* owned; NULL when 0 */
    size_t                        own_pubkey_len;
    ca_peer_signer_fn             signer;
    ca_peer_capability_handler_fn capability_handler;
    void                         *ctx;

    last_seen_t                  *last_seen;
    size_t                        last_seen_count, last_seen_cap;

    pending_t                    *pending;
    size_t                        pending_count, pending_cap;

    msg_fifo_t                    external_inbox;  /* StreamInbox buffer */
};

/* Sign(data): signer seam or empty (the C# `_signer is null ? [] : _signer(data)`).
 * On success *out_sig / *out_len describe a malloc'd buffer (NULL/0 for empty).
 * false only on a signer-reported failure. */
static bool proto_sign(ca_peer_protocol_t *p, const uint8_t *data, size_t len,
                       uint8_t **out_sig, size_t *out_len) {
    *out_sig = NULL;
    *out_len = 0;
    if (!p->signer) return true;                      /* empty signature */
    if (p->signer(p->ctx, data, len, out_sig, out_len) != 0) {
        *out_sig = NULL; *out_len = 0;
        return false;
    }
    return true;
}

/* Update _lastSeen[uhid] = ts (upsert). Best-effort: a failed grow is ignored. */
static void proto_touch_last_seen(ca_peer_protocol_t *p, const char *uhid,
                                  int64_t ts) {
    if (ap_is_ws(uhid)) return;
    for (size_t i = 0; i < p->last_seen_count; ++i) {
        if (strcmp(p->last_seen[i].uhid, uhid) == 0) {
            p->last_seen[i].last_seen_ms = ts;
            return;
        }
    }
    if (p->last_seen_count == p->last_seen_cap) {
        size_t nc = p->last_seen_cap ? p->last_seen_cap * 2 : 4;
        void *n = realloc(p->last_seen, nc * sizeof(*p->last_seen));
        if (!n) return;
        p->last_seen = (last_seen_t *)n;
        p->last_seen_cap = nc;
    }
    char *dup = ap_strdup(uhid);
    if (!dup) return;
    p->last_seen[p->last_seen_count].uhid = dup;
    p->last_seen[p->last_seen_count].last_seen_ms = ts;
    p->last_seen_count++;
}

/* WithLastSeen(peer): overlay LastSeenAt from the local map when present. */
static void proto_overlay_last_seen(const ca_peer_protocol_t *p,
                                    ca_peer_agent_t *peer) {
    for (size_t i = 0; i < p->last_seen_count; ++i)
        if (strcmp(p->last_seen[i].uhid, peer->uhid_identity_id) == 0) {
            peer->last_seen_at_ms = p->last_seen[i].last_seen_ms;
            return;
        }
    /* absent -> keep peer.LastSeenAt (already set). */
}

/* Register a pending invocation for invoke_id. false on OOM. */
static bool proto_add_pending(ca_peer_protocol_t *p, const uint8_t invoke_id[16]) {
    if (p->pending_count == p->pending_cap) {
        size_t nc = p->pending_cap ? p->pending_cap * 2 : 4;
        void *n = realloc(p->pending, nc * sizeof(*p->pending));
        if (!n) return false;
        p->pending = (pending_t *)n;
        p->pending_cap = nc;
    }
    pending_t *e = &p->pending[p->pending_count++];
    memset(e, 0, sizeof(*e));
    memcpy(e->invoke_id, invoke_id, 16);
    e->completed = false;
    return true;
}

static size_t proto_pending_index(const ca_peer_protocol_t *p,
                                  const uint8_t id[16]) {
    for (size_t i = 0; i < p->pending_count; ++i)
        if (memcmp(p->pending[i].invoke_id, id, 16) == 0) return i;
    return (size_t)-1;
}

ca_peer_protocol_t *ca_peer_protocol_create(
    const char *own_uhid, ca_peer_bus_t *bus,
    const ca_peer_capability_t *caps, size_t ncaps,
    const uint8_t *pubkey, size_t pubkey_len,
    ca_peer_signer_fn signer, ca_peer_capability_handler_fn capability_handler,
    void *ctx, int64_t now_ms) {
    if (ap_is_ws(own_uhid) || !bus || (ncaps > 0 && !caps)) return NULL;

    ca_peer_protocol_t *p = (ca_peer_protocol_t *)calloc(1, sizeof(*p));
    if (!p) return NULL;
    p->own_uhid = ap_strdup(own_uhid);
    if (!p->own_uhid) { free(p); return NULL; }
    p->bus                = bus;
    p->signer             = signer;
    p->capability_handler = capability_handler;
    p->ctx                = ctx;

    if (!capability_array_copy(&p->own_caps, &p->own_caps_count, caps, ncaps)) {
        free(p->own_uhid); free(p);
        return NULL;
    }
    if (!ap_dup_bytes(&p->own_pubkey, pubkey, pubkey_len)) {
        ca_peer_capability_free_array(p->own_caps, p->own_caps_count);
        free(p->own_uhid); free(p);
        return NULL;
    }
    p->own_pubkey_len = pubkey_len;

    /* _bus.Register(new PeerAgent(... "in-memory", DisplayName=ownUhid, now)). */
    ca_peer_agent_t self;
    memset(&self, 0, sizeof(self));
    ap_fill_random_bytes(self.id, sizeof(self.id));
    self.uhid_identity_id     = (char *)own_uhid;      /* borrowed for the copy */
    self.display_name         = (char *)own_uhid;
    self.capabilities         = p->own_caps;           /* borrowed for the copy */
    self.capabilities_count   = p->own_caps_count;
    self.public_key_der       = p->own_pubkey;
    self.public_key_len       = p->own_pubkey_len;
    self.current_transport_id = (char *)"in-memory";
    self.last_seen_at_ms      = now_ms;
    if (ca_peer_bus_register(bus, &self) != 0) {
        free(p->own_pubkey);
        ca_peer_capability_free_array(p->own_caps, p->own_caps_count);
        free(p->own_uhid); free(p);
        return NULL;
    }
    return p;
}

void ca_peer_protocol_destroy(ca_peer_protocol_t *proto) {
    if (!proto) return;
    ca_peer_bus_unregister(proto->bus, proto->own_uhid);   /* _bus.Unregister */

    for (size_t i = 0; i < proto->last_seen_count; ++i)
        free(proto->last_seen[i].uhid);
    free(proto->last_seen);

    for (size_t i = 0; i < proto->pending_count; ++i)
        if (proto->pending[i].completed)
            ca_peer_message_free(&proto->pending[i].reply);
    free(proto->pending);

    msg_fifo_free(&proto->external_inbox);
    ca_peer_capability_free_array(proto->own_caps, proto->own_caps_count);
    free(proto->own_pubkey);
    free(proto->own_uhid);
    free(proto);
}

const char *ca_peer_protocol_own_uhid(const ca_peer_protocol_t *proto) {
    return proto ? proto->own_uhid : NULL;
}

/* CompletePending: Response/Decline carry the original Invoke's Id in the first 16
 * payload bytes. Match and stash the reply on the pending entry. */
static void proto_complete_pending(ca_peer_protocol_t *p,
                                   const ca_peer_message_t *message) {
    if (message->payload_len < 16) return;
    size_t idx = proto_pending_index(p, message->payload);
    if (idx == (size_t)-1) return;
    if (p->pending[idx].completed) return;   /* TrySetResult already fired once */
    if (ca_peer_message_copy(&p->pending[idx].reply, message))
        p->pending[idx].completed = true;
}

/* RouteInvokeAsync: hand the (first) advertised capability to the handler and Send
 * a Response (handler ok) or Decline (non-0 / no handler), correlation-prefixed
 * with the original Invoke.Id. */
static void proto_route_invoke(ca_peer_protocol_t *p,
                               const ca_peer_message_t *invoke, int64_t now_ms) {
    if (!p->capability_handler) return;   /* C# early return when handler is null */

    /* The mock hands the first advertised capability to the handler. */
    ca_peer_capability_t fallback = { (char *)"unknown", (char *)"0.0.0", 0.0,
                                      (char *)"SDPKT" };
    const ca_peer_capability_t *cap =
        (p->own_caps_count > 0) ? &p->own_caps[0] : &fallback;

    uint8_t *result = NULL;
    size_t   result_len = 0;
    int      hr = p->capability_handler(p->ctx, cap, invoke->payload,
                                        invoke->payload_len, &result, &result_len);

    /* correlationPrefix = invoke.Id.ToByteArray() (the 16-byte Id). A non-0 handler
     * return is the C# `result is null` -> Decline. hr == 0 is always a Response,
     * even with an empty result (the C# non-null empty byte[]). */
    if (hr != 0) {
        /* Decline: payload = correlationPrefix. */
        free(result);
        uint8_t *sig = NULL; size_t sig_len = 0;
        proto_sign(p, invoke->id, 16, &sig, &sig_len);
        ca_peer_message_t decline;
        if (ca_peer_message_create(&decline, CA_PEER_MSG_DECLINE, p->own_uhid,
                                   invoke->from_uhid, "application/octet-stream",
                                   invoke->id, 16, sig, sig_len, NULL,
                                   now_ms) == 0) {
            ca_peer_bus_send(p->bus, &decline);
            ca_peer_message_free(&decline);
        }
        free(sig);
        return;
    }

    /* Response: payload = correlationPrefix ++ result. */
    size_t total = 16 + result_len;
    uint8_t *payload = (uint8_t *)malloc(total ? total : 1);
    if (!payload) { free(result); return; }
    memcpy(payload, invoke->id, 16);
    if (result_len > 0 && result) memcpy(payload + 16, result, result_len);
    free(result);

    uint8_t *sig = NULL; size_t sig_len = 0;
    proto_sign(p, payload, total, &sig, &sig_len);
    ca_peer_message_t response;
    if (ca_peer_message_create(&response, CA_PEER_MSG_RESPONSE, p->own_uhid,
                               invoke->from_uhid, "application/octet-stream",
                               payload, total, sig, sig_len, NULL, now_ms) == 0) {
        ca_peer_bus_send(p->bus, &response);
        ca_peer_message_free(&response);
    }
    free(sig);
    free(payload);
}

int ca_peer_protocol_pump(ca_peer_protocol_t *proto, int64_t now_ms) {
    if (!proto) return -1;
    int processed = 0;
    ca_peer_message_t msg;

    /* Drain this proto's inbox once (the pump loop over _bus.Receive). */
    while (ca_peer_bus_try_receive(proto->bus, proto->own_uhid, &msg)) {
        /* _lastSeen[message.FromUhid] = message.SentAt. */
        proto_touch_last_seen(proto, msg.from_uhid, msg.sent_at_ms);

        switch (msg.kind) {
            case CA_PEER_MSG_RESPONSE:
            case CA_PEER_MSG_DECLINE:
                proto_complete_pending(proto, &msg);
                break;
            case CA_PEER_MSG_INVOKE:
                proto_route_invoke(proto, &msg, now_ms);
                break;
            default:
                break;
        }

        /* Every inbound message is also surfaced to external consumers. */
        ca_peer_message_t surfaced;
        if (ca_peer_message_copy(&surfaced, &msg)) {
            if (!msg_fifo_push(&proto->external_inbox, surfaced))
                ca_peer_message_free(&surfaced);
        }
        ca_peer_message_free(&msg);
        processed++;
    }
    return processed;
}

bool ca_peer_protocol_try_read_inbox(ca_peer_protocol_t *proto,
                                    ca_peer_message_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!proto || !out) return false;
    return msg_fifo_pop(&proto->external_inbox, out);
}

ca_peer_agent_t *ca_peer_protocol_discover(ca_peer_protocol_t *proto,
                                          int64_t now_ms, size_t *out_count) {
    if (!out_count) return NULL;
    if (!proto) { *out_count = (size_t)-1; return NULL; }

    /* Broadcast a signed Discover (empty payload). */
    uint8_t *sig = NULL; size_t sig_len = 0;
    proto_sign(proto, NULL, 0, &sig, &sig_len);
    ca_peer_message_t announce;
    if (ca_peer_message_create(&announce, CA_PEER_MSG_DISCOVER, proto->own_uhid,
                               "*", "application/json", NULL, 0, sig, sig_len,
                               NULL, now_ms) == 0) {
        ca_peer_bus_send(proto->bus, &announce);
        ca_peer_message_free(&announce);
    }
    free(sig);

    /* return bus.RegisteredPeers.Where(!= self).Select(WithLastSeen). */
    size_t all_n = 0;
    ca_peer_agent_t *all = ca_peer_bus_registered_peers(proto->bus, &all_n);
    if (all_n == (size_t)-1) { *out_count = (size_t)-1; return NULL; }
    if (all_n == 0) { *out_count = 0; return NULL; }

    /* filter self, overlay last-seen (in place, then compact). */
    size_t kept = 0;
    for (size_t i = 0; i < all_n; ++i) {
        if (strcmp(all[i].uhid_identity_id, proto->own_uhid) == 0) {
            ca_peer_agent_free(&all[i]);
            continue;
        }
        proto_overlay_last_seen(proto, &all[i]);
        if (kept != i) all[kept] = all[i];
        kept++;
    }
    if (kept == 0) { free(all); *out_count = 0; return NULL; }
    *out_count = kept;
    return all;
}

bool ca_peer_protocol_greet(ca_peer_protocol_t *proto, const char *target_uhid,
                           int64_t now_ms, ca_peer_agent_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!proto || ap_is_ws(target_uhid) || !out) return false;

    /* if (!bus.TryGetPeer(target)) return null. */
    if (!ca_peer_bus_try_get_peer(proto->bus, target_uhid, out)) return false;

    /* Send a signed Greet (empty payload). */
    uint8_t *sig = NULL; size_t sig_len = 0;
    proto_sign(proto, NULL, 0, &sig, &sig_len);
    ca_peer_message_t greet;
    if (ca_peer_message_create(&greet, CA_PEER_MSG_GREET, proto->own_uhid,
                               target_uhid, "application/json", NULL, 0,
                               sig, sig_len, NULL, now_ms) == 0) {
        ca_peer_bus_send(proto->bus, &greet);
        ca_peer_message_free(&greet);
    }
    free(sig);

    proto_overlay_last_seen(proto, out);   /* WithLastSeen(peer) */
    return true;
}

ca_peer_capability_t *ca_peer_protocol_query_capabilities(
    ca_peer_protocol_t *proto, const char *target_uhid, size_t *out_count) {
    if (!out_count) return NULL;
    if (!proto || ap_is_ws(target_uhid)) { *out_count = (size_t)-1; return NULL; }

    ca_peer_agent_t peer;
    if (!ca_peer_bus_try_get_peer(proto->bus, target_uhid, &peer)) {
        *out_count = 0;   /* unknown peer -> Array.Empty */
        return NULL;
    }
    if (peer.capabilities_count == 0) {
        ca_peer_agent_free(&peer);
        *out_count = 0;
        return NULL;
    }
    ca_peer_capability_t *out = NULL;
    size_t n = 0;
    bool ok = capability_array_copy(&out, &n, peer.capabilities,
                                    peer.capabilities_count);
    ca_peer_agent_free(&peer);
    if (!ok) { *out_count = (size_t)-1; return NULL; }
    *out_count = n;
    return out;
}

ca_peer_error_t ca_peer_protocol_invoke(ca_peer_protocol_t *proto,
                                       const char *target_uhid,
                                       const ca_peer_capability_t *capability,
                                       const uint8_t *request_payload,
                                       size_t request_len,
                                       uint8_t out_invoke_id[16], int64_t now_ms) {
    if (!proto || ap_is_ws(target_uhid) || !capability || !out_invoke_id)
        return CA_PEER_UNREACHABLE;

    /* if (!bus.TryGetPeer(target)) throw Unreachable. */
    ca_peer_agent_t peer;
    if (!ca_peer_bus_try_get_peer(proto->bus, target_uhid, &peer))
        return CA_PEER_UNREACHABLE;
    ca_peer_agent_free(&peer);

    /* Build a signed Invoke (application/octet-stream, payload=requestPayload). */
    uint8_t *sig = NULL; size_t sig_len = 0;
    proto_sign(proto, request_payload, request_len, &sig, &sig_len);
    ca_peer_message_t invoke;
    if (ca_peer_message_create(&invoke, CA_PEER_MSG_INVOKE, proto->own_uhid,
                               target_uhid, "application/octet-stream",
                               request_payload, request_len, sig, sig_len,
                               NULL, now_ms) != 0) {
        free(sig);
        return CA_PEER_UNREACHABLE;
    }
    free(sig);

    memcpy(out_invoke_id, invoke.id, 16);

    /* Register the pending invocation keyed by Invoke.Id, then Send. */
    if (!proto_add_pending(proto, invoke.id)) {
        ca_peer_message_free(&invoke);
        return CA_PEER_UNREACHABLE;
    }
    ca_peer_bus_send(proto->bus, &invoke);
    ca_peer_message_free(&invoke);
    return CA_PEER_OK;
}

bool ca_peer_protocol_try_take_reply(ca_peer_protocol_t *proto,
                                    const uint8_t invoke_id[16],
                                    ca_peer_message_t *out, ca_peer_error_t *err) {
    if (out) memset(out, 0, sizeof(*out));
    if (err) *err = CA_PEER_OK;
    if (!proto || !invoke_id || !out) return false;

    size_t idx = proto_pending_index(proto, invoke_id);
    if (idx == (size_t)-1) return false;             /* no such invocation */
    if (!proto->pending[idx].completed) return false; /* still pending (await) */

    /* Hand over the stashed reply and remove the pending entry. */
    *out = proto->pending[idx].reply;                /* transfer ownership */
    if (err)
        *err = (out->kind == CA_PEER_MSG_DECLINE) ? CA_PEER_DECLINED : CA_PEER_OK;

    /* swap-remove the consumed entry (its reply is now owned by *out). */
    proto->pending[idx] = proto->pending[--proto->pending_count];
    return true;
}
