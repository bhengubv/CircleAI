/*
 * test_peer_security.c — transport-agnostic peer security layer (peer_security.h).
 *
 * Verifies:
 *   ThreatDetector          : degradation weights x multipliers, indicator tags
 *   SecurityOptions         : documented defaults
 *   NodeTrustRegistry       : degrade+clamp, event history, recovery, stream replay
 *   DirectivePublisher      : fan-out, unsubscribe, unsubscribe-during-dispatch
 *   SecurityLayerService    : threshold directives (elevate/avoid/quarantine),
 *                             most-severe-wins, posture, active flag, recovery tick
 *   PeerIntelligenceService : network health, threat assessment, routing advice,
 *                             trust-score stream
 *   IPeerSecurityEventFeed  : pump adapter into the layer
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

static bool approx(double a, double b) { return fabs(a - b) < 1e-9; }

/* Fixed base timestamp so window math is deterministic. */
#define T0 1000000000000LL /* arbitrary epoch ms */

/* ---------------------------------------------------------------------------
 * ThreatDetector
 * --------------------------------------------------------------------------- */
static void test_threat_detector(void) {
    /* Degradation = BaseWeight(kind) * ThreatMultiplier(level). */
    ca_peer_security_event_t *e = ca_peer_security_event_create(
        "n1", CA_PEER_EVENT_INTRUSION_SIGNAL, CA_PEER_THREAT_HIGH,
        "probe", "wifi", T0);
    assert(e);
    /* 0.15 * 2.0 = 0.30 */
    assert(approx(ca_threat_detector_compute_degradation(e), 0.30));
    ca_peer_security_event_destroy(e);

    /* None -> 0 regardless of kind. */
    ca_peer_security_event_t *none = ca_peer_security_event_create(
        "n1", CA_PEER_EVENT_DATA_EXFILTRATION, CA_PEER_THREAT_NONE, "x", "ble", T0);
    assert(approx(ca_threat_detector_compute_degradation(none), 0.0));
    ca_peer_security_event_destroy(none);

    /* Critical multiplier 3.0: auth 0.05 * 3 = 0.15. */
    ca_peer_security_event_t *auth = ca_peer_security_event_create(
        "n1", CA_PEER_EVENT_AUTH_ATTEMPT, CA_PEER_THREAT_CRITICAL, "x", "http", T0);
    assert(approx(ca_threat_detector_compute_degradation(auth), 0.15));
    ca_peer_security_event_destroy(auth);

    /* Indicators: 3 auth attempts -> repeated-auth-attempts; plus intrusion and
     * a high-severity event; 3 distinct kinds -> multi-vector-activity. */
    ca_peer_security_event_t *evs[5];
    evs[0] = ca_peer_security_event_create("n", CA_PEER_EVENT_AUTH_ATTEMPT,
                                           CA_PEER_THREAT_LOW, "a", "wifi", T0);
    evs[1] = ca_peer_security_event_create("n", CA_PEER_EVENT_AUTH_ATTEMPT,
                                           CA_PEER_THREAT_LOW, "a", "wifi", T0);
    evs[2] = ca_peer_security_event_create("n", CA_PEER_EVENT_AUTH_ATTEMPT,
                                           CA_PEER_THREAT_LOW, "a", "wifi", T0);
    evs[3] = ca_peer_security_event_create("n", CA_PEER_EVENT_INTRUSION_SIGNAL,
                                           CA_PEER_THREAT_CRITICAL, "i", "wifi", T0);
    evs[4] = ca_peer_security_event_create("n", CA_PEER_EVENT_DATA_EXFILTRATION,
                                           CA_PEER_THREAT_MEDIUM, "x", "wifi", T0);
    char *out[6];
    size_t n = ca_threat_detector_detect_indicators(
        (const ca_peer_security_event_t *const *)evs, 5,
        300000LL, T0 + 1000LL, out, 6);
    /* Expected set in C# order: repeated-auth, intrusion, high-severity,
     * multi-vector (4 distinct kinds? no: auth,intrusion,exfil = 3), data-exfil.
     * Privilege not present. */
    assert(n == 5);
    assert(strcmp(out[0], "repeated-auth-attempts") == 0);
    assert(strcmp(out[1], "intrusion-signal-detected") == 0);
    assert(strcmp(out[2], "high-severity-event") == 0);
    assert(strcmp(out[3], "multi-vector-activity") == 0);
    assert(strcmp(out[4], "data-exfiltration-signal") == 0);
    for (size_t i = 0; i < n; i++) free(out[i]);
    for (size_t i = 0; i < 5; i++) ca_peer_security_event_destroy(evs[i]);

    /* Events outside the window are ignored -> empty. */
    ca_peer_security_event_t *old = ca_peer_security_event_create(
        "n", CA_PEER_EVENT_INTRUSION_SIGNAL, CA_PEER_THREAT_HIGH, "i", "wifi", T0);
    char *out2[6];
    size_t n2 = ca_threat_detector_detect_indicators(
        (const ca_peer_security_event_t *const *)&old, 1,
        300000LL, T0 + 400000LL, out2, 6); /* now well past the 5-min window */
    assert(n2 == 0);
    ca_peer_security_event_destroy(old);

    printf("  threat detector: OK\n");
}

