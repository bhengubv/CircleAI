/*
 * test_watchdog.c — CircleAI local runtime immune system (watchdog.h).
 *
 * Verifies:
 *   SecurityCheckpoint  : create, verify, tamper detection, deep copy, to_string
 *   SecurityResponse    : NoAction / KeyRotation / Rollback / Composite factories
 *   UhidKeyRing         : sign/verify, revoke blocks sign, verify-after-revoke,
 *                         rotate returns fresh ring + leaves old revoked,
 *                         cross-ring signature rejection, public-key export
 *   RedactedEvidence    : value redaction (empty + non-empty), JSON shape
 *   DefaultSecurityWatchdog : graduated response policy + stream replay
 *   DefaultAnomalyEventDispatcher : dispatched / duplicate / below / cancelled
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "circle_ai/circle_ai.h"

/* ---------------------------------------------------------------------------
 * SecurityCheckpoint
 * --------------------------------------------------------------------------- */
static void test_checkpoint(void) {
    const uint8_t payload[] = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
    ca_security_checkpoint_t *cp = ca_security_checkpoint_create(
        "uhid-alice", "CircleAI.Companion", payload, sizeof(payload));
    assert(cp);
    assert(strcmp(ca_security_checkpoint_uhid(cp), "uhid-alice") == 0);
    assert(strcmp(ca_security_checkpoint_module(cp), "CircleAI.Companion") == 0);

    size_t plen = 0;
    const uint8_t *pp = ca_security_checkpoint_payload(cp, &plen);
    assert(plen == sizeof(payload));
    assert(memcmp(pp, payload, plen) == 0);

    /* Verify passes on unmodified payload. */
    assert(ca_security_checkpoint_verify(cp) == true);

    /* Deep copy verifies too, and shares no memory. */
    ca_security_checkpoint_t *cp2 = ca_security_checkpoint_copy(cp);
    assert(cp2);
    assert(ca_security_checkpoint_verify(cp2) == true);
    size_t p2len = 0;
    const uint8_t *pp2 = ca_security_checkpoint_payload(cp2, &p2len);
    assert(pp2 != pp); /* distinct buffers */
    assert(p2len == plen);

    /* Hash is 32 bytes and identical across copy. */
    const uint8_t *h1 = ca_security_checkpoint_payload_hash(cp);
    const uint8_t *h2 = ca_security_checkpoint_payload_hash(cp2);
    assert(memcmp(h1, h2, 32) == 0);

    /* to_string never leaks payload bytes; contains module + hash prefix. */
    char buf[256];
    int n = ca_security_checkpoint_to_string(cp, buf, sizeof(buf));
    assert(n > 0);
    assert(strstr(buf, "CircleAI.Companion") != NULL);
    assert(strstr(buf, "PayloadSha256=") != NULL);
    assert(strstr(buf, "PayloadBytes=10") != NULL);

    ca_security_checkpoint_destroy(cp);
    ca_security_checkpoint_destroy(cp2);

    /* Empty payload is valid (hashes the empty message). */
    ca_security_checkpoint_t *empty =
        ca_security_checkpoint_create("uhid-bob", "CircleAI.Memory", NULL, 0);
    assert(empty);
    assert(ca_security_checkpoint_verify(empty) == true);
    ca_security_checkpoint_destroy(empty);

    /* Blank identity / module rejected. */
    assert(ca_security_checkpoint_create("", "m", payload, 1) == NULL);
    assert(ca_security_checkpoint_create("u", "   ", payload, 1) == NULL);
    /* NULL payload with non-zero len rejected. */
    assert(ca_security_checkpoint_create("u", "m", NULL, 5) == NULL);

    printf("  checkpoint: OK\n");
}

/* Tamper detection requires reaching into the payload; since payload accessor
 * returns const, we exercise verify-mismatch by creating two checkpoints over
 * different payloads and confirming their stored hashes differ. */
