/*
 * test_aether.c — CircleAI.Aether contracts (aether.h).
 *
 * Verifies:
 *   Records          : node/transport/route/security/network event helpers,
 *                      AuthChallengeResult, SecurityDirective, deep copy
 *   Version          : System.Version-style comparison
 *   IAetherContext   : IsAvailable / IsSufficient / RequiresAuth / IsEnabled
 *                      derived-flag rules; toggle-off of an OS instance
 *   Telemetry hub    : fan-out to observers, subscribe SYNC before publish,
 *                      unsubscribe-during-dispatch, NullAetherTelemetry
 *   Auth challenge   : scripted adapter enforces the platform minimum floor,
 *                      OS toggle always demands biometric+deviceadmin
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <math.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

#define T0 1700000000000LL

static bool approx(double a, double b) { return fabs(a - b) < 1e-9; }

/* ---------------------------------------------------------------------------
 * Event records
 * --------------------------------------------------------------------------- */
static void test_records(void) {
    /* Node event + health validity + IsExit. */
    ca_aether_node_health_t h = { 0.8, true, 12, 3 };
    assert(ca_aether_node_health_is_valid(&h));
    ca_aether_node_health_t bad = { 1.5, true, 0, 0 };
    assert(!ca_aether_node_health_is_valid(&bad));

    ca_aether_node_event_t *ne =
        ca_aether_node_event_create("n1", CA_AETHER_NODE_LEFT, h, T0);
    assert(ne && strcmp(ne->node_id, "n1") == 0);
    assert(ca_aether_node_event_is_exit(ne));
    ca_aether_node_event_t *ne2 = ca_aether_node_event_copy(ne);
    assert(ne2 && ne2->node_id != ne->node_id &&
           strcmp(ne2->node_id, "n1") == 0);
    assert(approx(ne2->health.trust_score, 0.8));
    ca_aether_node_event_destroy(ne);
    ca_aether_node_event_destroy(ne2);

    /* Transport event ExceedsLoss. */
    ca_aether_transport_event_t te = { 0 };
    te.has_packet_loss = true;
    te.packet_loss_rate = 0.2;
    assert(ca_aether_transport_event_exceeds_loss(&te, 0.1));
    assert(!ca_aether_transport_event_exceeds_loss(&te, 0.3));
    te.has_packet_loss = false;
    assert(!ca_aether_transport_event_exceeds_loss(&te, 0.0));

    /* Route event hop count + IsFailed + copy. */
    const char *path[] = { "a", "b", "c" };
    ca_aether_route_event_t *re = ca_aether_route_event_create(
        "a", "c", path, 3, CA_AETHER_ROUTE_FAILED, "link down", T0);
    assert(re && ca_aether_route_event_hop_count(re) == 3);
    assert(ca_aether_route_event_is_failed(re));
    assert(strcmp(re->failure_reason, "link down") == 0);
    ca_aether_route_event_t *re2 = ca_aether_route_event_copy(re);
    assert(re2 && re2->path_count == 3 && strcmp(re2->path[1], "b") == 0);
    ca_aether_route_event_destroy(re);
    ca_aether_route_event_destroy(re2);

    /* Security event metadata + high severity. */
    ca_aether_metadata_pair_t md[2] = {
        { (char *)"srcPort", (char *)"443" },
        { (char *)"attempts", (char *)"5" },
    };
    ca_aether_security_event_t *se = ca_aether_security_event_create(
        "n2", CA_AETHER_SEC_INTRUSION_SIGNAL, CA_AETHER_THREAT_HIGH,
        "replay", md, 2, T0);
    assert(se && ca_aether_security_event_is_high_severity(se));
    assert(strcmp(ca_aether_security_event_metadata(se, "attempts"), "5") == 0);
    assert(ca_aether_security_event_metadata(se, "nope") == NULL);
    ca_aether_security_event_t *se2 = ca_aether_security_event_copy(se);
    assert(se2 && se2->metadata_count == 2);
    assert(strcmp(ca_aether_security_event_metadata(se2, "srcPort"), "443") == 0);
    ca_aether_security_event_destroy(se);
    ca_aether_security_event_destroy(se2);

    /* Network event congestion. */
    ca_aether_network_event_t nete = { CA_AETHER_NET_CONGESTION_DETECTED, 10, 4,
                                       0.9, T0 };
    assert(ca_aether_network_event_is_high_congestion(&nete));
    nete.congestion_level = 0.5;
    assert(!ca_aether_network_event_is_high_congestion(&nete));

    /* AuthChallengeResult factories. */
    ca_auth_challenge_result_t ok =
        ca_auth_challenge_result_success(CA_AUTH_METHOD_BIOMETRIC_AND_DEVICE_ADMIN, T0);
    assert(ok.succeeded && ok.failure_reason == NULL);
    ca_auth_challenge_result_destroy(&ok);
    ca_auth_challenge_result_t fail =
        ca_auth_challenge_result_failure(CA_AUTH_METHOD_BIOMETRIC, "nope", T0);
    assert(!fail.succeeded && strcmp(fail.failure_reason, "nope") == 0);
    ca_auth_challenge_result_destroy(&fail);

    /* SecurityDirective HasTarget / IsPermanent + copy. */
    ca_aether_security_directive_t *d = ca_aether_security_directive_create(
        CA_AETHER_DIRECTIVE_QUARANTINE_NODE, "bad-node", true, 0.1,
        CA_AETHER_THREAT_CRITICAL, "isolate", false, 0, T0);
    assert(d && ca_aether_security_directive_has_target(d));
    assert(ca_aether_security_directive_is_permanent(d)); /* no duration */
    ca_aether_security_directive_t *d2 = ca_aether_security_directive_copy(d);
    assert(d2 && strcmp(d2->target_node_id, "bad-node") == 0);
    assert(d2->has_trust_score_override && approx(d2->trust_score_override, 0.1));
    ca_aether_security_directive_destroy(d);
    ca_aether_security_directive_destroy(d2);

    /* Directive with no target => !HasTarget; with duration => !IsPermanent. */
    ca_aether_security_directive_t *d3 = ca_aether_security_directive_create(
        CA_AETHER_DIRECTIVE_ELEVATE_MONITORING, "  ", false, 0,
        CA_AETHER_THREAT_LOW, "watch", true, 5000, T0);
    assert(!ca_aether_security_directive_has_target(d3)); /* whitespace */
    assert(!ca_aether_security_directive_is_permanent(d3));
    ca_aether_security_directive_destroy(d3);
}