/* ---------------------------------------------------------------------------
 * SecurityOptions defaults
 * --------------------------------------------------------------------------- */
static void test_options(void) {
    ca_security_options_t o;
    ca_security_options_init_defaults(&o);
    assert(approx(o.elevate_monitoring_threshold, 0.75));
    assert(approx(o.avoid_node_threshold, 0.50));
    assert(approx(o.quarantine_threshold, 0.25));
    assert(approx(o.recovery_rate_per_second, 0.001));
    assert(o.event_window_ms == 5 * 60 * 1000);
    assert(o.max_events_per_node == 100);
    assert(approx(o.initial_trust_score, 1.0));
    printf("  options: OK\n");
}

/* ---------------------------------------------------------------------------
 * NodeTrustRegistry
 * --------------------------------------------------------------------------- */
static void test_registry(void) {
    ca_security_options_t o;
    ca_security_options_init_defaults(&o);
    ca_node_trust_registry_t *reg = ca_node_trust_registry_create(&o);
    assert(reg);

    /* Unknown node reports initial trust. */
    assert(approx(ca_node_trust_registry_get_trust_score(reg, "ghost"), 1.0));

    /* Degrade drops the score and clamps at 0. */
    ca_peer_security_event_t *e = ca_peer_security_event_create(
        "n1", CA_PEER_EVENT_INTRUSION_SIGNAL, CA_PEER_THREAT_HIGH, "hit", "wifi", T0);
    double prev = 0, cur = 0;
    assert(ca_node_trust_registry_apply_degradation(reg, e, 0.30, &prev, &cur) == 0);
    assert(approx(prev, 1.0));
    assert(approx(cur, 0.70));
    ca_peer_security_event_destroy(e);

    /* Recent-events history holds the event within window. */
    ca_peer_security_event_t **hist = NULL;
    size_t hc = ca_node_trust_registry_get_recent_events(reg, "n1", T0 + 1000, &hist);
    assert(hc == 1);
    assert(hist[0]->kind == CA_PEER_EVENT_INTRUSION_SIGNAL);
    ca_peer_security_event_destroy(hist[0]);
    free(hist);

    /* Clamp at zero after big degradation. */
    ca_peer_security_event_t *e2 = ca_peer_security_event_create(
        "n1", CA_PEER_EVENT_INTRUSION_SIGNAL, CA_PEER_THREAT_CRITICAL, "big", "wifi", T0);
    ca_node_trust_registry_apply_degradation(reg, e2, 5.0, &prev, &cur);
    assert(approx(cur, 0.0));
    ca_peer_security_event_destroy(e2);

    /* Recovery heals toward 1.0: 0.001/s * 100000 ms = 0.1. */
    ca_node_trust_registry_apply_recovery(reg, 100000);
    assert(approx(ca_node_trust_registry_get_trust_score(reg, "n1"), 0.1));

    /* Stream replay: a reader opened now sees all updates emitted so far. */
    ca_trust_update_reader_t *rd = ca_node_trust_registry_open_reader(reg);
    ca_peer_trust_score_update_t u;
    size_t updates = 0;
    bool saw_recovery = false, saw_degradation = false;
    while (ca_trust_update_reader_next(rd, &u)) {
        updates++;
        if (strcmp(u.reason, "passive-recovery") == 0) saw_recovery = true;
        if (strcmp(u.reason, "hit") == 0) saw_degradation = true;
        ca_peer_trust_score_update_destroy(&u);
    }
    /* 2 degradations + 1 recovery = 3 updates. */
    assert(updates == 3);
    assert(saw_recovery && saw_degradation);
    ca_trust_update_reader_destroy(rd);

    /* all_node_ids returns the single tracked node. */
    char **ids = NULL;
    size_t idc = ca_node_trust_registry_all_node_ids(reg, &ids);
    assert(idc == 1 && strcmp(ids[0], "n1") == 0);
    free(ids[0]); free(ids);

    ca_node_trust_registry_destroy(reg);
    printf("  registry: OK\n");
}