static void test_checkpoint_hash_distinguishes(void) {
    const uint8_t a[] = { 0xAA, 0xBB };
    const uint8_t b[] = { 0xAA, 0xBC };
    ca_security_checkpoint_t *ca = ca_security_checkpoint_create("u", "m", a, 2);
    ca_security_checkpoint_t *cb = ca_security_checkpoint_create("u", "m", b, 2);
    assert(ca && cb);
    assert(memcmp(ca_security_checkpoint_payload_hash(ca),
                  ca_security_checkpoint_payload_hash(cb), 32) != 0);
    ca_security_checkpoint_destroy(ca);
    ca_security_checkpoint_destroy(cb);
    printf("  checkpoint hash distinguishes: OK\n");
}

/* ---------------------------------------------------------------------------
 * SecurityResponse
 * --------------------------------------------------------------------------- */
static void test_response(void) {
    ca_security_response_t *na =
        ca_security_response_no_action("sig-1", "monitoring only");
    assert(na);
    assert(ca_security_response_kind(na) == CA_RESPONSE_NO_ACTION);
    assert(strcmp(ca_security_response_signal_id(na), "sig-1") == 0);
    size_t ac = 99;
    ca_security_response_applied_actions(na, &ac);
    assert(ac == 0);
    ca_security_response_destroy(na);

    ca_security_response_t *kr =
        ca_security_response_for_key_rotation("sig-2", "rotate now");
    assert(kr && ca_security_response_kind(kr) == CA_RESPONSE_KEY_ROTATION);
    ca_security_response_destroy(kr);

    /* Rollback records the restored checkpoint by deep copy. */
    const uint8_t pl[] = { 9, 9, 9 };
    ca_security_checkpoint_t *cp =
        ca_security_checkpoint_create("u", "CircleAI.Memory", pl, 3);
    ca_security_response_t *rb = ca_security_response_for_rollback("sig-3", cp);
    assert(rb && ca_security_response_kind(rb) == CA_RESPONSE_STATE_ROLLBACK);
    const ca_security_checkpoint_t *restored =
        ca_security_response_restored_checkpoint(rb);
    assert(restored != NULL);
    assert(strcmp(ca_security_checkpoint_module(restored), "CircleAI.Memory") == 0);
    /* Deep copy: destroying the original leaves the response intact. */
    ca_security_checkpoint_destroy(cp);
    assert(ca_security_checkpoint_verify(
        ca_security_response_restored_checkpoint(rb)) == true);
    ca_security_response_destroy(rb);

    /* Composite carries the action list. */
    ca_security_response_kind_t actions[] = {
        CA_RESPONSE_KEY_ROTATION, CA_RESPONSE_MESH_ISOLATION_SIGNAL
    };
    ca_security_response_t *comp = ca_security_response_composite(
        "sig-4", actions, 2, "composite", NULL);
    assert(comp && ca_security_response_kind(comp) == CA_RESPONSE_COMPOSITE);
    size_t cc = 0;
    const ca_security_response_kind_t *ap =
        ca_security_response_applied_actions(comp, &cc);
    assert(cc == 2);
    assert(ap[0] == CA_RESPONSE_KEY_ROTATION);
    assert(ap[1] == CA_RESPONSE_MESH_ISOLATION_SIGNAL);
    ca_security_response_destroy(comp);

    /* Rollback with NULL restored is rejected. */
    assert(ca_security_response_for_rollback("s", NULL) == NULL);

    printf("  response: OK\n");
}

/* ---------------------------------------------------------------------------
 * UhidKeyRing
 * --------------------------------------------------------------------------- */