/* ---------------------------------------------------------------------------
 * Version comparison
 * --------------------------------------------------------------------------- */
static void test_version(void) {
    ca_aether_version_t a = { 2, 5, 0, -1 };
    ca_aether_version_t b = { 2, 5, -1, -1 }; /* build unset => 0 */
    assert(ca_aether_version_compare(a, b) == 0);
    ca_aether_version_t c = { 2, 6, 0, 0 };
    assert(ca_aether_version_compare(a, c) < 0);
    assert(ca_aether_version_compare(c, a) > 0);
    ca_aether_version_t d = { 2, 5, 1, 0 };
    assert(ca_aether_version_compare(d, a) > 0);
}

/* ---------------------------------------------------------------------------
 * IAetherContext derived flags
 * --------------------------------------------------------------------------- */
static void test_context(void) {
    ca_aether_version_t rt = { 2, 5, 0, 0 };
    ca_aether_version_t minv = { 2, 4, 0, 0 };

    /* OS-managed, enabled, runtime >= minimum. */
    ca_aether_context_impl_t *c = ca_aether_context_impl_create(
        CA_AETHER_INSTALL_OS, true, rt, true, minv, true);
    assert(c);
    ca_aether_context_t v = ca_aether_context_impl_as_context(c);
    assert(v.install_level(v.self) == CA_AETHER_INSTALL_OS);
    assert(v.is_available(v.self));
    assert(v.is_enabled(v.self));
    assert(v.requires_auth(v.self)); /* OS => RequiresAuth */
    assert(v.is_sufficient(v.self)); /* 2.5 >= 2.4 */
    ca_aether_version_t got;
    assert(v.runtime_version(v.self, &got) && got.minor == 5);
    assert(v.minimum_required(v.self, &got) && got.minor == 4);

    /* Toggle the OS instance off: available/enabled flip to false. */
    ca_aether_context_impl_set_enabled(c, false);
    assert(!v.is_available(v.self));
    assert(!v.is_enabled(v.self));
    assert(v.requires_auth(v.self)); /* still OS */
    ca_aether_context_impl_destroy(c);

    /* App-level, no minimum => IsSufficient always true; RequiresAuth false. */
    ca_aether_context_impl_t *c2 = ca_aether_context_impl_create(
        CA_AETHER_INSTALL_APP, true, rt, false, minv, true);
    ca_aether_context_t v2 = ca_aether_context_impl_as_context(c2);
    assert(v2.is_sufficient(v2.self));
    assert(!v2.requires_auth(v2.self));
    assert(!v2.minimum_required(v2.self, &got)); /* null */
    ca_aether_context_impl_destroy(c2);

    /* None => never available/enabled. */
    ca_aether_context_impl_t *c3 = ca_aether_context_impl_create(
        CA_AETHER_INSTALL_NONE, false, rt, false, minv, true);
    ca_aether_context_t v3 = ca_aether_context_impl_as_context(c3);
    assert(!v3.is_available(v3.self) && !v3.is_enabled(v3.self));
    assert(!v3.runtime_version(v3.self, &got)); /* null */
    ca_aether_context_impl_destroy(c3);

    /* Runtime below minimum => IsSufficient false. Missing runtime + minimum
     * present => false. */
    ca_aether_version_t old = { 2, 3, 0, 0 };
    ca_aether_context_impl_t *c4 = ca_aether_context_impl_create(
        CA_AETHER_INSTALL_OS, true, old, true, minv, true);
    ca_aether_context_t v4 = ca_aether_context_impl_as_context(c4);
    assert(!v4.is_sufficient(v4.self)); /* 2.3 < 2.4 */
    ca_aether_context_impl_destroy(c4);

    ca_aether_context_impl_t *c5 = ca_aether_context_impl_create(
        CA_AETHER_INSTALL_OS, false, rt, true, minv, true);
    ca_aether_context_t v5 = ca_aether_context_impl_as_context(c5);
    assert(!v5.is_sufficient(v5.self)); /* no runtime, minimum present */
    ca_aether_context_impl_destroy(c5);
}