/* Event-history bounding: exceeding max_events_per_node drops oldest first. */
static void test_registry_history_bound(void) {
    ca_security_options_t o;
    ca_security_options_init_defaults(&o);
    o.max_events_per_node = 3;
    ca_node_trust_registry_t *reg = ca_node_trust_registry_create(&o);
    for (int i = 0; i < 5; i++) {
        ca_peer_security_event_t *e = ca_peer_security_event_create(
            "n", CA_PEER_EVENT_BEHAVIOUR_CHANGE, CA_PEER_THREAT_LOW,
            "evt", "wifi", T0 + i);
        ca_node_trust_registry_apply_degradation(reg, e, 0.0, NULL, NULL);
        ca_peer_security_event_destroy(e);
    }
    ca_peer_security_event_t **hist = NULL;
    size_t hc = ca_node_trust_registry_get_recent_events(reg, "n", T0 + 100, &hist);
    assert(hc == 3); /* bounded */
    /* Oldest dropped: remaining timestamps are T0+2, T0+3, T0+4. */
    assert(hist[0]->occurred_at_ms == T0 + 2);
    assert(hist[2]->occurred_at_ms == T0 + 4);
    for (size_t i = 0; i < hc; i++) ca_peer_security_event_destroy(hist[i]);
    free(hist);
    ca_node_trust_registry_destroy(reg);
    printf("  registry history bound: OK\n");
}

/* ---------------------------------------------------------------------------
 * DirectivePublisher
 * --------------------------------------------------------------------------- */
typedef struct { int count; ca_peer_directive_kind_t last; } dcap_t;
static void dcap_on(void *user, const ca_peer_directive_t *d) {
    dcap_t *c = (dcap_t *)user;
    c->count++;
    c->last = d->kind;
}

/* Consumer that unsubscribes itself on first callback. */
typedef struct {
    ca_directive_publisher_t    *pub;
    ca_directive_subscription_t *self;
    int                          count;
} self_unsub_t;
static void self_unsub_on(void *user, const ca_peer_directive_t *d) {
    (void)d;
    self_unsub_t *s = (self_unsub_t *)user;
    s->count++;
    ca_directive_publisher_unsubscribe(s->pub, s->self);
    s->self = NULL;
}

