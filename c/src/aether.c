/*
 * aether.c — CircleAI.Aether contracts (C11 port).
 *
 * Records + their helpers, an in-memory telemetry hub (fan-out publisher),
 * NullAetherTelemetry, a config-driven IAetherContext, and a deterministic
 * scripted IAuthChallenge. Ported 1:1 from CircleAI.Aether.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/aether.h"

#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <ctype.h>

/* --- small string / array helpers ---------------------------------------- */

static char *dup_or_null(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* strdup that turns NULL into an empty owned string (never returns NULL unless
 * OOM). Mirrors C# string fields that are non-null. */
static char *dup_or_empty(const char *s) {
    return dup_or_null(s ? s : "");
}

static bool is_null_or_whitespace(const char *s) {
    if (!s) return true;
    for (const char *p = s; *p; ++p)
        if (!isspace((unsigned char)*p)) return false;
    return true;
}

/* Deep-copy a NULL-terminated-count array of strings. Returns NULL when count
 * is 0. On OOM frees the partial copy and returns NULL with *ok=false. */
static char **dup_str_array(const char *const *src, size_t count, bool *ok) {
    if (ok) *ok = true;
    if (count == 0) return NULL;
    char **out = (char **)calloc(count, sizeof(*out));
    if (!out) { if (ok) *ok = false; return NULL; }
    for (size_t i = 0; i < count; ++i) {
        out[i] = dup_or_empty(src ? src[i] : NULL);
        if (!out[i]) {
            for (size_t j = 0; j < i; ++j) free(out[j]);
            free(out);
            if (ok) *ok = false;
            return NULL;
        }
    }
    return out;
}

static void free_str_array(char **arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) free(arr[i]);
    free(arr);
}

/* ===========================================================================
 * AetherNodeHealth / AetherNodeEvent
 * =========================================================================== */

bool ca_aether_node_health_is_valid(const ca_aether_node_health_t *h) {
    return h && h->trust_score >= 0.0 && h->trust_score <= 1.0;
}

