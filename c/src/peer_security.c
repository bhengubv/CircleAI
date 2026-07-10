/*
 * peer_security.c — transport-agnostic peer security layer (C11 port).
 *
 * See peer_security.h. Ports PeerSecurityTypes, ThreatDetector, SecurityOptions,
 * NodeTrustRegistry, DirectivePublisher, SecurityLayerService (IPeerSecurityLayer),
 * and PeerIntelligenceService (IPeerIntelligence).
 *
 * In-memory, deterministic, single-threaded. Linear arrays throughout (no
 * hashtable). All owning string fields are strdup'd with matching frees.
 */

#include "circle_ai/peer_security.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---------------------------------------------------------------------------
 * libc helpers
 * --------------------------------------------------------------------------- */

static char *ps_strdup(const char *s) {
    if (!s) s = "";
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

static void ps_free_str_array(char **arr, size_t n) {
    if (!arr) return;
    for (size_t i = 0; i < n; i++) free(arr[i]);
    free(arr);
}

/* ===========================================================================
 * Record free/copy helpers
 * =========================================================================== */

static bool ps_event_fill(ca_peer_security_event_t *e, const char *node_id,
                          ca_peer_security_event_kind_t kind,
                          ca_peer_threat_level_t level, const char *description,
                          const char *transport_id, int64_t occurred_at_ms) {
    e->node_id      = ps_strdup(node_id);
    e->description  = ps_strdup(description);
    e->transport_id = ps_strdup(transport_id);
    e->kind         = kind;
    e->threat_level = level;
    e->occurred_at_ms = occurred_at_ms;
    return e->node_id && e->description && e->transport_id;
}

static void ps_event_clear(ca_peer_security_event_t *e) {
    if (!e) return;
    free(e->node_id);
    free(e->description);
    free(e->transport_id);
    e->node_id = e->description = e->transport_id = NULL;
}

ca_peer_security_event_t *ca_peer_security_event_create(
    const char *node_id, ca_peer_security_event_kind_t kind,
    ca_peer_threat_level_t threat_level, const char *description,
    const char *transport_id, int64_t occurred_at_ms) {
    ca_peer_security_event_t *e =
        (ca_peer_security_event_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    if (!ps_event_fill(e, node_id, kind, threat_level, description,
                       transport_id, occurred_at_ms)) {
        ps_event_clear(e);
        free(e);
        return NULL;
    }
    return e;
}

void ca_peer_security_event_destroy(ca_peer_security_event_t *e) {
    if (!e) return;
    ps_event_clear(e);
    free(e);
}

ca_peer_security_event_t *ca_peer_security_event_copy(
    const ca_peer_security_event_t *e) {
    if (!e) return NULL;
    return ca_peer_security_event_create(e->node_id, e->kind, e->threat_level,
                                         e->description, e->transport_id,
                                         e->occurred_at_ms);
}

void ca_peer_directive_destroy(ca_peer_directive_t *d) {
    if (!d) return;
    free(d->target_node_id);
    free(d->reason);
    free(d);
}

ca_peer_directive_t *ca_peer_directive_copy(const ca_peer_directive_t *d) {
    if (!d) return NULL;
    ca_peer_directive_t *c = (ca_peer_directive_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    *c = *d;
    c->target_node_id = ps_strdup(d->target_node_id);
    c->reason         = ps_strdup(d->reason);
    if (!c->target_node_id || !c->reason) { ca_peer_directive_destroy(c); return NULL; }
    return c;
}

/* Fields-only: the update is filled into a caller-owned (typically stack)
 * struct by ca_trust_update_reader_next, so we free the owning fields but
 * never the struct itself. Mirrors the report/assessment/advice family. */
void ca_peer_trust_score_update_destroy(ca_peer_trust_score_update_t *u) {
    if (!u) return;
    free(u->node_id);
    free(u->reason);
    u->node_id = NULL;
    u->reason  = NULL;
}

void ca_peer_network_health_report_destroy(ca_peer_network_health_report_t *r) {
    if (!r) return;
    free(r->summary);
    r->summary = NULL;
}

void ca_peer_threat_assessment_destroy(ca_peer_threat_assessment_t *a) {
    if (!a) return;
    free(a->node_id);
    ps_free_str_array(a->indicators, a->indicator_count);
    a->node_id = NULL;
    a->indicators = NULL;
    a->indicator_count = 0;
}

void ca_peer_routing_advice_destroy(ca_peer_routing_advice_t *a) {
    if (!a) return;
    free(a->destination_node_id);
    ps_free_str_array(a->recommended_path, a->recommended_path_count);
    ps_free_str_array(a->avoid_node_ids, a->avoid_node_count);
    free(a->reasoning);
    a->destination_node_id = NULL;
    a->recommended_path = NULL;
    a->avoid_node_ids = NULL;
    a->reasoning = NULL;
}

/* ===========================================================================
 * SecurityOptions
 * =========================================================================== */

void ca_security_options_init_defaults(ca_security_options_t *opts) {
    if (!opts) return;
    opts->elevate_monitoring_threshold = 0.75;
    opts->avoid_node_threshold         = 0.50;
    opts->quarantine_threshold         = 0.25;
    opts->recovery_rate_per_second     = 0.001;
    opts->event_window_ms              = 5 * 60 * 1000; /* 5 minutes */
    opts->max_events_per_node          = 100;
    opts->initial_trust_score          = 1.0;
}

/* ===========================================================================
 * ThreatDetector
 * =========================================================================== */

static double ps_base_weight(ca_peer_security_event_kind_t kind) {
    switch (kind) {
        case CA_PEER_EVENT_AUTH_ATTEMPT:       return 0.05;
        case CA_PEER_EVENT_ROUTING_ANOMALY:    return 0.10;
        case CA_PEER_EVENT_BEHAVIOUR_CHANGE:   return 0.08;
        case CA_PEER_EVENT_ENCRYPTION_EVENT:   return 0.06;
        case CA_PEER_EVENT_INTRUSION_SIGNAL:   return 0.15;
        case CA_PEER_EVENT_PRIVILEGE_ATTEMPT:  return 0.12;
        case CA_PEER_EVENT_CONNECTION_ANOMALY: return 0.07;
        case CA_PEER_EVENT_DATA_EXFILTRATION:  return 0.14;
        case CA_PEER_EVENT_DENIAL_OF_SERVICE:  return 0.13;
        default:                               return 0.05;
    }
}

static double ps_threat_multiplier(ca_peer_threat_level_t level) {
    switch (level) {
        case CA_PEER_THREAT_NONE:     return 0.0;
        case CA_PEER_THREAT_LOW:      return 0.5;
        case CA_PEER_THREAT_MEDIUM:   return 1.0;
        case CA_PEER_THREAT_HIGH:     return 2.0;
        case CA_PEER_THREAT_CRITICAL: return 3.0;
        default:                      return 1.0;
    }
}

double ca_threat_detector_compute_degradation(const ca_peer_security_event_t *e) {
    if (!e) return 0.0;
    return ps_base_weight(e->kind) * ps_threat_multiplier(e->threat_level);
}

size_t ca_threat_detector_detect_indicators(
    const ca_peer_security_event_t *const *events, size_t event_count,
    int64_t window_ms, int64_t now_ms, char **out, size_t out_cap) {
    if (!out || out_cap == 0) return 0;
    int64_t cutoff = now_ms - window_ms;

    /* Windowed subset (mirrors .Where(e => e.OccurredAt >= cutoff)). */
    size_t auth_count = 0;
    bool has_intrusion = false, has_high_sev = false;
    bool has_privilege = false, has_exfil = false;
    /* distinct-kind tracking over the 10 kinds */
    bool seen_kind[10];
    memset(seen_kind, 0, sizeof(seen_kind));
    size_t windowed = 0;

    for (size_t i = 0; i < event_count; i++) {
        const ca_peer_security_event_t *e = events[i];
        if (!e || e->occurred_at_ms < cutoff) continue;
        windowed++;
        if (e->kind == CA_PEER_EVENT_AUTH_ATTEMPT) auth_count++;
        if (e->kind == CA_PEER_EVENT_INTRUSION_SIGNAL) has_intrusion = true;
        if (e->kind == CA_PEER_EVENT_PRIVILEGE_ATTEMPT) has_privilege = true;
        if (e->kind == CA_PEER_EVENT_DATA_EXFILTRATION) has_exfil = true;
        if (e->threat_level == CA_PEER_THREAT_HIGH ||
            e->threat_level == CA_PEER_THREAT_CRITICAL) has_high_sev = true;
        if ((int)e->kind >= 0 && (int)e->kind < 10) seen_kind[e->kind] = true;
    }

    if (windowed == 0) return 0;

    size_t distinct = 0;
    for (int k = 0; k < 10; k++) if (seen_kind[k]) distinct++;

    size_t n = 0;
    /* Order MUST match the C# to keep list-equality tests stable. */
    if (auth_count >= 3 && n < out_cap) out[n++] = ps_strdup("repeated-auth-attempts");
    if (has_intrusion && n < out_cap)   out[n++] = ps_strdup("intrusion-signal-detected");
    if (has_high_sev && n < out_cap)    out[n++] = ps_strdup("high-severity-event");
    if (distinct >= 3 && n < out_cap)   out[n++] = ps_strdup("multi-vector-activity");
    if (has_privilege && n < out_cap)   out[n++] = ps_strdup("privilege-escalation-attempt");
    if (has_exfil && n < out_cap)       out[n++] = ps_strdup("data-exfiltration-signal");

    return n;
}

/* ===========================================================================
 * NodeTrustRegistry
 * =========================================================================== */

typedef struct {
    char                      *node_id;      /* owned */
    double                     trust_score;
    int64_t                    last_updated_ms;
    ca_peer_security_event_t **events;       /* owned array of owned events */
    size_t                     event_count;
    size_t                     event_cap;
} ps_node_entry_t;

/* Unbounded update log for the trust-score stream. */
typedef struct {
    ca_peer_trust_score_update_t *items; /* owned array; strings owned */
    size_t                        count;
    size_t                        cap;
} ps_update_log_t;

struct ca_node_trust_registry {
    ca_security_options_t opts;
    ps_node_entry_t      *nodes;   /* linear array */
    size_t                node_count;
    size_t                node_cap;
    ps_update_log_t       updates;
};

struct ca_trust_update_reader {
    const ca_node_trust_registry_t *reg;
    size_t                          cursor;
};

ca_node_trust_registry_t *ca_node_trust_registry_create(
    const ca_security_options_t *options) {
    ca_node_trust_registry_t *reg =
        (ca_node_trust_registry_t *)calloc(1, sizeof(*reg));
    if (!reg) return NULL;
    if (options) reg->opts = *options;
    else ca_security_options_init_defaults(&reg->opts);
    return reg;
}

static void ps_entry_clear(ps_node_entry_t *e) {
    free(e->node_id);
    for (size_t i = 0; i < e->event_count; i++)
        ca_peer_security_event_destroy(e->events[i]);
    free(e->events);
}

void ca_node_trust_registry_destroy(ca_node_trust_registry_t *reg) {
    if (!reg) return;
    for (size_t i = 0; i < reg->node_count; i++) ps_entry_clear(&reg->nodes[i]);
    free(reg->nodes);
    for (size_t i = 0; i < reg->updates.count; i++) {
        free(reg->updates.items[i].node_id);
        free(reg->updates.items[i].reason);
    }
    free(reg->updates.items);
    free(reg);
}

static ps_node_entry_t *ps_find_node(const ca_node_trust_registry_t *reg,
                                     const char *node_id) {
    if (!node_id) return NULL;
    for (size_t i = 0; i < reg->node_count; i++)
        if (strcmp(reg->nodes[i].node_id, node_id) == 0)
            return &reg->nodes[i];
    return NULL;
}

static ps_node_entry_t *ps_get_or_create_entry(ca_node_trust_registry_t *reg,
                                                const char *node_id) {
    ps_node_entry_t *e = ps_find_node(reg, node_id);
    if (e) return e;
    if (reg->node_count == reg->node_cap) {
        size_t ncap = reg->node_cap == 0 ? 8 : reg->node_cap * 2;
        ps_node_entry_t *nn = (ps_node_entry_t *)realloc(
            reg->nodes, ncap * sizeof(*nn));
        if (!nn) return NULL;
        reg->nodes = nn;
        reg->node_cap = ncap;
    }
    ps_node_entry_t *ne = &reg->nodes[reg->node_count];
    memset(ne, 0, sizeof(*ne));
    ne->node_id = ps_strdup(node_id);
    if (!ne->node_id) return NULL;
    ne->trust_score = reg->opts.initial_trust_score;
    ne->last_updated_ms = 0;
    reg->node_count++;
    return ne;
}

double ca_node_trust_registry_get_or_create(ca_node_trust_registry_t *reg,
                                             const char *node_id) {
    if (!reg) return 0.0;
    ps_node_entry_t *e = ps_get_or_create_entry(reg, node_id);
    return e ? e->trust_score : reg->opts.initial_trust_score;
}

double ca_node_trust_registry_get_trust_score(
    const ca_node_trust_registry_t *reg, const char *node_id) {
    if (!reg) return 0.0;
    ps_node_entry_t *e = ps_find_node(reg, node_id);
    return e ? e->trust_score : reg->opts.initial_trust_score;
}

size_t ca_node_trust_registry_all_node_ids(
    const ca_node_trust_registry_t *reg, char ***out_ids) {
    if (out_ids) *out_ids = NULL;
    if (!reg || reg->node_count == 0) return 0;
    char **ids = (char **)malloc(reg->node_count * sizeof(*ids));
    if (!ids) return 0;
    for (size_t i = 0; i < reg->node_count; i++) {
        ids[i] = ps_strdup(reg->nodes[i].node_id);
        if (!ids[i]) { ps_free_str_array(ids, i); return 0; }
    }
    if (out_ids) *out_ids = ids;
    else ps_free_str_array(ids, reg->node_count);
    return reg->node_count;
}

static double ps_clamp(double v, double lo, double hi) {
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

static bool ps_update_log_push(ps_update_log_t *log, const char *node_id,
                               double previous, double current,
                               const char *reason, int64_t at_ms) {
    if (log->count == log->cap) {
        size_t ncap = log->cap == 0 ? 16 : log->cap * 2;
        ca_peer_trust_score_update_t *ni =
            (ca_peer_trust_score_update_t *)realloc(
                log->items, ncap * sizeof(*ni));
        if (!ni) return false;
        log->items = ni;
        log->cap = ncap;
    }
    ca_peer_trust_score_update_t *u = &log->items[log->count];
    u->node_id = ps_strdup(node_id);
    u->reason  = ps_strdup(reason);
    if (!u->node_id || !u->reason) { free(u->node_id); free(u->reason); return false; }
    u->previous_score = previous;
    u->new_score      = current;
    u->changed_at_ms  = at_ms;
    log->count++;
    return true;
}

int ca_node_trust_registry_apply_degradation(
    ca_node_trust_registry_t *reg, const ca_peer_security_event_t *security_event,
    double degradation_amount, double *out_previous, double *out_current) {
    if (!reg || !security_event) return -1;
    ps_node_entry_t *e = ps_get_or_create_entry(reg, security_event->node_id);
    if (!e) return -1;

    double previous = e->trust_score;
    e->trust_score  = ps_clamp(previous - degradation_amount, 0.0, 1.0);
    e->last_updated_ms = security_event->occurred_at_ms;

    /* Append to bounded per-node history (deep copy; oldest dropped first). */
    ca_peer_security_event_t *copy = ca_peer_security_event_copy(security_event);
    if (!copy) return -1;
    if (e->event_count == e->event_cap) {
        size_t ncap = e->event_cap == 0 ? 8 : e->event_cap * 2;
        ca_peer_security_event_t **nev = (ca_peer_security_event_t **)realloc(
            e->events, ncap * sizeof(*nev));
        if (!nev) { ca_peer_security_event_destroy(copy); return -1; }
        e->events = nev;
        e->event_cap = ncap;
    }
    e->events[e->event_count++] = copy;
    while ((int)e->event_count > reg->opts.max_events_per_node) {
        ca_peer_security_event_destroy(e->events[0]);
        memmove(&e->events[0], &e->events[1],
                (e->event_count - 1) * sizeof(*e->events));
        e->event_count--;
    }

    double current = e->trust_score;
    double d = current - previous;
    if (d < 0) d = -d;
    if (d > 0.0001)
        ps_update_log_push(&reg->updates, e->node_id, previous, current,
                           security_event->description,
                           security_event->occurred_at_ms);

    if (out_previous) *out_previous = previous;
    if (out_current)  *out_current  = current;
    return 0;
}

void ca_node_trust_registry_apply_recovery(ca_node_trust_registry_t *reg,
                                           int64_t elapsed_ms) {
    if (!reg) return;
    double amount = reg->opts.recovery_rate_per_second * ((double)elapsed_ms / 1000.0);
    if (amount <= 0) return;
    /* now used for the passive-recovery update timestamp. Deterministic-ish;
     * the C# uses DateTimeOffset.UtcNow — we mirror with a real clock. */
    extern int64_t ps_now_ms_impl(void);
    int64_t now = ps_now_ms_impl();
    for (size_t i = 0; i < reg->node_count; i++) {
        ps_node_entry_t *e = &reg->nodes[i];
        if (e->trust_score >= 1.0) continue;
        double previous = e->trust_score;
        double next = previous + amount;
        if (next > 1.0) next = 1.0;
        e->trust_score = next;
        e->last_updated_ms = now;
        ps_update_log_push(&reg->updates, e->node_id, previous, next,
                           "passive-recovery", now);
    }
}

size_t ca_node_trust_registry_get_recent_events(
    const ca_node_trust_registry_t *reg, const char *node_id, int64_t now_ms,
    ca_peer_security_event_t ***out_events) {
    if (out_events) *out_events = NULL;
    if (!reg) return 0;
    ps_node_entry_t *e = ps_find_node(reg, node_id);
    if (!e || e->event_count == 0) return 0;

    int64_t cutoff = now_ms - reg->opts.event_window_ms;
    /* First pass: count. */
    size_t match = 0;
    for (size_t i = 0; i < e->event_count; i++)
        if (e->events[i]->occurred_at_ms >= cutoff) match++;
    if (match == 0) return 0;

    ca_peer_security_event_t **arr =
        (ca_peer_security_event_t **)malloc(match * sizeof(*arr));
    if (!arr) return 0;
    size_t j = 0;
    for (size_t i = 0; i < e->event_count && j < match; i++) {
        if (e->events[i]->occurred_at_ms < cutoff) continue;
        arr[j] = ca_peer_security_event_copy(e->events[i]);
        if (!arr[j]) {
            for (size_t k = 0; k < j; k++) ca_peer_security_event_destroy(arr[k]);
            free(arr);
            return 0;
        }
        j++;
    }
    if (out_events) *out_events = arr;
    else {
        for (size_t k = 0; k < match; k++) ca_peer_security_event_destroy(arr[k]);
        free(arr);
    }
    return match;
}

ca_trust_update_reader_t *ca_node_trust_registry_open_reader(
    ca_node_trust_registry_t *reg) {
    if (!reg) return NULL;
    ca_trust_update_reader_t *r =
        (ca_trust_update_reader_t *)calloc(1, sizeof(*r));
    if (!r) return NULL;
    r->reg = reg;
    r->cursor = 0;
    return r;
}

void ca_trust_update_reader_destroy(ca_trust_update_reader_t *r) { free(r); }

bool ca_trust_update_reader_next(ca_trust_update_reader_t *r,
                                 ca_peer_trust_score_update_t *out) {
    if (!r || !out || !r->reg) return false;
    if (r->cursor >= r->reg->updates.count) return false;
    const ca_peer_trust_score_update_t *src = &r->reg->updates.items[r->cursor++];
    out->node_id = ps_strdup(src->node_id);
    out->reason  = ps_strdup(src->reason);
    out->previous_score = src->previous_score;
    out->new_score      = src->new_score;
    out->changed_at_ms  = src->changed_at_ms;
    return true;
}

/* Shared clock impl referenced above and by services. */
#if defined(_WIN32)
#  include <windows.h>
int64_t ps_now_ms_impl(void) {
    FILETIME ft;
    GetSystemTimeAsFileTime(&ft);
    uint64_t t = ((uint64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
    t -= 116444736000000000ULL;
    return (int64_t)(t / 10000ULL);
}
#else
#  include <time.h>
int64_t ps_now_ms_impl(void) {
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    return (int64_t)ts.tv_sec * 1000LL + (int64_t)(ts.tv_nsec / 1000000L);
}
#endif

/* ===========================================================================
 * DirectivePublisher
 * =========================================================================== */

typedef struct {
    ca_peer_directive_consumer_fn consumer;
    void                         *user;
    bool                          active; /* false once unsubscribed */
} ps_sub_slot_t;

struct ca_directive_subscription {
    ca_directive_publisher_t *pub;
    size_t                    slot_index;
};

struct ca_directive_publisher {
    ps_sub_slot_t *slots; /* linear; unsubscribed slots marked inactive */
    size_t         count;
    size_t         cap;
};

ca_directive_publisher_t *ca_directive_publisher_create(void) {
    return (ca_directive_publisher_t *)calloc(1, sizeof(ca_directive_publisher_t));
}

void ca_directive_publisher_destroy(ca_directive_publisher_t *pub) {
    if (!pub) return;
    free(pub->slots);
    free(pub);
}

ca_directive_subscription_t *ca_directive_publisher_subscribe(
    ca_directive_publisher_t *pub, ca_peer_directive_consumer_fn consumer,
    void *user) {
    if (!pub || !consumer) return NULL;
    if (pub->count == pub->cap) {
        size_t ncap = pub->cap == 0 ? 4 : pub->cap * 2;
        ps_sub_slot_t *ns = (ps_sub_slot_t *)realloc(pub->slots, ncap * sizeof(*ns));
        if (!ns) return NULL;
        pub->slots = ns;
        pub->cap = ncap;
    }
    ca_directive_subscription_t *handle =
        (ca_directive_subscription_t *)calloc(1, sizeof(*handle));
    if (!handle) return NULL;
    pub->slots[pub->count].consumer = consumer;
    pub->slots[pub->count].user     = user;
    pub->slots[pub->count].active   = true;
    handle->pub = pub;
    handle->slot_index = pub->count;
    pub->count++;
    return handle;
}

void ca_directive_publisher_unsubscribe(ca_directive_publisher_t *pub,
                                        ca_directive_subscription_t *sub) {
    if (!pub || !sub) return;
    if (sub->pub == pub && sub->slot_index < pub->count)
        pub->slots[sub->slot_index].active = false; /* idempotent */
    free(sub);
}

void ca_directive_publisher_publish(ca_directive_publisher_t *pub,
                                    const ca_peer_directive_t *directive) {
    if (!pub || !directive) return;
    /* Snapshot the active consumers, then fire outside any mutation window so a
     * callback that unsubscribes during dispatch cannot corrupt iteration. */
    size_t n = pub->count;
    if (n == 0) return;
    ps_sub_slot_t *snap = (ps_sub_slot_t *)malloc(n * sizeof(*snap));
    if (!snap) return;
    memcpy(snap, pub->slots, n * sizeof(*snap));
    for (size_t i = 0; i < n; i++)
        if (snap[i].active && snap[i].consumer)
            snap[i].consumer(snap[i].user, directive);
    free(snap);
}

int ca_directive_publisher_subscriber_count(const ca_directive_publisher_t *pub) {
    if (!pub) return 0;
    int c = 0;
    for (size_t i = 0; i < pub->count; i++) if (pub->slots[i].active) c++;
    return c;
}

/* ===========================================================================
 * Shared threat-level bucketing (AISecurityLayerService.ScoreToThreatLevel /
 * PeerIntelligenceService level switch — identical thresholds).
 * =========================================================================== */

static ca_peer_threat_level_t ps_score_to_threat_level(double score) {
    if (score <= 0.25) return CA_PEER_THREAT_CRITICAL;
    if (score <= 0.50) return CA_PEER_THREAT_HIGH;
    if (score <= 0.75) return CA_PEER_THREAT_MEDIUM;
    if (score <= 0.90) return CA_PEER_THREAT_LOW;
    return CA_PEER_THREAT_NONE;
}

/* ===========================================================================
 * SecurityLayerService (IPeerSecurityLayer)
 * =========================================================================== */

struct ca_security_layer_service {
    ca_node_trust_registry_t *registry;   /* borrowed */
    ca_security_options_t     options;    /* copy */
    ca_directive_publisher_t *publisher;  /* borrowed */
    bool                      active;
};

ca_security_layer_service_t *ca_security_layer_service_create(
    ca_node_trust_registry_t *registry, const ca_security_options_t *options,
    ca_directive_publisher_t *publisher) {
    if (!registry || !publisher) return NULL;
    ca_security_layer_service_t *svc =
        (ca_security_layer_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->registry  = registry;
    svc->publisher = publisher;
    if (options) svc->options = *options;
    else ca_security_options_init_defaults(&svc->options);
    return svc;
}

void ca_security_layer_service_destroy(ca_security_layer_service_t *svc) {
    free(svc);
}

void ca_security_layer_service_start(ca_security_layer_service_t *svc) {
    if (svc) svc->active = true;
}
void ca_security_layer_service_stop(ca_security_layer_service_t *svc) {
    if (svc) svc->active = false;
}
bool ca_security_layer_service_is_active(const ca_security_layer_service_t *svc) {
    return svc ? svc->active : false;
}

static void ps_issue_directive(ca_security_layer_service_t *svc,
                               ca_peer_directive_kind_t kind, const char *node_id,
                               double trust_score, const char *reason,
                               ca_peer_threat_level_t threat_level) {
    ca_peer_directive_t d;
    memset(&d, 0, sizeof(d));
    d.kind           = kind;
    d.target_node_id = (char *)node_id;   /* borrowed for the publish call */
    d.trust_score    = trust_score;
    d.threat_level   = threat_level;
    d.reason         = (char *)reason;    /* borrowed for the publish call */
    d.has_duration   = false;             /* permanent until ReleaseNode */
    d.duration_ms    = 0;
    d.issued_at_ms   = ps_now_ms_impl();
    ca_directive_publisher_publish(svc->publisher, &d);
    /* Do NOT free d.target_node_id / d.reason — they are borrowed. */
}

static void ps_evaluate_thresholds(ca_security_layer_service_t *svc,
                                   const char *node_id, double previous,
                                   double current, const char *reason) {
    const ca_security_options_t *o = &svc->options;
    if (previous > o->quarantine_threshold && current <= o->quarantine_threshold) {
        ps_issue_directive(svc, CA_PEER_DIRECTIVE_QUARANTINE_NODE, node_id,
                           current, reason, CA_PEER_THREAT_CRITICAL);
        return;
    }
    if (previous > o->avoid_node_threshold && current <= o->avoid_node_threshold) {
        ps_issue_directive(svc, CA_PEER_DIRECTIVE_AVOID_NODE, node_id,
                           current, reason, CA_PEER_THREAT_HIGH);
        return;
    }
    if (previous > o->elevate_monitoring_threshold &&
        current <= o->elevate_monitoring_threshold) {
        ps_issue_directive(svc, CA_PEER_DIRECTIVE_ELEVATE_MONITORING, node_id,
                           current, reason, CA_PEER_THREAT_MEDIUM);
    }
}

void ca_security_layer_service_handle_peer_event(
    ca_security_layer_service_t *svc, const ca_peer_security_event_t *e) {
    if (!svc || !e) return;
    double degradation = ca_threat_detector_compute_degradation(e);
    if (degradation <= 0) return; /* None — no trust impact */
    double previous = 0, current = 0;
    if (ca_node_trust_registry_apply_degradation(svc->registry, e, degradation,
                                                 &previous, &current) != 0)
        return;
    ps_evaluate_thresholds(svc, e->node_id, previous, current, e->description);
}

void ca_security_layer_service_handle_peer_left(
    ca_security_layer_service_t *svc, const char *node_id) {
    (void)svc; (void)node_id; /* trust entry retained; no directive */
}

ca_directive_subscription_t *ca_security_layer_service_subscribe_directives(
    ca_security_layer_service_t *svc, ca_peer_directive_consumer_fn consumer,
    void *user) {
    if (!svc) return NULL;
    return ca_directive_publisher_subscribe(svc->publisher, consumer, user);
}

void ca_security_layer_service_unsubscribe_directives(
    ca_security_layer_service_t *svc, ca_directive_subscription_t *sub) {
    if (!svc || !sub) return;
    ca_directive_publisher_unsubscribe(svc->publisher, sub);
}

void ca_security_layer_service_get_posture(
    const ca_security_layer_service_t *svc, ca_peer_security_posture_t *out) {
    if (!out) return;
    memset(out, 0, sizeof(*out));
    if (!svc) return;
    const ca_security_options_t *o = &svc->options;

    char **ids = NULL;
    size_t n = ca_node_trust_registry_all_node_ids(svc->registry, &ids);

    int quarantined = 0, monitored = 0;
    double worst = 1.0;
    for (size_t i = 0; i < n; i++) {
        double s = ca_node_trust_registry_get_trust_score(svc->registry, ids[i]);
        if (s <= o->quarantine_threshold) quarantined++;
        if (s <= o->elevate_monitoring_threshold && s > o->quarantine_threshold)
            monitored++;
        if (i == 0 || s < worst) worst = s;
    }
    if (n == 0) worst = 1.0;
    ps_free_str_array(ids, n);

    out->overall_threat_level   = ps_score_to_threat_level(worst);
    out->quarantined_peer_count = quarantined;
    out->monitored_peer_count   = monitored;
    out->is_active              = svc->active;
    out->generated_at_ms        = ps_now_ms_impl();
}

void ca_security_layer_service_recover_tick(ca_security_layer_service_t *svc,
                                            int64_t elapsed_ms) {
    if (!svc) return;
    ca_node_trust_registry_apply_recovery(svc->registry, elapsed_ms);
}

/* ===========================================================================
 * PeerIntelligenceService (IPeerIntelligence)
 * =========================================================================== */

struct ca_peer_intelligence_service {
    ca_node_trust_registry_t *registry; /* borrowed */
    ca_security_options_t     options;  /* copy */
};

ca_peer_intelligence_service_t *ca_peer_intelligence_service_create(
    ca_node_trust_registry_t *registry, const ca_security_options_t *options) {
    if (!registry) return NULL;
    ca_peer_intelligence_service_t *svc =
        (ca_peer_intelligence_service_t *)calloc(1, sizeof(*svc));
    if (!svc) return NULL;
    svc->registry = registry;
    if (options) svc->options = *options;
    else ca_security_options_init_defaults(&svc->options);
    return svc;
}

void ca_peer_intelligence_service_destroy(ca_peer_intelligence_service_t *svc) {
    free(svc);
}

int ca_peer_intelligence_service_get_network_health(
    const ca_peer_intelligence_service_t *svc,
    ca_peer_network_health_report_t *out) {
    if (!svc || !out) return -1;
    memset(out, 0, sizeof(*out));
    const ca_security_options_t *o = &svc->options;

    char **ids = NULL;
    size_t n = ca_node_trust_registry_all_node_ids(svc->registry, &ids);

    if (n == 0) {
        out->overall_score         = 1.0;
        out->trusted_peer_count    = 0;
        out->suspicious_peer_count = 0;
        out->summary               = ps_strdup("No peers observed.");
        out->generated_at_ms       = ps_now_ms_impl();
        return out->summary ? 0 : -1;
    }

    double sum = 0;
    int trusted = 0, suspicious = 0;
    for (size_t i = 0; i < n; i++) {
        double s = ca_node_trust_registry_get_trust_score(svc->registry, ids[i]);
        sum += s;
        if (s > o->avoid_node_threshold) trusted++;
        if (s <= o->elevate_monitoring_threshold) suspicious++;
    }
    double overall = sum / (double)n;
    ps_free_str_array(ids, n);

    const char *summary;
    if (overall > 0.90)      summary = "Network health is excellent.";
    else if (overall > 0.75) summary = "Network health is good; minor anomalies detected.";
    else if (overall > 0.50) summary = "Network health is degraded; elevated monitoring active.";
    else if (overall > 0.25) summary = "Network health is poor; routing around compromised peers.";
    else                     summary = "Network health is critical; quarantine directives in effect.";

    out->overall_score         = overall;
    out->trusted_peer_count    = trusted;
    out->suspicious_peer_count = suspicious;
    out->summary               = ps_strdup(summary);
    out->generated_at_ms       = ps_now_ms_impl();
    return out->summary ? 0 : -1;
}

int ca_peer_intelligence_service_assess_threat(
    const ca_peer_intelligence_service_t *svc, const char *node_id,
    int64_t now_ms, ca_peer_threat_assessment_t *out) {
    if (!svc || !out) return -1;
    memset(out, 0, sizeof(*out));

    double score = ca_node_trust_registry_get_trust_score(svc->registry, node_id);
    double deficit = 1.0 - score;

    ca_peer_security_event_t **events = NULL;
    size_t ev_count = ca_node_trust_registry_get_recent_events(
        svc->registry, node_id, now_ms, &events);

    char *ind[6];
    size_t ind_count = ca_threat_detector_detect_indicators(
        (const ca_peer_security_event_t *const *)events, ev_count,
        svc->options.event_window_ms, now_ms, ind, 6);

    for (size_t i = 0; i < ev_count; i++) ca_peer_security_event_destroy(events[i]);
    free(events);

    ca_peer_threat_level_t level = ps_score_to_threat_level(score);

    double confidence = deficit + (double)ind_count * 0.1;
    if (confidence > 1.0) confidence = 1.0;

    out->node_id      = ps_strdup(node_id);
    if (!out->node_id) { for (size_t i=0;i<ind_count;i++) free(ind[i]); return -1; }
    out->confidence   = confidence;
    out->threat_level = level;
    out->assessed_at_ms = now_ms;

    if (ind_count > 0) {
        out->indicators = (char **)malloc(ind_count * sizeof(char *));
        if (!out->indicators) {
            for (size_t i = 0; i < ind_count; i++) free(ind[i]);
            free(out->node_id); out->node_id = NULL;
            return -1;
        }
        for (size_t i = 0; i < ind_count; i++) out->indicators[i] = ind[i];
        out->indicator_count = ind_count;
    }
    return 0;
}

int ca_peer_intelligence_service_get_routing_advice(
    const ca_peer_intelligence_service_t *svc, const char *destination_node_id,
    ca_peer_routing_advice_t *out) {
    if (!svc || !out) return -1;
    memset(out, 0, sizeof(*out));
    const ca_security_options_t *o = &svc->options;

    char **ids = NULL;
    size_t n = ca_node_trust_registry_all_node_ids(svc->registry, &ids);

    /* Avoid list: every node at or below the avoid threshold. */
    char **avoid = NULL;
    size_t avoid_count = 0;
    if (n > 0) {
        avoid = (char **)malloc(n * sizeof(*avoid));
        if (!avoid) { ps_free_str_array(ids, n); return -1; }
        for (size_t i = 0; i < n; i++) {
            double s = ca_node_trust_registry_get_trust_score(svc->registry, ids[i]);
            if (s <= o->avoid_node_threshold) {
                avoid[avoid_count] = ps_strdup(ids[i]);
                if (!avoid[avoid_count]) {
                    ps_free_str_array(avoid, avoid_count);
                    ps_free_str_array(ids, n);
                    return -1;
                }
                avoid_count++;
            }
        }
    }
    ps_free_str_array(ids, n);

    double dest_score = ca_node_trust_registry_get_trust_score(
        svc->registry, destination_node_id);

    /* Recommended path is direct only when destination is above avoid-thresh. */
    char **recommended = NULL;
    size_t rec_count = 0;
    if (dest_score > o->avoid_node_threshold) {
        recommended = (char **)malloc(sizeof(char *));
        if (!recommended) { ps_free_str_array(avoid, avoid_count); return -1; }
        recommended[0] = ps_strdup(destination_node_id);
        if (!recommended[0]) {
            free(recommended);
            ps_free_str_array(avoid, avoid_count);
            return -1;
        }
        rec_count = 1;
    }

    /* Reasoning string (F2 = 2 decimal places for the trusted branch). */
    char reasoning[256 + 128];
    const char *dst = destination_node_id ? destination_node_id : "";
    if (dest_score > 0.75)
        snprintf(reasoning, sizeof(reasoning),
                 "Direct path to %s is trusted (score %.2f).", dst, dest_score);
    else if (dest_score > 0.50)
        snprintf(reasoning, sizeof(reasoning),
                 "Destination %s is under monitoring; routing with caution.", dst);
    else if (dest_score > 0.25)
        snprintf(reasoning, sizeof(reasoning),
                 "Destination %s has degraded trust; avoid recommended.", dst);
    else
        snprintf(reasoning, sizeof(reasoning),
                 "Destination %s is quarantined; no safe path available.", dst);

    out->destination_node_id    = ps_strdup(destination_node_id);
    out->recommended_path       = recommended;
    out->recommended_path_count = rec_count;
    out->avoid_node_ids         = avoid;
    out->avoid_node_count       = avoid_count;
    out->confidence             = dest_score;
    out->reasoning              = ps_strdup(reasoning);
    out->generated_at_ms        = ps_now_ms_impl();
    if (!out->destination_node_id || !out->reasoning) {
        ca_peer_routing_advice_destroy(out);
        return -1;
    }
    return 0;
}

ca_trust_update_reader_t *ca_peer_intelligence_service_stream_trust_scores(
    ca_peer_intelligence_service_t *svc) {
    if (!svc) return NULL;
    return ca_node_trust_registry_open_reader(svc->registry);
}

/* ===========================================================================
 * IPeerSecurityEventFeed convenience pump
 * =========================================================================== */

static void ps_feed_layer_handler(void *user, const ca_peer_security_event_t *e) {
    ca_security_layer_service_handle_peer_event(
        (ca_security_layer_service_t *)user, e);
}

void ca_peer_security_event_feed_pump_into_layer(
    const ca_peer_security_event_feed_t *feed,
    ca_security_layer_service_t *layer) {
    if (!feed || !feed->start || !layer) return;
    feed->start(feed->self, ps_feed_layer_handler, layer);
}