static void test_publisher(void) {
    ca_directive_publisher_t *pub = ca_directive_publisher_create();
    assert(pub);
    assert(ca_directive_publisher_subscriber_count(pub) == 0);

    dcap_t a = {0, 0}, b = {0, 0};
    ca_directive_subscription_t *sa = ca_directive_publisher_subscribe(pub, dcap_on, &a);
    ca_directive_subscription_t *sb = ca_directive_publisher_subscribe(pub, dcap_on, &b);
    assert(sa && sb);
    assert(ca_directive_publisher_subscriber_count(pub) == 2);

    ca_peer_directive_t d;
    memset(&d, 0, sizeof(d));
    d.kind = CA_PEER_DIRECTIVE_QUARANTINE_NODE;
    d.target_node_id = (char *)"n1";
    d.reason = (char *)"bad";
    /* Fan-out: both consumers receive it. */
    ca_directive_publisher_publish(pub, &d);
    assert(a.count == 1 && b.count == 1);
    assert(a.last == CA_PEER_DIRECTIVE_QUARANTINE_NODE);

    /* Unsubscribe a; only b receives the next. */
    ca_directive_publisher_unsubscribe(pub, sa);
    assert(ca_directive_publisher_subscriber_count(pub) == 1);
    ca_directive_publisher_publish(pub, &d);
    assert(a.count == 1 && b.count == 2);

    ca_directive_publisher_unsubscribe(pub, sb);
    assert(ca_directive_publisher_subscriber_count(pub) == 0);

    /* Unsubscribe-during-dispatch must not corrupt the fan-out. */
    self_unsub_t su = { pub, NULL, 0 };
    su.self = ca_directive_publisher_subscribe(pub, self_unsub_on, &su);
    dcap_t bystander = {0, 0};
    ca_directive_subscription_t *sby =
        ca_directive_publisher_subscribe(pub, dcap_on, &bystander);
    ca_directive_publisher_publish(pub, &d); /* su unsubscribes itself mid-fanout */
    assert(su.count == 1);
    assert(bystander.count == 1); /* bystander still delivered */
    /* Second publish: su is gone, only bystander. */
    ca_directive_publisher_publish(pub, &d);
    assert(su.count == 1);
    assert(bystander.count == 2);
    ca_directive_publisher_unsubscribe(pub, sby);

    ca_directive_publisher_destroy(pub);
    printf("  publisher: OK\n");
}

/* ---------------------------------------------------------------------------
 * SecurityLayerService — threshold directives
 * --------------------------------------------------------------------------- */
typedef struct {
    int elevate, avoid, quarantine;
} dir_counts_t;
static void dir_count_on(void *user, const ca_peer_directive_t *d) {
    dir_counts_t *c = (dir_counts_t *)user;
    switch (d->kind) {
        case CA_PEER_DIRECTIVE_ELEVATE_MONITORING: c->elevate++; break;
        case CA_PEER_DIRECTIVE_AVOID_NODE:         c->avoid++; break;
        case CA_PEER_DIRECTIVE_QUARANTINE_NODE:    c->quarantine++; break;
        default: break;
    }
}

static void test_layer_service(void) {
    ca_security_options_t o;
    ca_security_options_init_defaults(&o);
    ca_node_trust_registry_t *reg = ca_node_trust_registry_create(&o);
    ca_directive_publisher_t *pub = ca_directive_publisher_create();
    ca_security_layer_service_t *svc =
        ca_security_layer_service_create(reg, &o, pub);
    assert(svc);

    dir_counts_t counts = {0, 0, 0};
    ca_directive_subscription_t *sub =
        ca_security_layer_service_subscribe_directives(svc, dir_count_on, &counts);
    assert(sub);

    /* Active flag toggles. */
    assert(ca_security_layer_service_is_active(svc) == false);
    ca_security_layer_service_start(svc);
    assert(ca_security_layer_service_is_active(svc) == true);

    /* Drive the node from 1.0 downward past each threshold. Each event uses a
     * Medium intrusion (0.15 * 1.0 = 0.15 degradation). Crossings:
     *   1.00 -> 0.85 -> 0.70 (crosses 0.75: elevate)
     *   0.70 -> 0.55 -> 0.40 (crosses 0.50: avoid)
     *   0.40 -> 0.25 (crosses 0.25 at boundary: quarantine, since <= 0.25) */
    for (int i = 0; i < 5; i++) {
        ca_peer_security_event_t *e = ca_peer_security_event_create(
            "n1", CA_PEER_EVENT_INTRUSION_SIGNAL, CA_PEER_THREAT_MEDIUM,
            "attack", "wifi", T0 + i);
        ca_security_layer_service_handle_peer_event(svc, e);
        ca_peer_security_event_destroy(e);
    }
    /* Exactly one of each directive across the descent. */
    assert(counts.elevate == 1);
    assert(counts.avoid == 1);
    assert(counts.quarantine == 1);

    /* None-level events are ignored (no directive, no degradation). */
    double before = ca_node_trust_registry_get_trust_score(reg, "n1");
    ca_peer_security_event_t *noop = ca_peer_security_event_create(
        "n1", CA_PEER_EVENT_AUTH_ATTEMPT, CA_PEER_THREAT_NONE, "noop", "wifi", T0);
    ca_security_layer_service_handle_peer_event(svc, noop);
    ca_peer_security_event_destroy(noop);
    assert(approx(ca_node_trust_registry_get_trust_score(reg, "n1"), before));

    /* Posture: 1 quarantined node, active. */
    ca_peer_security_posture_t posture;
    ca_security_layer_service_get_posture(svc, &posture);
    assert(posture.quarantined_peer_count == 1);
    assert(posture.is_active == true);
    assert(posture.overall_threat_level == CA_PEER_THREAT_CRITICAL);

    /* Recovery tick lifts the score (0.001/s * 300000 ms = 0.3). */
    ca_security_layer_service_recover_tick(svc, 300000);
    double after = ca_node_trust_registry_get_trust_score(reg, "n1");
    assert(after > before);

    ca_security_layer_service_stop(svc);
    assert(ca_security_layer_service_is_active(svc) == false);

    ca_directive_publisher_unsubscribe(pub, sub);
    ca_security_layer_service_destroy(svc);
    ca_directive_publisher_destroy(pub);
    ca_node_trust_registry_destroy(reg);
    printf("  layer service: OK\n");
}