ca_aether_node_event_t *ca_aether_node_event_create(
    const char *node_id, ca_aether_node_event_kind_t kind,
    ca_aether_node_health_t health, int64_t occurred_at_ms) {
    ca_aether_node_event_t *e = (ca_aether_node_event_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->node_id = dup_or_empty(node_id);
    if (!e->node_id) { free(e); return NULL; }
    e->kind = kind;
    e->health = health;
    e->occurred_at_ms = occurred_at_ms;
    return e;
}

void ca_aether_node_event_destroy(ca_aether_node_event_t *e) {
    if (!e) return;
    free(e->node_id);
    free(e);
}

ca_aether_node_event_t *ca_aether_node_event_copy(const ca_aether_node_event_t *e) {
    if (!e) return NULL;
    return ca_aether_node_event_create(e->node_id, e->kind, e->health,
                                       e->occurred_at_ms);
}

bool ca_aether_node_event_is_exit(const ca_aether_node_event_t *e) {
    return e && e->kind == CA_AETHER_NODE_LEFT;
}

/* ===========================================================================
 * AetherTransportEvent
 * =========================================================================== */

static ca_aether_transport_event_t *transport_event_create(
    const char *node_id, ca_aether_transport_event_kind_t kind,
    ca_aether_transport_kind_t transport, bool has_latency, int64_t latency_ms,
    bool has_packet_loss, double packet_loss_rate, int64_t occurred_at_ms) {
    ca_aether_transport_event_t *e =
        (ca_aether_transport_event_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->node_id = dup_or_empty(node_id);
    if (!e->node_id) { free(e); return NULL; }
    e->kind = kind;
    e->transport = transport;
    e->has_latency = has_latency;
    e->latency_ms = latency_ms;
    e->has_packet_loss = has_packet_loss;
    e->packet_loss_rate = packet_loss_rate;
    e->occurred_at_ms = occurred_at_ms;
    return e;
}

void ca_aether_transport_event_destroy(ca_aether_transport_event_t *e) {
    if (!e) return;
    free(e->node_id);
    free(e);
}

ca_aether_transport_event_t *ca_aether_transport_event_copy(
    const ca_aether_transport_event_t *e) {
    if (!e) return NULL;
    return transport_event_create(e->node_id, e->kind, e->transport,
                                  e->has_latency, e->latency_ms,
                                  e->has_packet_loss, e->packet_loss_rate,
                                  e->occurred_at_ms);
}

bool ca_aether_transport_event_exceeds_loss(
    const ca_aether_transport_event_t *e, double threshold) {
    return e && e->has_packet_loss && e->packet_loss_rate > threshold;
}

/* ===========================================================================
 * AetherRouteEvent
 * =========================================================================== */

ca_aether_route_event_t *ca_aether_route_event_create(
    const char *source_node_id, const char *destination_node_id,
    const char *const *path, size_t path_count,
    ca_aether_route_event_kind_t kind, const char *failure_reason,
    int64_t occurred_at_ms) {
    ca_aether_route_event_t *e =
        (ca_aether_route_event_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->source_node_id = dup_or_empty(source_node_id);
    e->destination_node_id = dup_or_empty(destination_node_id);
    if (!e->source_node_id || !e->destination_node_id) goto fail;
    bool ok = true;
    e->path = dup_str_array(path, path_count, &ok);
    if (!ok) goto fail;
    e->path_count = path_count;
    e->kind = kind;
    e->failure_reason = failure_reason ? dup_or_null(failure_reason) : NULL;
    if (failure_reason && !e->failure_reason) goto fail;
    e->occurred_at_ms = occurred_at_ms;
    return e;
fail:
    ca_aether_route_event_destroy(e);
    return NULL;
}

void ca_aether_route_event_destroy(ca_aether_route_event_t *e) {
    if (!e) return;
    free(e->source_node_id);
    free(e->destination_node_id);
    free_str_array(e->path, e->path_count);
    free(e->failure_reason);
    free(e);
}

ca_aether_route_event_t *ca_aether_route_event_copy(
    const ca_aether_route_event_t *e) {
    if (!e) return NULL;
    return ca_aether_route_event_create(
        e->source_node_id, e->destination_node_id,
        (const char *const *)e->path, e->path_count, e->kind,
        e->failure_reason, e->occurred_at_ms);
}

size_t ca_aether_route_event_hop_count(const ca_aether_route_event_t *e) {
    return e ? e->path_count : 0;
}

bool ca_aether_route_event_is_failed(const ca_aether_route_event_t *e) {
    return e && e->kind == CA_AETHER_ROUTE_FAILED;
}

/* ===========================================================================
 * AetherSecurityEvent
 * =========================================================================== */

static void free_metadata(ca_aether_metadata_pair_t *m, size_t n) {
    if (!m) return;
    for (size_t i = 0; i < n; ++i) { free(m[i].key); free(m[i].value); }
    free(m);
}

ca_aether_security_event_t *ca_aether_security_event_create(
    const char *node_id, ca_aether_security_event_kind_t kind,
    ca_aether_threat_level_t threat_level, const char *description,
    const ca_aether_metadata_pair_t *metadata, size_t metadata_count,
    int64_t occurred_at_ms) {
    ca_aether_security_event_t *e =
        (ca_aether_security_event_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->node_id = dup_or_empty(node_id);
    e->description = dup_or_empty(description);
    if (!e->node_id || !e->description) goto fail;
    if (metadata_count > 0) {
        e->metadata = (ca_aether_metadata_pair_t *)calloc(
            metadata_count, sizeof(*e->metadata));
        if (!e->metadata) goto fail;
        for (size_t i = 0; i < metadata_count; ++i) {
            e->metadata[i].key = dup_or_empty(metadata ? metadata[i].key : NULL);
            e->metadata[i].value =
                dup_or_empty(metadata ? metadata[i].value : NULL);
            if (!e->metadata[i].key || !e->metadata[i].value) {
                e->metadata_count = i + 1; /* free what we built */
                goto fail;
            }
        }
        e->metadata_count = metadata_count;
    }
    e->kind = kind;
    e->threat_level = threat_level;
    e->occurred_at_ms = occurred_at_ms;
    return e;
fail:
    ca_aether_security_event_destroy(e);
    return NULL;
}

void ca_aether_security_event_destroy(ca_aether_security_event_t *e) {
    if (!e) return;
    free(e->node_id);
    free(e->description);
    free_metadata(e->metadata, e->metadata_count);
    free(e);
}

ca_aether_security_event_t *ca_aether_security_event_copy(
    const ca_aether_security_event_t *e) {
    if (!e) return NULL;
    return ca_aether_security_event_create(
        e->node_id, e->kind, e->threat_level, e->description, e->metadata,
        e->metadata_count, e->occurred_at_ms);
}

bool ca_aether_security_event_is_high_severity(
    const ca_aether_security_event_t *e) {
    return e && (e->threat_level == CA_AETHER_THREAT_HIGH ||
                 e->threat_level == CA_AETHER_THREAT_CRITICAL);
}

const char *ca_aether_security_event_metadata(
    const ca_aether_security_event_t *e, const char *key) {
    if (!e || !key) return NULL;
    for (size_t i = 0; i < e->metadata_count; ++i)
        if (strcmp(e->metadata[i].key, key) == 0) return e->metadata[i].value;
    return NULL;
}

/* ===========================================================================
 * AetherNetworkEvent
 * =========================================================================== */

bool ca_aether_network_event_is_high_congestion(
    const ca_aether_network_event_t *e) {
    return e && e->congestion_level > 0.75;
}

/* ===========================================================================
 * In-memory telemetry hub (IAetherTelemetry fan-out publisher)
 * =========================================================================== */

/* A subscription is one observer + a live flag (live=false once unsubscribed;
 * kept so a snapshot taken mid-fan-out never touches freed memory). */
struct ca_aether_subscription {
    ca_aether_telemetry_observer_t observer;
    bool live;
};

struct ca_aether_telemetry_hub {
    ca_aether_subscription_t **subs; /* owned array of owned subs */
    size_t count;
    size_t cap;
};

ca_aether_telemetry_hub_t *ca_aether_telemetry_hub_create(void) {
    ca_aether_telemetry_hub_t *h =
        (ca_aether_telemetry_hub_t *)calloc(1, sizeof(*h));
    return h;
}

void ca_aether_telemetry_hub_destroy(ca_aether_telemetry_hub_t *hub) {
    if (!hub) return;
    for (size_t i = 0; i < hub->count; ++i) free(hub->subs[i]);
    free(hub->subs);
    free(hub);
}

static ca_aether_subscription_t *hub_subscribe(
    void *self, const ca_aether_telemetry_observer_t *observer) {
    ca_aether_telemetry_hub_t *hub = (ca_aether_telemetry_hub_t *)self;
    if (!hub || !observer) return NULL;
    if (hub->count == hub->cap) {
        size_t nc = hub->cap ? hub->cap * 2 : 4;
        ca_aether_subscription_t **ns = (ca_aether_subscription_t **)realloc(
            hub->subs, nc * sizeof(*ns));
        if (!ns) return NULL;
        hub->subs = ns;
        hub->cap = nc;
    }
    ca_aether_subscription_t *s =
        (ca_aether_subscription_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->observer = *observer;
    s->live = true;
    hub->subs[hub->count++] = s;
    return s;
}

static void hub_unsubscribe(void *self, ca_aether_subscription_t *sub) {
    ca_aether_telemetry_hub_t *hub = (ca_aether_telemetry_hub_t *)self;
    if (!hub || !sub) return;
    for (size_t i = 0; i < hub->count; ++i) {
        if (hub->subs[i] == sub) {
            sub->live = false;               /* neutralise any live snapshot */
            free(sub);
            hub->subs[i] = hub->subs[--hub->count];
            return;
        }
    }
}

ca_aether_telemetry_t ca_aether_telemetry_hub_as_telemetry(
    ca_aether_telemetry_hub_t *hub) {
    ca_aether_telemetry_t t;
    t.self = hub;
    t.subscribe = hub_subscribe;
    t.unsubscribe = hub_unsubscribe;
    return t;
}

int ca_aether_telemetry_hub_subscriber_count(
    const ca_aether_telemetry_hub_t *hub) {
    return hub ? (int)hub->count : 0;
}

/* Snapshot the live subscription pointers, then dispatch — a callback may
 * unsubscribe itself (or others) during the loop without corrupting iteration.
 * We check ->live per element because a snapshot entry could be freed by an
 * earlier callback in the same fan-out. */
#define HUB_FANOUT(cb_field, evt)                                              \
    do {                                                                       \
        if (!hub || hub->count == 0) return;                                   \
        size_t n = hub->count;                                                 \
        ca_aether_subscription_t **snap =                                      \
            (ca_aether_subscription_t **)malloc(n * sizeof(*snap));            \
        if (!snap) return;                                                     \
        for (size_t i = 0; i < n; ++i) snap[i] = hub->subs[i];                 \
        for (size_t i = 0; i < n; ++i) {                                       \
            ca_aether_subscription_t *s = snap[i];                             \
            if (s->live && s->observer.cb_field)                              \
                s->observer.cb_field(s->observer.self, (evt));                 \
        }                                                                      \
        free(snap);                                                            \
    } while (0)

void ca_aether_telemetry_hub_publish_node(
    ca_aether_telemetry_hub_t *hub, const ca_aether_node_event_t *e) {
    HUB_FANOUT(on_node_event, e);
}
void ca_aether_telemetry_hub_publish_transport(
    ca_aether_telemetry_hub_t *hub, const ca_aether_transport_event_t *e) {
    HUB_FANOUT(on_transport_event, e);
}
void ca_aether_telemetry_hub_publish_route(
    ca_aether_telemetry_hub_t *hub, const ca_aether_route_event_t *e) {
    HUB_FANOUT(on_route_event, e);
}
void ca_aether_telemetry_hub_publish_security(
    ca_aether_telemetry_hub_t *hub, const ca_aether_security_event_t *e) {
    HUB_FANOUT(on_security_event, e);
}
void ca_aether_telemetry_hub_publish_network(
    ca_aether_telemetry_hub_t *hub, const ca_aether_network_event_t *e) {
    HUB_FANOUT(on_network_event, e);
}

#undef HUB_FANOUT

/* --- NullAetherTelemetry ------------------------------------------------- */

/* A single shared no-op subscription token. Non-NULL so callers can treat a
 * returned handle as "subscribed"; unsubscribe is a no-op. */
static ca_aether_subscription_t g_null_sub = { { 0 }, false };

static ca_aether_subscription_t *null_subscribe(
    void *self, const ca_aether_telemetry_observer_t *observer) {
    (void)self;
    if (!observer) return NULL; /* ArgumentNullException.ThrowIfNull(observer) */
    return &g_null_sub;
}
static void null_unsubscribe(void *self, ca_aether_subscription_t *sub) {
    (void)self; (void)sub;
}

ca_aether_telemetry_t ca_null_aether_telemetry(void) {
    ca_aether_telemetry_t t;
    t.self = NULL;
    t.subscribe = null_subscribe;
    t.unsubscribe = null_unsubscribe;
    return t;
}

/* ===========================================================================
 * Version + IAetherContext
 * =========================================================================== */

int ca_aether_version_compare(ca_aether_version_t a, ca_aether_version_t b) {
    int ac[4] = { a.major, a.minor, a.build < 0 ? 0 : a.build,
                  a.revision < 0 ? 0 : a.revision };
    int bc[4] = { b.major, b.minor, b.build < 0 ? 0 : b.build,
                  b.revision < 0 ? 0 : b.revision };
    for (int i = 0; i < 4; ++i) {
        if (ac[i] < bc[i]) return -1;
        if (ac[i] > bc[i]) return 1;
    }
    return 0;
}

struct ca_aether_context_impl {
    ca_aether_install_level_t level;
    bool                has_runtime;
    ca_aether_version_t runtime;
    bool                has_minimum;
    ca_aether_version_t minimum;
    bool                enabled;
};

ca_aether_context_impl_t *ca_aether_context_impl_create(
    ca_aether_install_level_t level,
    bool has_runtime, ca_aether_version_t runtime,
    bool has_minimum, ca_aether_version_t minimum,
    bool enabled) {
    ca_aether_context_impl_t *c =
        (ca_aether_context_impl_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->level = level;
    c->has_runtime = has_runtime;
    c->runtime = runtime;
    c->has_minimum = has_minimum;
    c->minimum = minimum;
    c->enabled = enabled;
    return c;
}

void ca_aether_context_impl_destroy(ca_aether_context_impl_t *c) { free(c); }

void ca_aether_context_impl_set_enabled(ca_aether_context_impl_t *c,
                                        bool enabled) {
    if (c) c->enabled = enabled;
}

static ca_aether_install_level_t ctx_install_level(void *self) {
    return ((ca_aether_context_impl_t *)self)->level;
}
static bool ctx_is_available(void *self) {
    ca_aether_context_impl_t *c = (ca_aether_context_impl_t *)self;
    return c->level != CA_AETHER_INSTALL_NONE && c->enabled;
}
static bool ctx_runtime_version(void *self, ca_aether_version_t *out) {
    ca_aether_context_impl_t *c = (ca_aether_context_impl_t *)self;
    if (!c->has_runtime) return false;
    if (out) *out = c->runtime;
    return true;
}
static bool ctx_minimum_required(void *self, ca_aether_version_t *out) {
    ca_aether_context_impl_t *c = (ca_aether_context_impl_t *)self;
    if (!c->has_minimum) return false;
    if (out) *out = c->minimum;
    return true;
}
static bool ctx_is_sufficient(void *self) {
    ca_aether_context_impl_t *c = (ca_aether_context_impl_t *)self;
    if (!c->has_minimum) return true; /* Always true when MinimumRequired null */
    if (!c->has_runtime) return false;
    return ca_aether_version_compare(c->runtime, c->minimum) >= 0;
}
static bool ctx_requires_auth(void *self) {
    return ((ca_aether_context_impl_t *)self)->level == CA_AETHER_INSTALL_OS;
}
static bool ctx_is_enabled(void *self) {
    ca_aether_context_impl_t *c = (ca_aether_context_impl_t *)self;
    return c->level != CA_AETHER_INSTALL_NONE && c->enabled;
}

ca_aether_context_t ca_aether_context_impl_as_context(
    ca_aether_context_impl_t *c) {
    ca_aether_context_t v;
    v.self = c;
    v.install_level = ctx_install_level;
    v.is_available = ctx_is_available;
    v.runtime_version = ctx_runtime_version;
    v.minimum_required = ctx_minimum_required;
    v.is_sufficient = ctx_is_sufficient;
    v.requires_auth = ctx_requires_auth;
    v.is_enabled = ctx_is_enabled;
    return v;
}

/* ===========================================================================
 * Intelligence result records
 * =========================================================================== */

void ca_aether_network_health_report_destroy(
    ca_aether_network_health_report_t *r) {
    if (!r) return;
    free(r->summary);
    r->summary = NULL;
}
bool ca_aether_network_health_report_is_valid(
    const ca_aether_network_health_report_t *r) {
    return r && r->overall_score >= 0.0 && r->overall_score <= 1.0;
}

void ca_aether_threat_assessment_destroy(ca_aether_threat_assessment_t *a) {
    if (!a) return;
    free(a->node_id);
    free_str_array(a->indicators, a->indicator_count);
    a->node_id = NULL;
    a->indicators = NULL;
    a->indicator_count = 0;
}
bool ca_aether_threat_assessment_is_valid(
    const ca_aether_threat_assessment_t *a) {
    return a && a->threat_confidence >= 0.0 && a->threat_confidence <= 1.0;
}

void ca_aether_routing_advice_destroy(ca_aether_routing_advice_t *a) {
    if (!a) return;
    free(a->destination_node_id);
    free_str_array(a->recommended_path, a->recommended_path_count);
    free_str_array(a->avoid_nodes, a->avoid_node_count);
    free(a->reasoning);
    a->destination_node_id = NULL;
    a->recommended_path = NULL;
    a->recommended_path_count = 0;
    a->avoid_nodes = NULL;
    a->avoid_node_count = 0;
    a->reasoning = NULL;
}

void ca_aether_trust_score_update_destroy(ca_aether_trust_score_update_t *u) {
    if (!u) return;
    free(u->node_id);
    free(u->reason);
    u->node_id = NULL;
    u->reason = NULL;
}
bool ca_aether_trust_score_update_has_changed(
    const ca_aether_trust_score_update_t *u) {
    return u && fabs(u->current_score - u->previous_score) > 0.001;
}
bool ca_aether_trust_score_update_is_degraded(
    const ca_aether_trust_score_update_t *u) {
    return u && u->current_score < u->previous_score;
}

/* The trust-score reader is an opaque wrapper around caller-supplied callbacks
 * over a user context. The intelligence binding (aethernet_security.c) uses it
 * to wrap an underlying peer stream. */
struct ca_aether_trust_score_reader {
    void *user;
    bool (*next)(void *user, ca_aether_trust_score_update_t *out);
    void (*destroy)(void *user);
};

ca_aether_trust_score_reader_t *ca_aether_trust_score_reader_create(
    void *user,
    bool (*next)(void *user, ca_aether_trust_score_update_t *out),
    void (*destroy)(void *user)) {
    if (!next) return NULL;
    ca_aether_trust_score_reader_t *r =
        (ca_aether_trust_score_reader_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->user = user;
    r->next = next;
    r->destroy = destroy;
    return r;
}

void ca_aether_trust_score_reader_destroy(ca_aether_trust_score_reader_t *r) {
    if (!r) return;
    if (r->destroy) r->destroy(r->user);
    free(r);
}
bool ca_aether_trust_score_reader_next(
    ca_aether_trust_score_reader_t *r, ca_aether_trust_score_update_t *out) {
    if (!r || !r->next || !out) return false;
    return r->next(r->user, out);
}

/* ===========================================================================
 * SecurityDirective
 * =========================================================================== */

ca_aether_security_directive_t *ca_aether_security_directive_create(
    ca_aether_security_directive_kind_t kind, const char *target_node_id,
    bool has_trust_score_override, double trust_score_override,
    ca_aether_threat_level_t threat_level, const char *reason,
    bool has_duration, int64_t duration_ms, int64_t issued_at_ms) {
    ca_aether_security_directive_t *d =
        (ca_aether_security_directive_t *)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->kind = kind;
    d->target_node_id = target_node_id ? dup_or_null(target_node_id) : NULL;
    if (target_node_id && !d->target_node_id) { free(d); return NULL; }
    d->has_trust_score_override = has_trust_score_override;
    d->trust_score_override = trust_score_override;
    d->threat_level = threat_level;
    d->reason = dup_or_empty(reason);
    if (!d->reason) { free(d->target_node_id); free(d); return NULL; }
    d->has_duration = has_duration;
    d->duration_ms = duration_ms;
    d->issued_at_ms = issued_at_ms;
    return d;
}

void ca_aether_security_directive_destroy(ca_aether_security_directive_t *d) {
    if (!d) return;
    free(d->target_node_id);
    free(d->reason);
    free(d);
}

ca_aether_security_directive_t *ca_aether_security_directive_copy(
    const ca_aether_security_directive_t *d) {
    if (!d) return NULL;
    return ca_aether_security_directive_create(
        d->kind, d->target_node_id, d->has_trust_score_override,
        d->trust_score_override, d->threat_level, d->reason, d->has_duration,
        d->duration_ms, d->issued_at_ms);
}

bool ca_aether_security_directive_has_target(
    const ca_aether_security_directive_t *d) {
    return d && !is_null_or_whitespace(d->target_node_id);
}

bool ca_aether_security_directive_is_permanent(
    const ca_aether_security_directive_t *d) {
    return d && !d->has_duration;
}

/* ===========================================================================
 * AuthChallengeResult
 * =========================================================================== */

void ca_auth_challenge_result_destroy(ca_auth_challenge_result_t *r) {
    if (!r) return;
    free(r->failure_reason);
    r->failure_reason = NULL;
}

ca_auth_challenge_result_t *ca_auth_challenge_result_copy(
    const ca_auth_challenge_result_t *r) {
    if (!r) return NULL;
    ca_auth_challenge_result_t *out =
        (ca_auth_challenge_result_t *)calloc(1, sizeof(*out));
    if (!out) return NULL;
    out->succeeded = r->succeeded;
    out->method_used = r->method_used;
    out->completed_at_ms = r->completed_at_ms;
    if (r->failure_reason) {
        out->failure_reason = dup_or_null(r->failure_reason);
        if (!out->failure_reason) { free(out); return NULL; }
    }
    return out;
}

ca_auth_challenge_result_t ca_auth_challenge_result_success(
    ca_auth_method_t method, int64_t now_ms) {
    ca_auth_challenge_result_t r;
    r.succeeded = true;
    r.method_used = method;
    r.failure_reason = NULL;
    r.completed_at_ms = now_ms;
    return r;
}

ca_auth_challenge_result_t ca_auth_challenge_result_failure(
    ca_auth_method_t method, const char *reason, int64_t now_ms) {
    ca_auth_challenge_result_t r;
    r.succeeded = false;
    r.method_used = method;
    r.failure_reason = dup_or_empty(reason);
    r.completed_at_ms = now_ms;
    return r;
}

/* ===========================================================================
 * Scripted IAuthChallenge
 * =========================================================================== */

struct ca_scripted_auth_challenge {
    ca_auth_method_t available; /* strongest credential the device satisfies */
    int64_t          now_ms;    /* fixed clock */
};

ca_scripted_auth_challenge_t *ca_scripted_auth_challenge_create(
    ca_auth_method_t available_method, int64_t fixed_now_ms) {
    ca_scripted_auth_challenge_t *a =
        (ca_scripted_auth_challenge_t *)calloc(1, sizeof(*a));
    if (!a) return NULL;
    a->available = available_method;
    a->now_ms = fixed_now_ms;
    return a;
}

void ca_scripted_auth_challenge_destroy(ca_scripted_auth_challenge_t *a) {
    free(a);
}

void ca_scripted_auth_challenge_set_available(
    ca_scripted_auth_challenge_t *a, ca_auth_method_t available_method) {
    if (a) a->available = available_method;
}

/* Enforce the platform floor: effective minimum is at least
 * BiometricAndDeviceAdmin. Succeed with `available` when available >= min. */
static int scripted_challenge(void *self, ca_auth_challenge_reason_t reason,
                              bool has_minimum, ca_auth_method_t minimum_method,
                              const char *prompt,
                              ca_auth_challenge_result_t *out) {
    (void)reason; (void)prompt;
    ca_scripted_auth_challenge_t *a = (ca_scripted_auth_challenge_t *)self;
    if (!a || !out) return -1;
    ca_auth_method_t requested =
        has_minimum ? minimum_method : CA_AUTH_METHOD_BIOMETRIC_AND_DEVICE_ADMIN;
    ca_auth_method_t effective = requested;
    if ((int)effective < (int)CA_AUTH_METHOD_BIOMETRIC_AND_DEVICE_ADMIN)
        effective = CA_AUTH_METHOD_BIOMETRIC_AND_DEVICE_ADMIN;
    if ((int)a->available >= (int)effective)
        *out = ca_auth_challenge_result_success(a->available, a->now_ms);
    else
        *out = ca_auth_challenge_result_failure(
            effective, "insufficient authentication strength", a->now_ms);
    return 0;
}

static int scripted_request_os_toggle(void *self, bool enable,
                                      ca_auth_challenge_result_t *out) {
    (void)enable;
    /* Always requires BiometricAndDeviceAdmin at minimum. */
    return scripted_challenge(self, CA_AUTH_REASON_OS_LEVEL_TOGGLE, true,
                              CA_AUTH_METHOD_BIOMETRIC_AND_DEVICE_ADMIN,
                              "OS-level toggle", out);
}

ca_auth_challenge_t ca_scripted_auth_challenge_as_challenge(
    ca_scripted_auth_challenge_t *a) {
    ca_auth_challenge_t v;
    v.self = a;
    v.challenge = scripted_challenge;
    v.request_os_toggle = scripted_request_os_toggle;
    return v;
}
