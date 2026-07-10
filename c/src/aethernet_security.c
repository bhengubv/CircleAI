/*
 * aethernet_security.c — CircleAI.Security.AetherNet bindings (C11 port).
 *
 * AetherMapper, MeshDirectiveStore, MeshSecurityGate, AetherSecurityBridge and
 * AetherIntelligenceAdapter — ported 1:1 from CircleAI.Security.AetherNet. The
 * bridge/adapter are pure translation over the existing peer_security engine.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/aethernet_security.h"

#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdio.h>

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

/* ===========================================================================
 * AetherMapper — explicit switches, default arms match the C# fall-throughs.
 * =========================================================================== */

ca_peer_security_event_kind_t ca_aether_mapper_to_peer_event_kind(
    ca_aether_security_event_kind_t kind) {
    switch (kind) {
        case CA_AETHER_SEC_NODE_AUTH_ATTEMPT:     return CA_PEER_EVENT_AUTH_ATTEMPT;
        case CA_AETHER_SEC_ROUTING_ANOMALY:       return CA_PEER_EVENT_ROUTING_ANOMALY;
        case CA_AETHER_SEC_NODE_BEHAVIOUR_CHANGE: return CA_PEER_EVENT_BEHAVIOUR_CHANGE;
        case CA_AETHER_SEC_ENCRYPTION_EVENT:      return CA_PEER_EVENT_ENCRYPTION_EVENT;
        case CA_AETHER_SEC_INTRUSION_SIGNAL:      return CA_PEER_EVENT_INTRUSION_SIGNAL;
        case CA_AETHER_SEC_PRIVILEGE_ATTEMPT:     return CA_PEER_EVENT_PRIVILEGE_ATTEMPT;
        default:                                  return CA_PEER_EVENT_UNKNOWN;
    }
}

ca_peer_threat_level_t ca_aether_mapper_to_peer_threat_level(
    ca_aether_threat_level_t level) {
    switch (level) {
        case CA_AETHER_THREAT_NONE:     return CA_PEER_THREAT_NONE;
        case CA_AETHER_THREAT_LOW:      return CA_PEER_THREAT_LOW;
        case CA_AETHER_THREAT_MEDIUM:   return CA_PEER_THREAT_MEDIUM;
        case CA_AETHER_THREAT_HIGH:     return CA_PEER_THREAT_HIGH;
        case CA_AETHER_THREAT_CRITICAL: return CA_PEER_THREAT_CRITICAL;
        default:                        return CA_PEER_THREAT_NONE;
    }
}

ca_aether_threat_level_t ca_aether_mapper_to_aether_threat_level(
    ca_peer_threat_level_t level) {
    switch (level) {
        case CA_PEER_THREAT_NONE:     return CA_AETHER_THREAT_NONE;
        case CA_PEER_THREAT_LOW:      return CA_AETHER_THREAT_LOW;
        case CA_PEER_THREAT_MEDIUM:   return CA_AETHER_THREAT_MEDIUM;
        case CA_PEER_THREAT_HIGH:     return CA_AETHER_THREAT_HIGH;
        case CA_PEER_THREAT_CRITICAL: return CA_AETHER_THREAT_CRITICAL;
        default:                      return CA_AETHER_THREAT_NONE;
    }
}

ca_aether_security_directive_kind_t ca_aether_mapper_to_security_directive_kind(
    ca_peer_directive_kind_t kind) {
    switch (kind) {
        case CA_PEER_DIRECTIVE_ELEVATE_MONITORING:
            return CA_AETHER_DIRECTIVE_ELEVATE_MONITORING;
        case CA_PEER_DIRECTIVE_AVOID_NODE:
            return CA_AETHER_DIRECTIVE_AVOID_NODE;
        case CA_PEER_DIRECTIVE_QUARANTINE_NODE:
            return CA_AETHER_DIRECTIVE_QUARANTINE_NODE;
        case CA_PEER_DIRECTIVE_RELEASE_NODE:
            return CA_AETHER_DIRECTIVE_RELEASE_NODE;
        default:
            return CA_AETHER_DIRECTIVE_ELEVATE_MONITORING;
    }
}

/* ===========================================================================
 * MeshDirectiveStore
 * =========================================================================== */