/* ---------------------------------------------------------------------------
 * PeerIntelligenceService
 * --------------------------------------------------------------------------- */
static void test_intelligence(void) {
    ca_security_options_t o;
    ca_security_options_init_defaults(&o);
    ca_node_trust_registry_t *reg = ca_node_trust_registry_create(&o);
    ca_peer_intelligence_service_t *intel =
        ca_peer_intelligence_service_create(reg, &o);
    assert(intel);

    /* Empty network -> perfect health, no peers. */
    ca_peer_network_health_report_t health;
    assert(ca_peer_intelligence_service_get_network_health(intel, &health) == 0);
    assert(approx(health.overall_score, 1.0));
    assert(health.trusted_peer_count == 0);
    assert(strcmp(health.summary, "No peers observed.") == 0);
    ca_peer_network_health_report_destroy(&health);

    /* Introduce two peers with different degradation. */
    ca_peer_security_event_t *e1 = ca_peer_security_event_create(
        "trusted", CA_PEER_EVENT_AUTH_ATTEMPT, CA_PEER_THREAT_LOW, "a", "wifi", T0);
    ca_node_trust_registry_apply_degradation(reg, e1, 0.05, NULL, NULL); /* 0.95 */
    ca_peer_security_event_destroy(e1);

    ca_peer_security_event_t *e2 = ca_peer_security_event_create(
        "bad", CA_PEER_EVENT_INTRUSION_SIGNAL, CA_PEER_THREAT_CRITICAL, "i", "wifi", T0);
    ca_node_trust_registry_apply_degradation(reg, e2, 0.85, NULL, NULL); /* 0.15 */
    ca_peer_security_event_destroy(e2);

    /* Health: avg (0.95 + 0.15)/2 = 0.55. trusted (>0.50)=1, suspicious(<=0.75)=1. */
    assert(ca_peer_intelligence_service_get_network_health(intel, &health) == 0);
    assert(approx(health.overall_score, 0.55));
    assert(health.trusted_peer_count == 1);
    assert(health.suspicious_peer_count == 1);
    assert(strstr(health.summary, "degraded") != NULL); /* 0.55 band */
    ca_peer_network_health_report_destroy(&health);

    /* Assess "bad": score 0.15 -> Critical; deficit 0.85. Its single Critical
     * intrusion event yields TWO indicators (intrusion-signal-detected +
     * high-severity-event), so confidence = min(1.0, 0.85 + 2*0.1) = 1.0. */
    ca_peer_threat_assessment_t assess;
    assert(ca_peer_intelligence_service_assess_threat(intel, "bad", T0 + 1000, &assess) == 0);
    assert(assess.threat_level == CA_PEER_THREAT_CRITICAL);
    assert(assess.indicator_count == 2);
    assert(approx(assess.confidence, 1.0));
    /* Indicators include intrusion + high-severity (Critical). */
    bool has_intr = false, has_high = false;
    for (size_t i = 0; i < assess.indicator_count; i++) {
        if (strcmp(assess.indicators[i], "intrusion-signal-detected") == 0) has_intr = true;
        if (strcmp(assess.indicators[i], "high-severity-event") == 0) has_high = true;
    }
    assert(has_intr && has_high);
    ca_peer_threat_assessment_destroy(&assess);

    /* Routing to "trusted" (0.95): direct path, F2 reasoning, high confidence. */
    ca_peer_routing_advice_t adv;
    assert(ca_peer_intelligence_service_get_routing_advice(intel, "trusted", &adv) == 0);
    assert(adv.recommended_path_count == 1);
    assert(strcmp(adv.recommended_path[0], "trusted") == 0);
    assert(approx(adv.confidence, 0.95));
    assert(strstr(adv.reasoning, "trusted") != NULL);
    assert(strstr(adv.reasoning, "0.95") != NULL); /* F2 formatting */
    /* "bad" (0.15) is on the avoid list. */
    bool bad_avoided = false;
    for (size_t i = 0; i < adv.avoid_node_count; i++)
        if (strcmp(adv.avoid_node_ids[i], "bad") == 0) bad_avoided = true;
    assert(bad_avoided);
    ca_peer_routing_advice_destroy(&adv);

    /* Routing to "bad" (0.15 <= avoid): no direct path, quarantine reasoning. */
    ca_peer_routing_advice_t adv2;
    assert(ca_peer_intelligence_service_get_routing_advice(intel, "bad", &adv2) == 0);
    assert(adv2.recommended_path_count == 0);
    assert(strstr(adv2.reasoning, "quarantined") != NULL);
    ca_peer_routing_advice_destroy(&adv2);

    /* Stream trust scores: reader replays every update emitted. */
    ca_trust_update_reader_t *rd =
        ca_peer_intelligence_service_stream_trust_scores(intel);
    assert(rd);
    ca_peer_trust_score_update_t u;
    size_t seen = 0;
    while (ca_trust_update_reader_next(rd, &u)) {
        seen++;
        ca_peer_trust_score_update_destroy(&u);
    }
    assert(seen == 2); /* two degradations recorded */
    ca_trust_update_reader_destroy(rd);

    ca_peer_intelligence_service_destroy(intel);
    ca_node_trust_registry_destroy(reg);
    printf("  intelligence: OK\n");
}

