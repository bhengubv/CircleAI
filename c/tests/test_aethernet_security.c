/*
 * test_aethernet_security.c — CircleAI.Security.AetherNet bindings
 * (aethernet_security.h).
 *
 * Verifies:
 *   AetherMapper              : enum translation both directions
 *   MeshDirectiveStore        : OnDirective sink, untargeted ignored, Release
 *                               lifts all, IsBlocked (Avoid/Quarantine) with
 *                               lazy expiry, GetActiveDirectives, tracked count
 *   MeshSecurityGate          : Decide / Enforce (allow + block message)
 *   AetherSecurityBridge      : end-to-end — Aether security event -> peer
 *                               layer degrades trust -> directive fires ->
 *                               adapted back to an Aether consumer; node-exit;
 *                               posture mapping; directive unsubscribe
 *   AetherIntelligenceAdapter : network health / threat assessment / routing
 *                               advice mapping + trust-score stream (NewScore
 *                               -> CurrentScore)
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

#define T0 1700000000000LL

static bool approx(double a, double b) { return fabs(a - b) < 1e-9; }

/* Fixed clock for the directive store. */
static int64_t g_clock = T0;
static int64_t store_clock(void *user) { (void)user; return g_clock; }

/* ---------------------------------------------------------------------------
 * AetherMapper
 * --------------------------------------------------------------------------- */
static void test_mapper(void) {
    assert(ca_aether_mapper_to_peer_event_kind(CA_AETHER_SEC_NODE_AUTH_ATTEMPT) ==
           CA_PEER_EVENT_AUTH_ATTEMPT);
    assert(ca_aether_mapper_to_peer_event_kind(CA_AETHER_SEC_INTRUSION_SIGNAL) ==
           CA_PEER_EVENT_INTRUSION_SIGNAL);
    assert(ca_aether_mapper_to_peer_event_kind(CA_AETHER_SEC_PRIVILEGE_ATTEMPT) ==
           CA_PEER_EVENT_PRIVILEGE_ATTEMPT);

    assert(ca_aether_mapper_to_peer_threat_level(CA_AETHER_THREAT_CRITICAL) ==
           CA_PEER_THREAT_CRITICAL);
    assert(ca_aether_mapper_to_aether_threat_level(CA_PEER_THREAT_HIGH) ==
           CA_AETHER_THREAT_HIGH);
    assert(ca_aether_mapper_to_aether_threat_level(CA_PEER_THREAT_NONE) ==
           CA_AETHER_THREAT_NONE);

    assert(ca_aether_mapper_to_security_directive_kind(
               CA_PEER_DIRECTIVE_QUARANTINE_NODE) ==
           CA_AETHER_DIRECTIVE_QUARANTINE_NODE);
    assert(ca_aether_mapper_to_security_directive_kind(
               CA_PEER_DIRECTIVE_RELEASE_NODE) ==
           CA_AETHER_DIRECTIVE_RELEASE_NODE);
    assert(ca_aether_mapper_to_security_directive_kind(
               CA_PEER_DIRECTIVE_ELEVATE_MONITORING) ==
           CA_AETHER_DIRECTIVE_ELEVATE_MONITORING);
}

/* Helper to feed a directive into the store. */
static void feed(ca_mesh_directive_store_t *s,
                 ca_aether_security_directive_kind_t kind, const char *node,
                 const char *reason, bool has_dur, int64_t dur, int64_t issued) {
    ca_aether_security_directive_t *d = ca_aether_security_directive_create(
        kind, node, false, 0, CA_AETHER_THREAT_HIGH, reason, has_dur, dur,
        issued);
    assert(d);
    ca_mesh_directive_store_on_directive(s, d);
    ca_aether_security_directive_destroy(d);
}

/* ---------------------------------------------------------------------------
 * MeshDirectiveStore
 * --------------------------------------------------------------------------- */