/* Per-node bucket: a growable list of owned directive copies. */
typedef struct {
    char                            *node_id;    /* owned */
    ca_aether_security_directive_t **items;      /* owned array of owned */
    size_t                           count;
    size_t                           cap;
} mds_bucket_t;

struct ca_mesh_directive_store {
    mds_bucket_t   *buckets;   /* owned array */
    size_t          count;
    size_t          cap;
    ca_mesh_clock_fn clock_fn;
    void           *clock_user;
};

ca_mesh_directive_store_t *ca_mesh_directive_store_create(
    ca_mesh_clock_fn clock_fn, void *clock_user) {
    ca_mesh_directive_store_t *s =
        (ca_mesh_directive_store_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->clock_fn = clock_fn;
    s->clock_user = clock_user;
    return s;
}

static void bucket_free(mds_bucket_t *b) {
    if (!b) return;
    free(b->node_id);
    for (size_t i = 0; i < b->count; ++i)
        ca_aether_security_directive_destroy(b->items[i]);
    free(b->items);
    b->node_id = NULL;
    b->items = NULL;
    b->count = b->cap = 0;
}

void ca_mesh_directive_store_destroy(ca_mesh_directive_store_t *store) {
    if (!store) return;
    for (size_t i = 0; i < store->count; ++i) bucket_free(&store->buckets[i]);
    free(store->buckets);
    free(store);
}

static int64_t mds_now(const ca_mesh_directive_store_t *s) {
    return s->clock_fn ? s->clock_fn(s->clock_user) : 0;
}

static long mds_index_of(const ca_mesh_directive_store_t *s, const char *node) {
    for (size_t i = 0; i < s->count; ++i)
        if (strcmp(s->buckets[i].node_id, node) == 0) return (long)i;
    return -1;
}

/* Remove bucket at idx (preserving order). */
static void mds_remove_bucket(ca_mesh_directive_store_t *s, size_t idx) {
    bucket_free(&s->buckets[idx]);
    for (size_t i = idx; i + 1 < s->count; ++i)
        s->buckets[i] = s->buckets[i + 1];
    s->count--;
}

static bool is_block_kind(ca_aether_security_directive_kind_t k) {
    return k == CA_AETHER_DIRECTIVE_AVOID_NODE ||
           k == CA_AETHER_DIRECTIVE_QUARANTINE_NODE;
}

/* IsExpired: Duration set AND (IssuedAt + Duration) <= now. */
static bool directive_expired(const ca_aether_security_directive_t *d,
                              int64_t now) {
    return d->has_duration && (d->issued_at_ms + d->duration_ms) <= now;
}

void ca_mesh_directive_store_on_directive(
    ca_mesh_directive_store_t *store,
    const ca_aether_security_directive_t *directive) {
    if (!store || !directive) return;
    if (!ca_aether_security_directive_has_target(directive)) return;
    const char *node_id = directive->target_node_id;

    if (directive->kind == CA_AETHER_DIRECTIVE_RELEASE_NODE) {
        long idx = mds_index_of(store, node_id);
        if (idx >= 0) mds_remove_bucket(store, (size_t)idx);
        return;
    }

    ca_aether_security_directive_t *copy =
        ca_aether_security_directive_copy(directive);
    if (!copy) return; /* OOM — best effort, drop */

    long idx = mds_index_of(store, node_id);
    if (idx < 0) {
        /* new bucket */
        if (store->count == store->cap) {
            size_t nc = store->cap ? store->cap * 2 : 8;
            mds_bucket_t *nb =
                (mds_bucket_t *)realloc(store->buckets, nc * sizeof(*nb));
            if (!nb) { ca_aether_security_directive_destroy(copy); return; }
            store->buckets = nb;
            store->cap = nc;
        }
        mds_bucket_t *b = &store->buckets[store->count];
        memset(b, 0, sizeof(*b));
        b->node_id = dup_str(node_id);
        if (!b->node_id) {
            ca_aether_security_directive_destroy(copy);
            return;
        }
        store->count++;
        idx = (long)(store->count - 1);
    }

    mds_bucket_t *b = &store->buckets[idx];
    if (b->count == b->cap) {
        size_t nc = b->cap ? b->cap * 2 : 4;
        ca_aether_security_directive_t **ni =
            (ca_aether_security_directive_t **)realloc(b->items,
                                                       nc * sizeof(*ni));
        if (!ni) { ca_aether_security_directive_destroy(copy); return; }
        b->items = ni;
        b->cap = nc;
    }
    b->items[b->count++] = copy;
}

/* ISecurityDirectiveConsumer vtable adapter. */
static void mds_consumer_on_directive(
    void *self, const ca_aether_security_directive_t *d) {
    ca_mesh_directive_store_on_directive((ca_mesh_directive_store_t *)self, d);
}

ca_aether_security_directive_consumer_t
ca_mesh_directive_store_as_consumer(ca_mesh_directive_store_t *store) {
    ca_aether_security_directive_consumer_t c;
    c.self = store;
    c.on_directive = mds_consumer_on_directive;
    return c;
}

bool ca_mesh_directive_store_is_blocked(ca_mesh_directive_store_t *store,
                                        const char *node_id,
                                        char **out_reason) {
    if (out_reason) *out_reason = NULL;
    if (!store || is_null_or_whitespace(node_id)) return false;
    long idx = mds_index_of(store, node_id);
    if (idx < 0) return false;

    mds_bucket_t *b = &store->buckets[idx];
    int64_t now = mds_now(store);
    const ca_aether_security_directive_t *latest_block = NULL;

    /* Walk backwards, dropping expired entries (list.RemoveAt(i)). */
    for (long i = (long)b->count - 1; i >= 0; --i) {
        ca_aether_security_directive_t *d = b->items[i];
        if (directive_expired(d, now)) {
            ca_aether_security_directive_destroy(d);
            for (size_t k = (size_t)i; k + 1 < b->count; ++k)
                b->items[k] = b->items[k + 1];
            b->count--;
            continue;
        }
        if (is_block_kind(d->kind) &&
            (latest_block == NULL || d->issued_at_ms > latest_block->issued_at_ms)) {
            latest_block = d;
        }
    }

    if (b->count == 0) {
        mds_remove_bucket(store, (size_t)idx);
        return false; /* latest_block would dangle; empty bucket => not blocked */
    }
    if (latest_block == NULL) return false;
    if (out_reason) {
        *out_reason = dup_str(latest_block->reason);
        if (!*out_reason) return false; /* OOM — degrade to not-blocked signal */
    }
    return true;
}

size_t ca_mesh_directive_store_get_active(
    const ca_mesh_directive_store_t *store, const char *node_id,
    ca_aether_security_directive_t ***out_directives) {
    if (out_directives) *out_directives = NULL;
    if (!store || is_null_or_whitespace(node_id)) return 0;
    long idx = mds_index_of(store, node_id);
    if (idx < 0) return 0;
    const mds_bucket_t *b = &store->buckets[idx];
    int64_t now = mds_now(store);

    /* Count unexpired (GetActiveDirectives does NOT mutate the list). */
    size_t n = 0;
    for (size_t i = 0; i < b->count; ++i)
        if (!directive_expired(b->items[i], now)) n++;
    if (n == 0) return 0;

    ca_aether_security_directive_t **out =
        (ca_aether_security_directive_t **)calloc(n, sizeof(*out));
    if (!out) return (size_t)-1;
    size_t j = 0;
    for (size_t i = 0; i < b->count; ++i) {
        if (directive_expired(b->items[i], now)) continue;
        out[j] = ca_aether_security_directive_copy(b->items[i]);
        if (!out[j]) {
            for (size_t k = 0; k < j; ++k)
                ca_aether_security_directive_destroy(out[k]);
            free(out);
            return (size_t)-1;
        }
        j++;
    }
    if (out_directives) *out_directives = out;
    else {
        for (size_t k = 0; k < n; ++k)
            ca_aether_security_directive_destroy(out[k]);
        free(out);
    }
    return n;
}

int ca_mesh_directive_store_tracked_node_count(
    const ca_mesh_directive_store_t *store) {
    return store ? (int)store->count : 0;
}

/* ===========================================================================
 * MeshSecurityGate
 * =========================================================================== */

struct ca_mesh_security_gate {
    ca_mesh_directive_store_t *store; /* borrowed */
};

ca_mesh_security_gate_t *ca_mesh_security_gate_create(
    ca_mesh_directive_store_t *store) {
    if (!store) return NULL;
    ca_mesh_security_gate_t *g =
        (ca_mesh_security_gate_t *)calloc(1, sizeof(*g));
    if (!g) return NULL;
    g->store = store;
    return g;
}

void ca_mesh_security_gate_destroy(ca_mesh_security_gate_t *gate) {
    free(gate);
}

void ca_mesh_gate_decision_free(ca_mesh_gate_decision_t *d) {
    if (!d) return;
    free(d->reason);
    d->reason = NULL;
}

int ca_mesh_security_gate_decide(ca_mesh_security_gate_t *gate,
                                 const char *user_or_node_id,
                                 ca_mesh_gate_decision_t *out) {
    if (!gate || !out) return -1;
    out->is_blocked = false;
    out->reason = NULL;
    if (is_null_or_whitespace(user_or_node_id)) {
        out->reason = dup_str(""); /* GateDecision.Allowed => Reason "" */
        return out->reason ? 0 : -1;
    }
    char *reason = NULL;
    if (ca_mesh_directive_store_is_blocked(gate->store, user_or_node_id,
                                           &reason)) {
        out->is_blocked = true;
        out->reason = reason ? reason : dup_str("");
        return out->reason ? 0 : -1;
    }
    out->reason = dup_str("");
    return out->reason ? 0 : -1;
}

bool ca_mesh_security_gate_enforce(ca_mesh_security_gate_t *gate,
                                   const char *user_or_node_id,
                                   char **out_reason, char **out_message) {
    if (out_reason) *out_reason = NULL;
    if (out_message) *out_message = NULL;
    if (!gate) return true;
    ca_mesh_gate_decision_t d;
    if (ca_mesh_security_gate_decide(gate, user_or_node_id, &d) != 0)
        return true; /* OOM — fail open like Decide's Allowed default */
    if (!d.is_blocked) {
        ca_mesh_gate_decision_free(&d);
        return true;
    }
    /* Blocked — build the exception-equivalent outputs. */
    if (out_reason) *out_reason = dup_str(d.reason);
    if (out_message) {
        const char *id = user_or_node_id ? user_or_node_id : "";
        const char *rs = d.reason ? d.reason : "";
        int need = snprintf(NULL, 0, "Mesh has blocked '%s': %s", id, rs);
        if (need >= 0) {
            char *msg = (char *)malloc((size_t)need + 1);
            if (msg) {
                snprintf(msg, (size_t)need + 1, "Mesh has blocked '%s': %s",
                         id, rs);
                *out_message = msg;
            }
        }
    }
    ca_mesh_gate_decision_free(&d);
    return false;
}

/* ===========================================================================
 * AetherSecurityBridge — IAISecurityLayer over SecurityLayerService
 * =========================================================================== */

struct ca_aether_security_bridge {
    ca_security_layer_service_t     *layer;        /* borrowed */
    ca_aether_telemetry_t            telemetry;    /* set on start */
    ca_aether_subscription_t        *tel_sub;      /* owned by the feed */
    bool                             subscribed;
};

ca_aether_security_bridge_t *ca_aether_security_bridge_create(
    ca_security_layer_service_t *layer) {
    if (!layer) return NULL;
    ca_aether_security_bridge_t *b =
        (ca_aether_security_bridge_t *)calloc(1, sizeof(*b));
    if (!b) return NULL;
    b->layer = layer;
    return b;
}

void ca_aether_security_bridge_destroy(ca_aether_security_bridge_t *bridge) {
    free(bridge);
}

/* --- telemetry observer: translate Aether events into the peer layer --- */

static void bridge_on_security_event(void *self,
                                     const ca_aether_security_event_t *e) {
    ca_aether_security_bridge_t *b = (ca_aether_security_bridge_t *)self;
    if (!b || !e) return;
    ca_peer_security_event_t *peer = ca_peer_security_event_create(
        e->node_id,
        ca_aether_mapper_to_peer_event_kind(e->kind),
        ca_aether_mapper_to_peer_threat_level(e->threat_level),
        e->description,
        "aether",           /* TransportId: "aether" */
        e->occurred_at_ms);
    if (!peer) return;
    ca_security_layer_service_handle_peer_event(b->layer, peer);
    ca_peer_security_event_destroy(peer);
}

static void bridge_on_node_event(void *self,
                                 const ca_aether_node_event_t *e) {
    ca_aether_security_bridge_t *b = (ca_aether_security_bridge_t *)self;
    if (!b || !e) return;
    if (ca_aether_node_event_is_exit(e)) /* e.IsExit */
        ca_security_layer_service_handle_peer_left(b->layer, e->node_id);
}

static void bridge_start(void *self, const ca_aether_telemetry_t *telemetry) {
    ca_aether_security_bridge_t *b = (ca_aether_security_bridge_t *)self;
    if (!b || !telemetry) return;
    b->telemetry = *telemetry;
    ca_aether_telemetry_observer_t obs;
    memset(&obs, 0, sizeof(obs));
    obs.self = b;
    obs.on_security_event = bridge_on_security_event;
    obs.on_node_event = bridge_on_node_event;
    /* transport / route / network intentionally NULL (ignored, as in C#). */
    b->tel_sub = telemetry->subscribe(telemetry->self, &obs);
    b->subscribed = (b->tel_sub != NULL);
    ca_security_layer_service_start(b->layer);
}

static void bridge_stop(void *self) {
    ca_aether_security_bridge_t *b = (ca_aether_security_bridge_t *)self;
    if (!b) return;
    if (b->subscribed && b->telemetry.unsubscribe) {
        b->telemetry.unsubscribe(b->telemetry.self, b->tel_sub);
        b->tel_sub = NULL;
        b->subscribed = false;
    }
    ca_security_layer_service_stop(b->layer);
}

/* --- directive adapter: PeerDirective -> SecurityDirective -> Aether consumer.
 * The consumer callback + the aether subscription token are bundled so the
 * peer publisher can drive the Aether consumer. */
typedef struct {
    ca_aether_security_directive_consumer_t consumer; /* copied by value */
} bridge_dir_adapter_t;

static void bridge_directive_trampoline(void *user,
                                        const ca_peer_directive_t *directive) {
    bridge_dir_adapter_t *ad = (bridge_dir_adapter_t *)user;
    if (!ad || !directive || !ad->consumer.on_directive) return;
    /* Build the Aether directive (TrustScoreOverride always set: peer
     * TrustScore is a plain double). */
    ca_aether_security_directive_t *aether = ca_aether_security_directive_create(
        ca_aether_mapper_to_security_directive_kind(directive->kind),
        directive->target_node_id,
        true, directive->trust_score,
        ca_aether_mapper_to_aether_threat_level(directive->threat_level),
        directive->reason,
        directive->has_duration, directive->duration_ms,
        directive->issued_at_ms);
    if (!aether) return;
    ad->consumer.on_directive(ad->consumer.self, aether);
    ca_aether_security_directive_destroy(aether);
}

/* The Aether directive subscription wraps the peer subscription + the heap
 * adapter so unsubscribe can free both. */
struct ca_aether_directive_subscription {
    ca_directive_subscription_t *peer_sub; /* owned by peer publisher */
    bridge_dir_adapter_t        *adapter;  /* owned here */
    ca_aether_security_bridge_t *bridge;
};

static ca_aether_directive_subscription_t *bridge_subscribe_directives(
    void *self, const ca_aether_security_directive_consumer_t *consumer) {
    ca_aether_security_bridge_t *b = (ca_aether_security_bridge_t *)self;
    if (!b || !consumer) return NULL;
    bridge_dir_adapter_t *ad =
        (bridge_dir_adapter_t *)calloc(1, sizeof(*ad));
    if (!ad) return NULL;
    ad->consumer = *consumer;
    ca_aether_directive_subscription_t *sub =
        (ca_aether_directive_subscription_t *)calloc(1, sizeof(*sub));
    if (!sub) { free(ad); return NULL; }
    sub->adapter = ad;
    sub->bridge = b;
    sub->peer_sub = ca_security_layer_service_subscribe_directives(
        b->layer, bridge_directive_trampoline, ad);
    if (!sub->peer_sub) { free(ad); free(sub); return NULL; }
    return sub;
}

static void bridge_unsubscribe_directives(
    void *self, ca_aether_directive_subscription_t *sub) {
    ca_aether_security_bridge_t *b = (ca_aether_security_bridge_t *)self;
    if (!b || !sub) return;
    /* Dispose the peer subscription via the layer (delegates to the publisher),
     * then free our adapter + wrapper. */
    if (sub->peer_sub)
        ca_security_layer_service_unsubscribe_directives(b->layer,
                                                         sub->peer_sub);
    free(sub->adapter);
    free(sub);
}

static void bridge_get_posture(void *self,
                               ca_aether_security_posture_t *out) {
    ca_aether_security_bridge_t *b = (ca_aether_security_bridge_t *)self;
    if (!b || !out) return;
    ca_peer_security_posture_t p;
    memset(&p, 0, sizeof(p));
    ca_security_layer_service_get_posture(b->layer, &p);
    out->overall_threat_level =
        ca_aether_mapper_to_aether_threat_level(p.overall_threat_level);
    out->quarantined_node_count = p.quarantined_peer_count;
    out->monitored_node_count = p.monitored_peer_count;
    out->is_active = p.is_active;
    out->assessed_at_ms = p.generated_at_ms;
}

ca_aether_ai_security_layer_t ca_aether_security_bridge_as_layer(
    ca_aether_security_bridge_t *bridge) {
    ca_aether_ai_security_layer_t v;
    v.self = bridge;
    v.start = bridge_start;
    v.stop = bridge_stop;
    v.subscribe_to_directives = bridge_subscribe_directives;
    v.unsubscribe_directives = bridge_unsubscribe_directives;
    v.get_posture = bridge_get_posture;
    return v;
}

/* ===========================================================================
 * AetherIntelligenceAdapter — IAetherIntelligence over PeerIntelligenceService
 * =========================================================================== */

struct ca_aether_intelligence_adapter {
    ca_peer_intelligence_service_t *inner;    /* borrowed */
    ca_aether_intel_now_fn          now_fn;
    void                           *now_user;
};

ca_aether_intelligence_adapter_t *ca_aether_intelligence_adapter_create(
    ca_peer_intelligence_service_t *inner, ca_aether_intel_now_fn now_fn,
    void *now_user) {
    if (!inner) return NULL;
    ca_aether_intelligence_adapter_t *a =
        (ca_aether_intelligence_adapter_t *)calloc(1, sizeof(*a));
    if (!a) return NULL;
    a->inner = inner;
    a->now_fn = now_fn;
    a->now_user = now_user;
    return a;
}

void ca_aether_intelligence_adapter_destroy(
    ca_aether_intelligence_adapter_t *adapter) {
    free(adapter);
}

/* Steal an owned string out of a peer field into an aether field, replacing
 * the source with NULL so a later peer-destroy is a no-op on it. */
static char *steal(char **p) {
    char *s = *p;
    *p = NULL;
    return s;
}

static int intel_get_network_health(
    void *self, ca_aether_network_health_report_t *out) {
    ca_aether_intelligence_adapter_t *a =
        (ca_aether_intelligence_adapter_t *)self;
    if (!a || !out) return -1;
    ca_peer_network_health_report_t r;
    memset(&r, 0, sizeof(r));
    if (ca_peer_intelligence_service_get_network_health(a->inner, &r) != 0)
        return -1;
    out->overall_score = r.overall_score;
    out->trusted_node_count = r.trusted_peer_count;
    out->suspicious_node_count = r.suspicious_peer_count;
    out->summary = steal(&r.summary); /* move ownership */
    out->generated_at_ms = r.generated_at_ms;
    ca_peer_network_health_report_destroy(&r); /* frees any leftover (none) */
    return 0;
}

static int intel_assess_threat(void *self, const char *node_id,
                               ca_aether_threat_assessment_t *out) {
    ca_aether_intelligence_adapter_t *a =
        (ca_aether_intelligence_adapter_t *)self;
    if (!a || !out) return -1;
    int64_t now = a->now_fn ? a->now_fn(a->now_user) : 0;
    ca_peer_threat_assessment_t as;
    memset(&as, 0, sizeof(as));
    if (ca_peer_intelligence_service_assess_threat(a->inner, node_id, now, &as)
        != 0)
        return -1;
    out->node_id = steal(&as.node_id);
    out->threat_confidence = as.confidence;
    out->level = ca_aether_mapper_to_aether_threat_level(as.threat_level);
    out->indicators = as.indicators;      /* move array ownership */
    out->indicator_count = as.indicator_count;
    as.indicators = NULL;
    as.indicator_count = 0;
    out->assessed_at_ms = as.assessed_at_ms;
    ca_peer_threat_assessment_destroy(&as);
    return 0;
}

static int intel_get_routing_advice(void *self,
                                    const char *destination_node_id,
                                    ca_aether_routing_advice_t *out) {
    ca_aether_intelligence_adapter_t *a =
        (ca_aether_intelligence_adapter_t *)self;
    if (!a || !out) return -1;
    ca_peer_routing_advice_t r;
    memset(&r, 0, sizeof(r));
    if (ca_peer_intelligence_service_get_routing_advice(
            a->inner, destination_node_id, &r) != 0)
        return -1;
    out->destination_node_id = steal(&r.destination_node_id);
    out->recommended_path = r.recommended_path;
    out->recommended_path_count = r.recommended_path_count;
    r.recommended_path = NULL;
    r.recommended_path_count = 0;
    out->avoid_nodes = r.avoid_node_ids;   /* AvoidNodeIds -> AvoidNodes */
    out->avoid_node_count = r.avoid_node_count;
    r.avoid_node_ids = NULL;
    r.avoid_node_count = 0;
    out->confidence = r.confidence;
    out->reasoning = steal(&r.reasoning);
    out->generated_at_ms = r.generated_at_ms;
    ca_peer_routing_advice_destroy(&r);
    return 0;
}

/* Trust-score reader wrapper: adapts a peer reader into an Aether reader via
 * the ca_aether_trust_score_reader_create factory, mapping
 * PeerTrustScoreUpdate.NewScore -> CurrentScore, ChangedAt -> UpdatedAt. The
 * user context is simply the owned peer reader. */
static bool intel_reader_next(void *user, ca_aether_trust_score_update_t *out) {
    ca_trust_update_reader_t *peer_reader = (ca_trust_update_reader_t *)user;
    if (!peer_reader || !out) return false;
    ca_peer_trust_score_update_t u;
    memset(&u, 0, sizeof(u));
    if (!ca_trust_update_reader_next(peer_reader, &u)) return false;
    out->node_id = steal(&u.node_id);
    out->previous_score = u.previous_score;
    out->current_score = u.new_score;       /* NewScore -> CurrentScore */
    out->reason = steal(&u.reason);
    out->updated_at_ms = u.changed_at_ms;    /* ChangedAt -> UpdatedAt */
    ca_peer_trust_score_update_destroy(&u);  /* frees any leftover (none) */
    return true;
}

static void intel_reader_destroy(void *user) {
    ca_trust_update_reader_destroy((ca_trust_update_reader_t *)user);
}

static ca_aether_trust_score_reader_t *intel_stream_trust_scores(void *self) {
    ca_aether_intelligence_adapter_t *a =
        (ca_aether_intelligence_adapter_t *)self;
    if (!a) return NULL;
    ca_trust_update_reader_t *pr =
        ca_peer_intelligence_service_stream_trust_scores(a->inner);
    if (!pr) return NULL;
    ca_aether_trust_score_reader_t *r = ca_aether_trust_score_reader_create(
        pr, intel_reader_next, intel_reader_destroy);
    if (!r) { ca_trust_update_reader_destroy(pr); return NULL; }
    return r;
}

ca_aether_intelligence_t ca_aether_intelligence_adapter_as_intelligence(
    ca_aether_intelligence_adapter_t *adapter) {
    ca_aether_intelligence_t v;
    v.self = adapter;
    v.get_network_health = intel_get_network_health;
    v.assess_threat = intel_assess_threat;
    v.get_routing_advice = intel_get_routing_advice;
    v.stream_trust_scores = intel_stream_trust_scores;
    return v;
}