/* ---------------------------------------------------------------------------
 * IPeerSecurityEventFeed — pump adapter
 * --------------------------------------------------------------------------- */
/* A deterministic feed that emits three events into the handler. */
static void demo_feed_start(void *self, ca_peer_event_handler_fn handler, void *user) {
    (void)self;
    for (int i = 0; i < 3; i++) {
        ca_peer_security_event_t *e = ca_peer_security_event_create(
            "feednode", CA_PEER_EVENT_INTRUSION_SIGNAL, CA_PEER_THREAT_CRITICAL,
            "feed hit", "wifi", T0 + i);
        handler(user, e);
        ca_peer_security_event_destroy(e);
    }
}

static void test_event_feed(void) {
    ca_security_options_t o;
    ca_security_options_init_defaults(&o);
    ca_node_trust_registry_t *reg = ca_node_trust_registry_create(&o);
    ca_directive_publisher_t *pub = ca_directive_publisher_create();
    ca_security_layer_service_t *svc =
        ca_security_layer_service_create(reg, &o, pub);

    ca_peer_security_event_feed_t feed;
    feed.self = NULL;
    feed.transport_id = "wifi";
    feed.start = demo_feed_start;

    /* Three critical intrusions (0.45 each) drive the node well below zero. */
    ca_peer_security_event_feed_pump_into_layer(&feed, svc);
    double score = ca_node_trust_registry_get_trust_score(reg, "feednode");
    assert(score < 0.25); /* quarantine territory */

    ca_security_layer_service_destroy(svc);
    ca_directive_publisher_destroy(pub);
    ca_node_trust_registry_destroy(reg);
    printf("  event feed pump: OK\n");
}

int main(void) {
    test_threat_detector();
    test_options();
    test_registry();
    test_registry_history_bound();
    test_publisher();
    test_layer_service();
    test_intelligence();
    test_event_feed();
    printf("All peer security tests passed.\n");
    return 0;
}