static void test_directive_store(void) {
    g_clock = T0;
    ca_mesh_directive_store_t *s =
        ca_mesh_directive_store_create(store_clock, NULL);
    assert(s && ca_mesh_directive_store_tracked_node_count(s) == 0);

    /* Untargeted directive is ignored. */
    feed(s, CA_AETHER_DIRECTIVE_ELEVATE_MONITORING, "  ", "x", false, 0, T0);
    assert(ca_mesh_directive_store_tracked_node_count(s) == 0);

    /* Quarantine 'bad' -> blocked. */
    feed(s, CA_AETHER_DIRECTIVE_QUARANTINE_NODE, "bad", "isolated", false, 0, T0);
    assert(ca_mesh_directive_store_tracked_node_count(s) == 1);
    char *reason = NULL;
    assert(ca_mesh_directive_store_is_blocked(s, "bad", &reason));
    assert(reason && strcmp(reason, "isolated") == 0);
    free(reason);

    /* ElevateMonitoring is NOT a block kind. */
    feed(s, CA_AETHER_DIRECTIVE_ELEVATE_MONITORING, "watched", "eyes", false, 0,
         T0);
    reason = NULL;
    assert(!ca_mesh_directive_store_is_blocked(s, "watched", &reason));
    assert(reason == NULL);

    /* Avoid is a block kind; the most RECENT block reason wins. */
    feed(s, CA_AETHER_DIRECTIVE_AVOID_NODE, "bad", "soft-block", false, 0,
         T0 + 100);
    assert(ca_mesh_directive_store_is_blocked(s, "bad", &reason));
    assert(reason && strcmp(reason, "soft-block") == 0); /* newer IssuedAt */
    free(reason);

    /* GetActiveDirectives: 'bad' has quarantine + avoid = 2. */
    ca_aether_security_directive_t **active = NULL;
    size_t n = ca_mesh_directive_store_get_active(s, "bad", &active);
    assert(n == 2);
    for (size_t i = 0; i < n; i++)
        ca_aether_security_directive_destroy(active[i]);
    free(active);

    /* Release lifts every directive for 'bad'. */
    feed(s, CA_AETHER_DIRECTIVE_RELEASE_NODE, "bad", "cleared", false, 0,
         T0 + 200);
    reason = NULL;
    assert(!ca_mesh_directive_store_is_blocked(s, "bad", &reason));
    assert(reason == NULL);
    n = ca_mesh_directive_store_get_active(s, "bad", &active);
    assert(n == 0 && active == NULL);

    /* Unknown / whitespace nodes. */
    assert(!ca_mesh_directive_store_is_blocked(s, "ghost", &reason));
    assert(!ca_mesh_directive_store_is_blocked(s, "   ", &reason));

    ca_mesh_directive_store_destroy(s);
}

/* Lazy expiry: a durationed block expires once now passes IssuedAt+Duration. */
static void test_directive_expiry(void) {
    g_clock = T0;
    ca_mesh_directive_store_t *s =
        ca_mesh_directive_store_create(store_clock, NULL);

    feed(s, CA_AETHER_DIRECTIVE_QUARANTINE_NODE, "temp", "10s block", true,
         10000, T0);
    char *reason = NULL;
    assert(ca_mesh_directive_store_is_blocked(s, "temp", &reason));
    free(reason);

    /* Not yet expired at +9999ms. */
    g_clock = T0 + 9999;
    assert(ca_mesh_directive_store_is_blocked(s, "temp", &reason));
    free(reason);

    /* Expired at +10000ms (IssuedAt + Duration <= now). Bucket swept + dropped. */
    g_clock = T0 + 10000;
    reason = NULL;
    assert(!ca_mesh_directive_store_is_blocked(s, "temp", &reason));
    assert(reason == NULL);
    assert(ca_mesh_directive_store_tracked_node_count(s) == 0);

    ca_mesh_directive_store_destroy(s);
}

/* ---------------------------------------------------------------------------
 * MeshSecurityGate
 * --------------------------------------------------------------------------- */
static void test_gate(void) {
    g_clock = T0;
    ca_mesh_directive_store_t *s =
        ca_mesh_directive_store_create(store_clock, NULL);
    ca_mesh_security_gate_t *gate = ca_mesh_security_gate_create(s);
    assert(gate);

    /* Allowed for unknown id. */
    ca_mesh_gate_decision_t dec;
    assert(ca_mesh_security_gate_decide(gate, "alice", &dec) == 0);
    assert(!dec.is_blocked && strcmp(dec.reason, "") == 0);
    ca_mesh_gate_decision_free(&dec);

    /* Enforce allows (returns true, no message). */
    char *reason = NULL, *msg = NULL;
    assert(ca_mesh_security_gate_enforce(gate, "alice", &reason, &msg));
    assert(reason == NULL && msg == NULL);

    /* Block alice via quarantine. */
    feed(s, CA_AETHER_DIRECTIVE_QUARANTINE_NODE, "alice", "abuse", false, 0, T0);
    assert(ca_mesh_security_gate_decide(gate, "alice", &dec) == 0);
    assert(dec.is_blocked && strcmp(dec.reason, "abuse") == 0);
    ca_mesh_gate_decision_free(&dec);

    /* Enforce now blocks (returns false) with reason + full message. */
    assert(!ca_mesh_security_gate_enforce(gate, "alice", &reason, &msg));
    assert(reason && strcmp(reason, "abuse") == 0);
    assert(msg && strcmp(msg, "Mesh has blocked 'alice': abuse") == 0);
    free(reason);
    free(msg);

    /* Whitespace id => allowed. */
    assert(ca_mesh_security_gate_decide(gate, "   ", &dec) == 0);
    assert(!dec.is_blocked);
    ca_mesh_gate_decision_free(&dec);

    ca_mesh_security_gate_destroy(gate);
    ca_mesh_directive_store_destroy(s);
}