/* ---------------------------------------------------------------------------
 * Telemetry hub fan-out
 * --------------------------------------------------------------------------- */
typedef struct {
    int  security_hits;
    int  node_hits;
    char last_node[32];
    /* self-unsubscribe support */
    ca_aether_telemetry_t     *tel;
    ca_aether_subscription_t **my_sub;
} obs_state_t;

static void obs_on_security(void *self, const ca_aether_security_event_t *e) {
    obs_state_t *s = (obs_state_t *)self;
    s->security_hits++;
    if (e && e->node_id) {
        strncpy(s->last_node, e->node_id, sizeof(s->last_node) - 1);
        s->last_node[sizeof(s->last_node) - 1] = '\0';
    }
}
static void obs_on_node(void *self, const ca_aether_node_event_t *e) {
    (void)e;
    ((obs_state_t *)self)->node_hits++;
}

/* An observer that unsubscribes ITSELF the first time it fires. */
static void obs_self_unsub_security(void *self,
                                    const ca_aether_security_event_t *e) {
    (void)e;
    obs_state_t *s = (obs_state_t *)self;
    s->security_hits++;
    if (s->tel && s->my_sub && *s->my_sub) {
        s->tel->unsubscribe(s->tel->self, *s->my_sub);
        *s->my_sub = NULL;
    }
}