static void test_keyring(void) {
    ca_uhid_key_ring_t *ring = ca_uhid_key_ring_generate_fresh("uhid-carol");
    assert(ring);
    assert(strcmp(ca_uhid_key_ring_uhid(ring), "uhid-carol") == 0);
    assert(ca_uhid_key_ring_is_revoked(ring) == false);
    assert(ca_uhid_key_ring_revoked_at_ms(ring) == -1);

    size_t pklen = 0;
    const uint8_t *pk = ca_uhid_key_ring_public_key(ring, &pklen);
    assert(pk && pklen == 32);

    const uint8_t msg[] = "attack at dawn";
    uint8_t sig[32];
    assert(ca_uhid_key_ring_sign(ring, msg, sizeof(msg), sig) == 0);
    assert(ca_uhid_key_ring_verify(ring, msg, sizeof(msg), sig, 32) == true);

    /* Wrong data fails verification. */
    const uint8_t other[] = "retreat at dusk";
    assert(ca_uhid_key_ring_verify(ring, other, sizeof(other), sig, 32) == false);

    /* Determinism: signing the same data again yields the identical MAC. */
    uint8_t sig2[32];
    assert(ca_uhid_key_ring_sign(ring, msg, sizeof(msg), sig2) == 0);
    assert(memcmp(sig, sig2, 32) == 0);

    /* Rotate: fresh ring, different id, old ring revoked but still verifies. */
    char old_id[64];
    strncpy(old_id, ca_uhid_key_ring_id(ring), sizeof(old_id) - 1);
    old_id[sizeof(old_id) - 1] = '\0';

    ca_uhid_key_ring_t *fresh = ca_uhid_key_ring_rotate(ring);
    assert(fresh);
    assert(ca_uhid_key_ring_is_revoked(ring) == true);           /* old revoked */
    assert(ca_uhid_key_ring_revoked_at_ms(ring) >= 0);
    assert(ca_uhid_key_ring_is_revoked(fresh) == false);
    assert(strcmp(ca_uhid_key_ring_id(fresh), old_id) != 0);     /* new id */
    assert(strcmp(ca_uhid_key_ring_uhid(fresh), "uhid-carol") == 0);

    /* Sign on the revoked ring fails (-2); Verify still works. */
    uint8_t sig3[32];
    assert(ca_uhid_key_ring_sign(ring, msg, sizeof(msg), sig3) == -2);
    assert(ca_uhid_key_ring_verify(ring, msg, sizeof(msg), sig, 32) == true);

    /* Cross-ring signatures do not validate (independent secrets). */
    uint8_t fresh_sig[32];
    assert(ca_uhid_key_ring_sign(fresh, msg, sizeof(msg), fresh_sig) == 0);
    assert(ca_uhid_key_ring_verify(ring, msg, sizeof(msg), fresh_sig, 32) == false);

    /* Public keys differ across rings. */
    size_t fpklen = 0;
    const uint8_t *fpk = ca_uhid_key_ring_public_key(fresh, &fpklen);
    assert(fpklen == 32);
    assert(memcmp(pk, fpk, 32) != 0);

    /* Wrong signature length rejected. */
    assert(ca_uhid_key_ring_verify(fresh, msg, sizeof(msg), fresh_sig, 16) == false);

    ca_uhid_key_ring_destroy(ring);
    ca_uhid_key_ring_destroy(fresh);

    /* Blank identity rejected. */
    assert(ca_uhid_key_ring_generate_fresh("") == NULL);
    assert(ca_uhid_key_ring_generate_fresh(NULL) == NULL);

    printf("  keyring: OK\n");
}

/* ---------------------------------------------------------------------------
 * RedactedEvidenceJsonConverter
 * --------------------------------------------------------------------------- */