/* ---------------------------------------------------------------------------
 * AetherSecurityBridge — end to end
 * --------------------------------------------------------------------------- */
typedef struct {
    int                                 count;
    ca_aether_security_directive_kind_t last_kind;
    char                                last_node[32];
    double                              last_trust;
    bool                                last_has_trust;
} dir_capture_t;

static void capture_directive(void *self,
                              const ca_aether_security_directive_t *d) {
    dir_capture_t *c = (dir_capture_t *)self;
    c->count++;
    c->last_kind = d->kind;
    c->last_has_trust = d->has_trust_score_override;
    c->last_trust = d->trust_score_override;
    if (d->target_node_id) {
        strncpy(c->last_node, d->target_node_id, sizeof(c->last_node) - 1);
        c->last_node[sizeof(c->last_node) - 1] = '\0';
    }
}

static void test_bridge(void) {
    /* Build the peer engine: registry + publisher + layer + options. */
    ca_security_options_t opts;
    ca_security_options_init_defaults(&opts);
    ca_node_trust_registry_t *reg = ca_node_trust_registry_create(&opts);
    ca_directive_publisher_t *pub = ca_directive_publisher_create();
    ca_security_layer_service_t *layer =
        ca_security_layer_service_create(reg, &opts, pub);
    assert(reg && pub && layer);

    /* The bridge (IAISecurityLayer). */
    ca_aether_security_bridge_t *bridge =
        ca_aether_security_bridge_create(layer);
    assert(bridge);
    ca_aether_ai_security_layer_t sl =
        ca_aether_security_bridge_as_layer(bridge);

    /* Subscribe an Aether directive consumer (through the bridge adapter). */
    dir_capture_t cap = { 0 };
    ca_aether_security_directive_consumer_t consumer;
    consumer.self = &cap;
    consumer.on_directive = capture_directive;
    ca_aether_directive_subscription_t *dsub =
        sl.subscribe_to_directives(sl.self, &consumer);
    assert(dsub);

    /* Wire the bridge to an in-memory Aether telemetry hub, then start. */
    ca_aether_telemetry_hub_t *hub = ca_aether_telemetry_hub_create();
    ca_aether_telemetry_t tel = ca_aether_telemetry_hub_as_telemetry(hub);
    sl.start(sl.self, &tel);
    assert(ca_aether_telemetry_hub_subscriber_count(hub) == 1);

    /* Publish an intrusion+critical event -> degradation 0.45: 1.0 -> 0.55,
     * crosses elevate(0.75) -> ElevateMonitoring directive to our consumer. */
    ca_aether_security_event_t *e1 = ca_aether_security_event_create(
        "attacker", CA_AETHER_SEC_INTRUSION_SIGNAL, CA_AETHER_THREAT_CRITICAL,
        "probe", NULL, 0, T0);
    ca_aether_telemetry_hub_publish_security(hub, e1);
    assert(cap.count == 1);
    assert(cap.last_kind == CA_AETHER_DIRECTIVE_ELEVATE_MONITORING);
    assert(strcmp(cap.last_node, "attacker") == 0);
    assert(cap.last_has_trust && approx(cap.last_trust, 0.55));

    /* A second identical event -> 0.55 -> 0.10, crosses quarantine(0.25);
     * most-severe wins -> QuarantineNode. */
    ca_aether_telemetry_hub_publish_security(hub, e1);
    assert(cap.count == 2);
    assert(cap.last_kind == CA_AETHER_DIRECTIVE_QUARANTINE_NODE);
    assert(approx(cap.last_trust, 0.10));
    ca_aether_security_event_destroy(e1);

    /* Posture: attacker at 0.10 <= quarantine threshold => quarantined = 1. */
    ca_aether_security_posture_t posture;
    sl.get_posture(sl.self, &posture);
    assert(posture.is_active);
    assert(posture.quarantined_node_count == 1);

    /* Node-exit event -> HandlePeerLeft (no directive, no crash). */
    ca_aether_node_health_t h = { 0.1, false, 0, 5 };
    ca_aether_node_event_t *ne =
        ca_aether_node_event_create("attacker", CA_AETHER_NODE_LEFT, h, T0);
    int before = cap.count;
    ca_aether_telemetry_hub_publish_security(hub, NULL); /* tolerate NULL */
    ca_aether_telemetry_hub_publish_node(hub, ne);
    assert(cap.count == before); /* peer-left issues nothing */
    ca_aether_node_event_destroy(ne);

    /* Stop unsubscribes from telemetry + stops the layer. */
    sl.stop(sl.self);
    assert(ca_aether_telemetry_hub_subscriber_count(hub) == 0);
    ca_aether_security_posture_t after_stop;
    sl.get_posture(sl.self, &after_stop);
    assert(!after_stop.is_active);

    /* Unsubscribe the directive consumer; a subsequent publish (after restart)
     * must NOT reach it. */
    sl.unsubscribe_directives(sl.self, dsub);
    assert(ca_directive_publisher_subscriber_count(pub) == 0);

    ca_aether_telemetry_hub_destroy(hub);
    ca_aether_security_bridge_destroy(bridge);
    ca_security_layer_service_destroy(layer);
    ca_directive_publisher_destroy(pub);
    ca_node_trust_registry_destroy(reg);
}