static void test_telemetry_hub(void) {
    ca_aether_telemetry_hub_t *hub = ca_aether_telemetry_hub_create();
    assert(hub);
    ca_aether_telemetry_t tel = ca_aether_telemetry_hub_as_telemetry(hub);

    obs_state_t s1 = { 0 };
    obs_state_t s2 = { 0 };

    /* SUBSCRIBE synchronously before any publish (no lost-message race). */
    ca_aether_telemetry_observer_t o1;
    memset(&o1, 0, sizeof(o1));
    o1.self = &s1;
    o1.on_security_event = obs_on_security;
    o1.on_node_event = obs_on_node;
    ca_aether_subscription_t *sub1 = tel.subscribe(tel.self, &o1);
    assert(sub1 && ca_aether_telemetry_hub_subscriber_count(hub) == 1);

    ca_aether_telemetry_observer_t o2;
    memset(&o2, 0, sizeof(o2));
    o2.self = &s2;
    o2.on_security_event = obs_on_security;
    ca_aether_subscription_t *sub2 = tel.subscribe(tel.self, &o2);
    assert(sub2 && ca_aether_telemetry_hub_subscriber_count(hub) == 2);

    /* Publish a security event -> both observers receive it. */
    ca_aether_security_event_t *e = ca_aether_security_event_create(
        "node-A", CA_AETHER_SEC_ROUTING_ANOMALY, CA_AETHER_THREAT_MEDIUM,
        "weird", NULL, 0, T0);
    ca_aether_telemetry_hub_publish_security(hub, e);
    assert(s1.security_hits == 1 && s2.security_hits == 1);
    assert(strcmp(s1.last_node, "node-A") == 0);

    /* Node event -> only s1 has a node handler. */
    ca_aether_node_health_t h = { 1.0, true, 0, 1 };
    ca_aether_node_event_t *ne =
        ca_aether_node_event_create("node-A", CA_AETHER_NODE_JOINED, h, T0);
    ca_aether_telemetry_hub_publish_node(hub, ne);
    assert(s1.node_hits == 1 && s2.node_hits == 0);
    ca_aether_node_event_destroy(ne);

    /* Unsubscribe s1, publish again -> only s2 fires. */
    tel.unsubscribe(tel.self, sub1);
    assert(ca_aether_telemetry_hub_subscriber_count(hub) == 1);
    ca_aether_telemetry_hub_publish_security(hub, e);
    assert(s1.security_hits == 1 && s2.security_hits == 2);

    tel.unsubscribe(tel.self, sub2);
    ca_aether_security_event_destroy(e);
    ca_aether_telemetry_hub_destroy(hub);
}

/* Unsubscribe DURING dispatch must not corrupt the fan-out. */
static void test_telemetry_self_unsub(void) {
    ca_aether_telemetry_hub_t *hub = ca_aether_telemetry_hub_create();
    ca_aether_telemetry_t tel = ca_aether_telemetry_hub_as_telemetry(hub);

    obs_state_t s = { 0 };
    ca_aether_subscription_t *sub = NULL;
    s.tel = &tel;
    s.my_sub = &sub;

    ca_aether_telemetry_observer_t o;
    memset(&o, 0, sizeof(o));
    o.self = &s;
    o.on_security_event = obs_self_unsub_security;
    sub = tel.subscribe(tel.self, &o);
    assert(sub);

    /* A second plain observer so the fan-out has >1 element in the snapshot. */
    obs_state_t s2 = { 0 };
    ca_aether_telemetry_observer_t o2;
    memset(&o2, 0, sizeof(o2));
    o2.self = &s2;
    o2.on_security_event = obs_on_security;
    ca_aether_subscription_t *sub2 = tel.subscribe(tel.self, &o2);
    assert(sub2);

    ca_aether_security_event_t *e = ca_aether_security_event_create(
        "x", CA_AETHER_SEC_INTRUSION_SIGNAL, CA_AETHER_THREAT_HIGH, "boom",
        NULL, 0, T0);
    ca_aether_telemetry_hub_publish_security(hub, e); /* s unsubscribes here */
    assert(s.security_hits == 1 && s2.security_hits == 1);
    assert(sub == NULL); /* self-unsubscribed */
    assert(ca_aether_telemetry_hub_subscriber_count(hub) == 1);

    /* Next publish: only s2 remains. */
    ca_aether_telemetry_hub_publish_security(hub, e);
    assert(s.security_hits == 1 && s2.security_hits == 2);

    tel.unsubscribe(tel.self, sub2);
    ca_aether_security_event_destroy(e);
    ca_aether_telemetry_hub_destroy(hub);
}

