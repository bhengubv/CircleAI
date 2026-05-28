/*
 * test_anomaly_signal.c — AnomalySignal factory tests.
 *
 * Verifies:
 *   - Confidence clamping per spec ([0, 1] inclusive, pass-through nominal)
 *   - Stable ThreatVector ordinals (0..7) across language ports
 *   - UUID v4 stamping (length 36, version nibble '4', variant nibble in [89ab])
 *   - Timestamp populated to a non-zero recent epoch ms
 *   - NULL out_signal returns -1 without dereference
 *   - NULL string inputs become empty C strings
 *
 * Exits 0 on success; asserts on first failure.
 */

#include <assert.h>
#include <stdio.h>
#include <string.h>
#include <time.h>

#include "circle_ai/circle_ai.h"

static void check_clamp(const char *id, float input, float expected) {
    ca_anomaly_signal_t sig;
    int rc = ca_anomaly_signal_create(
        CA_THREAT_MEMORY_ANOMALY, input,
        "Circle.AI.Test", "clamp probe", &sig);
    if (rc != 0) {
        fprintf(stderr, "FAIL [%s]: factory returned %d\n", id, rc);
        assert(0);
    }
    if (sig.confidence != expected) {
        fprintf(stderr, "FAIL [%s]: input %.4f -> got %.8f, expected %.8f\n",
                id, (double)input, (double)sig.confidence, (double)expected);
        assert(0);
    }
}

static void check_uuid_v4_shape(const char *uuid) {
    /* Length: 36 chars + NUL */
    size_t len = strlen(uuid);
    if (len != 36) {
        fprintf(stderr, "FAIL uuid len: got %zu, expected 36 (value=%s)\n", len, uuid);
        assert(0);
    }
    /* Dashes at positions 8, 13, 18, 23 */
    assert(uuid[8]  == '-');
    assert(uuid[13] == '-');
    assert(uuid[18] == '-');
    assert(uuid[23] == '-');

    /* Version nibble (position 14) must be '4' */
    if (uuid[14] != '4') {
        fprintf(stderr, "FAIL uuid version: expected '4' at pos 14, got '%c'\n", uuid[14]);
        assert(0);
    }
    /* Variant nibble (position 19) must be 8, 9, a, or b */
    char vn = uuid[19];
    if (!(vn == '8' || vn == '9' || vn == 'a' || vn == 'b')) {
        fprintf(stderr, "FAIL uuid variant: got '%c'\n", vn);
        assert(0);
    }
    /* All other characters must be lower-case hex */
    for (size_t i = 0; i < 36; i++) {
        if (i == 8 || i == 13 || i == 18 || i == 23) continue;
        char c = uuid[i];
        int hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
        if (!hex) {
            fprintf(stderr, "FAIL uuid hex: invalid char '%c' at %zu (value=%s)\n",
                    c, i, uuid);
            assert(0);
        }
    }
}

