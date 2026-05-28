#ifndef CIRCLE_AI_SECURITY_H
#define CIRCLE_AI_SECURITY_H

/*
 * security.h — ThreatVector + AnomalySignal.
 *
 * Portable C11 schema half of the Circle AI security pipeline. The watchdog
 * implementation stays C# host-side; every language port carries identical
 * detection types so signals serialise 1:1 across the network.
 *
 * Ordinals on ca_threat_vector_t are STABLE across language ports — never
 * reorder. New values must be appended at the end. CA_THREAT_UNKNOWN is
 * preserved at ordinal 7 as the explicit fall-through sentinel.
 *
 * Note: unlike richer ports, the C struct uses fixed-size buffers and omits
 * the evidence map for portability. Callers that need a key/value evidence
 * bag can layer one on top of ca_anomaly_signal_t.
 */

#include <stdint.h>

/* ---------------------------------------------------------------------------
 * ThreatVector — stable ordinals across language ports
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_THREAT_MEMORY_ANOMALY          = 0,
    CA_THREAT_CONTROL_FLOW_DRIFT      = 1,
    CA_THREAT_PRIVILEGE_ESCALATION    = 2,
    CA_THREAT_BIOMETRIC_SPOOF_ATTEMPT = 3,
    CA_THREAT_NETWORK_PIVOT           = 4,
    CA_THREAT_STATE_CORRUPTION        = 5,
    CA_THREAT_AGENT_PATCH_REJECTED    = 6,
    CA_THREAT_UNKNOWN                 = 7
} ca_threat_vector_t;

/* ---------------------------------------------------------------------------
 * AnomalySignal — fixed-size portable schema.
 *
 * Buffer sizes:
 *   id              : 36-char UUID v4 + NUL
 *   affected_module : up to 63 chars + NUL (e.g. "Circle.AI.Companion")
 *   description     : up to 255 chars + NUL
 * Strings are always null-terminated; truncation silently occurs on overflow.
 * --------------------------------------------------------------------------- */

#define CA_UUID_STR_LEN     37   /* 36 hex/dash characters + NUL */
#define CA_MODULE_NAME_LEN  64
#define CA_DESC_LEN         256

typedef struct {
    char                id[CA_UUID_STR_LEN];           /* UUID v4 string */
    ca_threat_vector_t  vector;
    float               confidence;                    /* clamped to [0, 1] */
    char                affected_module[CA_MODULE_NAME_LEN];
    char                description[CA_DESC_LEN];
    int64_t             detected_at_unix_ms;           /* epoch ms UTC */
} ca_anomaly_signal_t;

/* ---------------------------------------------------------------------------
 * Factory
 *
 * Stamps a fresh UUID v4 into out_signal->id, clamps confidence to [0, 1],
 * copies affected_module/description (truncating to fit), and records the
 * current UTC time in detected_at_unix_ms.
 *
 * Parameters:
 *   vector           — threat classification
 *   confidence       — likelihood in [0, 1] (clamped if out of range)
 *   affected_module  — null-terminated; may be NULL (treated as "")
 *   description      — null-terminated; may be NULL (treated as "")
 *   out_signal       — output struct; must not be NULL
 *
 * Returns 0 on success, -1 if out_signal is NULL.
 * --------------------------------------------------------------------------- */

int ca_anomaly_signal_create(
    ca_threat_vector_t   vector,
    float                confidence,
    const char          *affected_module,
    const char          *description,
    ca_anomaly_signal_t *out_signal
);

/* ---------------------------------------------------------------------------
 * UUID v4 helper — populates a 37-char buffer (36 chars + NUL) with a
 * random version-4 / variant-1 UUID. Uses BCryptGenRandom on Windows or
 * /dev/urandom on POSIX; falls back to rand() only when both fail.
 *
 * Buffer size: caller must pass a buffer of at least CA_UUID_STR_LEN bytes.
 * --------------------------------------------------------------------------- */

void ca_uuid_v4(char out_buf[CA_UUID_STR_LEN]);

#endif /* CIRCLE_AI_SECURITY_H */