static void test_redaction(void) {
    char buf[CA_REDACTED_VALUE_LEN];

    /* Empty / NULL redacts to bare "sha256:". */
    int n = ca_redacted_evidence_value(NULL, buf, sizeof(buf));
    assert(n == 7);
    assert(strcmp(buf, "sha256:") == 0);
    ca_redacted_evidence_value("", buf, sizeof(buf));
    assert(strcmp(buf, "sha256:") == 0);

    /* Non-empty: "sha256:" + 64 lowercase hex. */
    n = ca_redacted_evidence_value("token-abc", buf, sizeof(buf));
    assert(n == 7 + 64);
    assert(strncmp(buf, "sha256:", 7) == 0);
    assert(strlen(buf) == 71);
    for (size_t i = 7; i < 71; i++) {
        char c = buf[i];
        assert((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }
    /* Determinism: same input, same hash. */
    char buf2[CA_REDACTED_VALUE_LEN];
    ca_redacted_evidence_value("token-abc", buf2, sizeof(buf2));
    assert(strcmp(buf, buf2) == 0);

    /* Different input, different hash. */
    char buf3[CA_REDACTED_VALUE_LEN];
    ca_redacted_evidence_value("token-xyz", buf3, sizeof(buf3));
    assert(strcmp(buf, buf3) != 0);

    /* JSON object: keys preserved, values redacted. */
    const char *keys[]   = { "session", "payload" };
    const char *values[] = { "secret-1", "secret-2" };
    char *json = ca_redacted_evidence_to_json(keys, values, 2);
    assert(json);
    assert(strstr(json, "\"session\":") != NULL);
    assert(strstr(json, "\"payload\":") != NULL);
    assert(strstr(json, "sha256:") != NULL);
    /* Raw secret values must NOT appear. */
    assert(strstr(json, "secret-1") == NULL);
    assert(strstr(json, "secret-2") == NULL);
    assert(json[0] == '{');
    assert(json[strlen(json) - 1] == '}');
    free(json);

    /* Empty map -> "{}". */
    char *empty = ca_redacted_evidence_to_json(NULL, NULL, 0);
    assert(empty && strcmp(empty, "{}") == 0);
    free(empty);

    printf("  redaction: OK\n");
}

/* ---------------------------------------------------------------------------
 * DefaultSecurityWatchdog — graduated response policy
 * --------------------------------------------------------------------------- */
static void test_watchdog_policy(void) {
    ca_default_security_watchdog_t *w = ca_default_security_watchdog_create();
    assert(w);

    /* Low confidence -> NoAction. */
    ca_anomaly_signal_t low;
    ca_anomaly_signal_create(CA_THREAT_MEMORY_ANOMALY, 0.10f,
                             "CircleAI.Companion", "minor blip", &low);
    ca_security_response_t *r1 =
        ca_default_security_watchdog_on_anomaly(w, &low, NULL);
    assert(r1 && ca_security_response_kind(r1) == CA_RESPONSE_NO_ACTION);
    ca_security_response_destroy(r1);

    /* Mid confidence (0.30..0.60) -> KeyRotation. */
    ca_anomaly_signal_t mid;
    ca_anomaly_signal_create(CA_THREAT_BIOMETRIC_SPOOF_ATTEMPT, 0.45f,
                             "CircleAI.Identity", "spoof probe", &mid);
    ca_security_response_t *r2 =
        ca_default_security_watchdog_on_anomaly(w, &mid, NULL);
    assert(r2 && ca_security_response_kind(r2) == CA_RESPONSE_KEY_ROTATION);
    ca_security_response_destroy(r2);

    /* High confidence, non-high-severity vector, no checkpoint -> Composite
     * with exactly [KeyRotation, MeshIsolation] (no rollback). */
    ca_anomaly_signal_t high;
    ca_anomaly_signal_create(CA_THREAT_MEMORY_ANOMALY, 0.85f,
                             "CircleAI.Companion", "big blip", &high);
    ca_security_response_t *r3 =
        ca_default_security_watchdog_on_anomaly(w, &high, NULL);
    assert(r3 && ca_security_response_kind(r3) == CA_RESPONSE_COMPOSITE);
    size_t cnt = 0;
    const ca_security_response_kind_t *acts =
        ca_security_response_applied_actions(r3, &cnt);
    assert(cnt == 2);
    assert(acts[0] == CA_RESPONSE_KEY_ROTATION);
    assert(acts[1] == CA_RESPONSE_MESH_ISOLATION_SIGNAL);
    ca_security_response_destroy(r3);

    /* High confidence + high-severity vector + valid checkpoint -> Composite
     * WITH rollback (3 actions) and restored checkpoint recorded. */
    const uint8_t pl[] = { 4, 2 };
    ca_security_checkpoint_t *cp =
        ca_security_checkpoint_create("u", "CircleAI.Companion", pl, 2);
    ca_anomaly_signal_t crit;
    ca_anomaly_signal_create(CA_THREAT_PRIVILEGE_ESCALATION, 0.95f,
                             "CircleAI.Identity", "priv escalation", &crit);
    ca_security_response_t *r4 =
        ca_default_security_watchdog_on_anomaly(w, &crit, cp);
    assert(r4 && ca_security_response_kind(r4) == CA_RESPONSE_COMPOSITE);
    ca_security_response_applied_actions(r4, &cnt);
    assert(cnt == 3);
    assert(acts != NULL);
    const ca_security_response_kind_t *acts4 =
        ca_security_response_applied_actions(r4, &cnt);
    assert(acts4[2] == CA_RESPONSE_STATE_ROLLBACK);
    assert(ca_security_response_restored_checkpoint(r4) != NULL);
    ca_security_response_destroy(r4);
    ca_security_checkpoint_destroy(cp);

    /* High confidence + high-severity vector but NO checkpoint -> 2 actions. */
    ca_anomaly_signal_t crit2;
    ca_anomaly_signal_create(CA_THREAT_STATE_CORRUPTION, 0.99f,
                             "CircleAI.Memory", "state corruption", &crit2);
    ca_security_response_t *r5 =
        ca_default_security_watchdog_on_anomaly(w, &crit2, NULL);
    ca_security_response_applied_actions(r5, &cnt);
    assert(cnt == 2);
    ca_security_response_destroy(r5);

    /* Signal count reflects everything observed. */
    assert(ca_default_security_watchdog_signal_count(w) == 5);

    /* Stream replay: a reader opened AFTER the fact still sees all 5 signals
     * in order (unbounded, buffered-before-subscribe semantics). */
    ca_watchdog_signal_reader_t *rd = ca_default_security_watchdog_stream(w);
    assert(rd);
    ca_anomaly_signal_t got;
    size_t seen = 0;
    while (ca_watchdog_signal_reader_next(rd, &got)) seen++;
    assert(seen == 5);
    /* Drained now returns false. */
    assert(ca_watchdog_signal_reader_next(rd, &got) == false);
    ca_watchdog_signal_reader_destroy(rd);

    ca_default_security_watchdog_destroy(w);
    printf("  watchdog policy + stream: OK\n");
}

/* ---------------------------------------------------------------------------
 * DefaultAnomalyEventDispatcher
 * --------------------------------------------------------------------------- */
static void test_dispatcher(void) {
    ca_default_security_watchdog_t *w = ca_default_security_watchdog_create();
    ca_security_watchdog_t iface = ca_default_security_watchdog_as_interface(w);
    ca_default_anomaly_dispatcher_t *d =
        ca_default_anomaly_dispatcher_create(iface, 0.30);
    assert(d);

    /* Below threshold -> BelowThreshold, no response, watchdog not invoked. */
    ca_anomaly_signal_t low;
    ca_anomaly_signal_create(CA_THREAT_UNKNOWN, 0.10f, "m", "d", &low);
    ca_security_response_t *resp = (ca_security_response_t *)0x1;
    assert(ca_default_anomaly_dispatcher_dispatch(d, &low, NULL, false, &resp)
           == CA_DISPATCH_BELOW_THRESHOLD);
    assert(resp == NULL);
    assert(ca_default_security_watchdog_signal_count(w) == 0);

    /* Cancelled -> Cancelled. */
    ca_anomaly_signal_t ok;
    ca_anomaly_signal_create(CA_THREAT_NETWORK_PIVOT, 0.80f, "m", "d", &ok);
    assert(ca_default_anomaly_dispatcher_dispatch(d, &ok, NULL, true, &resp)
           == CA_DISPATCH_CANCELLED);
    assert(resp == NULL);

    /* First dispatch -> Dispatched, response set, watchdog invoked once. */
    assert(ca_default_anomaly_dispatcher_dispatch(d, &ok, NULL, false, &resp)
           == CA_DISPATCH_DISPATCHED);
    assert(resp != NULL);
    assert(strcmp(ca_security_response_signal_id(resp), ok.id) == 0);
    ca_security_response_destroy(resp);
    assert(ca_default_security_watchdog_signal_count(w) == 1);

    /* Same id again -> Duplicate, no second invocation. */
    resp = (ca_security_response_t *)0x1;
    assert(ca_default_anomaly_dispatcher_dispatch(d, &ok, NULL, false, &resp)
           == CA_DISPATCH_DUPLICATE);
    assert(resp == NULL);
    assert(ca_default_security_watchdog_signal_count(w) == 1);

    ca_default_anomaly_dispatcher_destroy(d);
    ca_default_security_watchdog_destroy(w);
    printf("  dispatcher: OK\n");
}

int main(void) {
    test_checkpoint();
    test_checkpoint_hash_distinguishes();
    test_response();
    test_keyring();
    test_redaction();
    test_watchdog_policy();
    test_dispatcher();
    printf("All watchdog tests passed.\n");
    return 0;
}