int main(void) {
    /* -----------------------------------------------------------------------
     * Confidence clamp vectors per spec
     * ----------------------------------------------------------------------- */
    check_clamp("above_max", 1.5f,  1.0f);
    check_clamp("below_min", -0.3f, 0.0f);
    check_clamp("at_max",    1.0f,  1.0f);
    check_clamp("at_min",    0.0f,  0.0f);
    check_clamp("nominal",   0.7f,  0.7f);

    /* -----------------------------------------------------------------------
     * ThreatVector ordinals — stable across all language ports
     * ----------------------------------------------------------------------- */
    assert(CA_THREAT_MEMORY_ANOMALY          == 0);
    assert(CA_THREAT_CONTROL_FLOW_DRIFT      == 1);
    assert(CA_THREAT_PRIVILEGE_ESCALATION    == 2);
    assert(CA_THREAT_BIOMETRIC_SPOOF_ATTEMPT == 3);
    assert(CA_THREAT_NETWORK_PIVOT           == 4);
    assert(CA_THREAT_STATE_CORRUPTION        == 5);
    assert(CA_THREAT_AGENT_PATCH_REJECTED    == 6);
    assert(CA_THREAT_UNKNOWN                 == 7);

    /* -----------------------------------------------------------------------
     * Full-field factory round-trip
     * ----------------------------------------------------------------------- */
    ca_anomaly_signal_t sig;
    int rc = ca_anomaly_signal_create(
        CA_THREAT_BIOMETRIC_SPOOF_ATTEMPT,
        0.82f,
        "Circle.AI.Identity",
        "Liveness check failed for embedding-vector replay.",
        &sig);
    assert(rc == 0);
    assert(sig.vector == CA_THREAT_BIOMETRIC_SPOOF_ATTEMPT);
    assert(sig.confidence == 0.82f);
    assert(strcmp(sig.affected_module, "Circle.AI.Identity") == 0);
    assert(strcmp(sig.description,
                  "Liveness check failed for embedding-vector replay.") == 0);
    assert(sig.detected_at_unix_ms > 0);

    /* Reasonable recency — within +/- 1 day of `now`.  This is loose to
     * survive build-host clock drift without becoming a flaky test. */
    int64_t now_ms = (int64_t)time(NULL) * 1000LL;
    int64_t delta  = sig.detected_at_unix_ms - now_ms;
    if (delta < 0) delta = -delta;
    assert(delta < 86400000LL);

    check_uuid_v4_shape(sig.id);

    /* -----------------------------------------------------------------------
     * Uniqueness — two consecutive signals must not collide
     * ----------------------------------------------------------------------- */
    ca_anomaly_signal_t a, b;
    ca_anomaly_signal_create(CA_THREAT_UNKNOWN, 0.5f, "m", "d", &a);
    ca_anomaly_signal_create(CA_THREAT_UNKNOWN, 0.5f, "m", "d", &b);
    if (strcmp(a.id, b.id) == 0) {
        fprintf(stderr, "FAIL uuid uniqueness: %s collides with itself\n", a.id);
        assert(0);
    }

    /* -----------------------------------------------------------------------
     * NULL out_signal → returns -1, no crash
     * ----------------------------------------------------------------------- */
    rc = ca_anomaly_signal_create(CA_THREAT_UNKNOWN, 0.5f, "m", "d", NULL);
    assert(rc == -1);

    /* -----------------------------------------------------------------------
     * NULL string inputs become empty strings
     * ----------------------------------------------------------------------- */
    ca_anomaly_signal_t nul_sig;
    rc = ca_anomaly_signal_create(CA_THREAT_UNKNOWN, 0.5f, NULL, NULL, &nul_sig);
    assert(rc == 0);
    assert(nul_sig.affected_module[0] == '\0');
    assert(nul_sig.description[0]     == '\0');

    /* -----------------------------------------------------------------------
     * Truncation discipline — over-long inputs must still null-terminate
     * ----------------------------------------------------------------------- */
    char long_mod[CA_MODULE_NAME_LEN + 32];
    memset(long_mod, 'M', sizeof(long_mod));
    long_mod[sizeof(long_mod) - 1] = '\0';

    char long_desc[CA_DESC_LEN + 64];
    memset(long_desc, 'D', sizeof(long_desc));
    long_desc[sizeof(long_desc) - 1] = '\0';

    ca_anomaly_signal_t trunc;
    rc = ca_anomaly_signal_create(CA_THREAT_STATE_CORRUPTION, 0.4f,
                                  long_mod, long_desc, &trunc);
    assert(rc == 0);
    assert(strlen(trunc.affected_module) == CA_MODULE_NAME_LEN - 1);
    assert(strlen(trunc.description)     == CA_DESC_LEN - 1);
    assert(trunc.affected_module[CA_MODULE_NAME_LEN - 1] == '\0');
    assert(trunc.description[CA_DESC_LEN - 1]           == '\0');

    /* -----------------------------------------------------------------------
     * Direct UUID helper — same shape checks
     * ----------------------------------------------------------------------- */
    char uuid_direct[CA_UUID_STR_LEN];
    ca_uuid_v4(uuid_direct);
    check_uuid_v4_shape(uuid_direct);

    printf("All anomaly signal tests passed.\n");
    return 0;
}