/* ---------------------------------------------------------------------------
 * AetherIntelligenceAdapter
 * --------------------------------------------------------------------------- */
static int64_t intel_now(void *user) { (void)user; return T0; }

static void test_intelligence_adapter(void) {
    ca_security_options_t opts;
    ca_security_options_init_defaults(&opts);
    ca_node_trust_registry_t *reg = ca_node_trust_registry_create(&opts);
    ca_peer_intelligence_service_t *inner =
        ca_peer_intelligence_service_create(reg, &opts);
    assert(reg && inner);

    ca_aether_intelligence_adapter_t *adapter =
        ca_aether_intelligence_adapter_create(inner, intel_now, NULL);
    assert(adapter);
    ca_aether_intelligence_t ai =
        ca_aether_intelligence_adapter_as_intelligence(adapter);

    /* Seed some trust so health/assessment have content. */
    ca_node_trust_registry_get_or_create(reg, "trusted");
    /* Degrade a node to make it suspicious. */
    ca_peer_security_event_t *ev = ca_peer_security_event_create(
        "suspect", CA_PEER_EVENT_INTRUSION_SIGNAL, CA_PEER_THREAT_CRITICAL,
        "bad", "aether", T0);
    double prev = 0, cur = 0;
    ca_node_trust_registry_apply_degradation(reg, ev, 0.45, &prev, &cur);
    ca_peer_security_event_destroy(ev);

    /* Network health maps peer -> aether field names. */
    ca_aether_network_health_report_t nh;
    memset(&nh, 0, sizeof(nh));
    assert(ai.get_network_health(ai.self, &nh) == 0);
    assert(nh.summary != NULL);
    assert(ca_aether_network_health_report_is_valid(&nh));
    assert(nh.trusted_node_count >= 0 && nh.suspicious_node_count >= 0);
    ca_aether_network_health_report_destroy(&nh);

    /* Threat assessment for the degraded node. */
    ca_aether_threat_assessment_t ta;
    memset(&ta, 0, sizeof(ta));
    assert(ai.assess_threat(ai.self, "suspect", &ta) == 0);
    assert(strcmp(ta.node_id, "suspect") == 0);
    assert(ca_aether_threat_assessment_is_valid(&ta));
    ca_aether_threat_assessment_destroy(&ta);

    /* Unknown node => zero-confidence assessment (still valid, node echoed). */
    memset(&ta, 0, sizeof(ta));
    assert(ai.assess_threat(ai.self, "who", &ta) == 0);
    assert(strcmp(ta.node_id, "who") == 0);
    ca_aether_threat_assessment_destroy(&ta);

    /* Routing advice maps AvoidNodeIds -> AvoidNodes. */
    ca_aether_routing_advice_t ra;
    memset(&ra, 0, sizeof(ra));
    assert(ai.get_routing_advice(ai.self, "dest", &ra) == 0);
    assert(strcmp(ra.destination_node_id, "dest") == 0);
    assert(ra.reasoning != NULL);
    ca_aether_routing_advice_destroy(&ra);

    /* Trust-score stream: the degradation above published an update; the reader
     * replays it, mapped NewScore -> CurrentScore. */
    ca_aether_trust_score_reader_t *reader = ai.stream_trust_scores(ai.self);
    assert(reader);
    ca_aether_trust_score_update_t u;
    int got = 0;
    while (ca_aether_trust_score_reader_next(reader, &u)) {
        got++;
        if (strcmp(u.node_id, "suspect") == 0) {
            assert(approx(u.previous_score, 1.0));
            assert(approx(u.current_score, 0.55)); /* 1.0 - 0.45 */
            assert(ca_aether_trust_score_update_has_changed(&u));
            assert(ca_aether_trust_score_update_is_degraded(&u));
        }
        ca_aether_trust_score_update_destroy(&u);
    }
    assert(got >= 1);
    ca_aether_trust_score_reader_destroy(reader);

    ca_aether_intelligence_adapter_destroy(adapter);
    ca_peer_intelligence_service_destroy(inner);
    ca_node_trust_registry_destroy(reg);
}

int main(void) {
    test_mapper();
    test_directive_store();
    test_directive_expiry();
    test_gate();
    test_bridge();
    test_intelligence_adapter();
    return 0;
}