/* NullAetherTelemetry: subscribe returns non-NULL, no events emitted. */
static void test_null_telemetry(void) {
    ca_aether_telemetry_t t = ca_null_aether_telemetry();
    obs_state_t s = { 0 };
    ca_aether_telemetry_observer_t o;
    memset(&o, 0, sizeof(o));
    o.self = &s;
    o.on_security_event = obs_on_security;
    ca_aether_subscription_t *sub = t.subscribe(t.self, &o);
    assert(sub != NULL);        /* non-null token */
    t.unsubscribe(t.self, sub); /* no-op */
    assert(s.security_hits == 0);
    /* NULL observer rejected. */
    assert(t.subscribe(t.self, NULL) == NULL);
}

/* ---------------------------------------------------------------------------
 * Scripted IAuthChallenge
 * --------------------------------------------------------------------------- */
static void test_auth_challenge(void) {
    /* Device satisfies biometric+deviceadmin. */
    ca_scripted_auth_challenge_t *a = ca_scripted_auth_challenge_create(
        CA_AUTH_METHOD_BIOMETRIC_AND_DEVICE_ADMIN, T0);
    assert(a);
    ca_auth_challenge_t ch = ca_scripted_auth_challenge_as_challenge(a);

    /* Requesting nothing => floor is biometric+deviceadmin => succeed. */
    ca_auth_challenge_result_t r;
    assert(ch.challenge(ch.self, CA_AUTH_REASON_PRIVILEGED_OPERATION, false, 0,
                        "confirm", &r) == 0);
    assert(r.succeeded &&
           r.method_used == CA_AUTH_METHOD_BIOMETRIC_AND_DEVICE_ADMIN);
    ca_auth_challenge_result_destroy(&r);

    /* Requesting a WEAKER minimum (Biometric) is raised to the floor and still
     * succeeds because the device satisfies the floor. */
    assert(ch.challenge(ch.self, CA_AUTH_REASON_MANUAL_REQUEST, true,
                        CA_AUTH_METHOD_BIOMETRIC, "x", &r) == 0);
    assert(r.succeeded);
    ca_auth_challenge_result_destroy(&r);

    /* Requesting Custom (stronger than device) => fail. */
    assert(ch.challenge(ch.self, CA_AUTH_REASON_MANUAL_REQUEST, true,
                        CA_AUTH_METHOD_CUSTOM, "x", &r) == 0);
    assert(!r.succeeded && r.method_used == CA_AUTH_METHOD_CUSTOM);
    assert(r.failure_reason != NULL);
    ca_auth_challenge_result_destroy(&r);

    /* OS toggle always demands biometric+deviceadmin: succeeds here. */
    assert(ch.request_os_toggle(ch.self, true, &r) == 0);
    assert(r.succeeded);
    ca_auth_challenge_result_destroy(&r);

    /* Downgrade the device to Biometric-only: floor not met => fail, and OS
     * toggle fails too. */
    ca_scripted_auth_challenge_set_available(a, CA_AUTH_METHOD_BIOMETRIC);
    assert(ch.challenge(ch.self, CA_AUTH_REASON_OS_LEVEL_TOGGLE, false, 0,
                        "x", &r) == 0);
    assert(!r.succeeded);
    ca_auth_challenge_result_destroy(&r);
    assert(ch.request_os_toggle(ch.self, false, &r) == 0);
    assert(!r.succeeded);
    ca_auth_challenge_result_destroy(&r);

    ca_scripted_auth_challenge_destroy(a);
}

int main(void) {
    test_records();
    test_version();
    test_context();
    test_telemetry_hub();
    test_telemetry_self_unsub();
    test_null_telemetry();
    test_auth_challenge();
    return 0;
}
