#ifndef CIRCLE_AI_WATCHDOG_H
#define CIRCLE_AI_WATCHDOG_H

/*
 * watchdog.h — CircleAI local runtime immune system (C11 port).
 *
 * Ports the local-runtime half of CircleAI.Security:
 *   SecurityResponseKind / SecurityResponse   (SecurityResponse.cs)
 *   SecurityCheckpoint                         (SecurityCheckpoint.cs)
 *   UhidKeyRing                                (UhidKeyRing.cs)
 *   ISecurityWatchdog + DefaultSecurityWatchdog(ISecurityWatchdog.cs)
 *   IAnomalyEventDispatcher + Default...       (IAnomalyEventDispatcher.cs)
 *   RedactedEvidenceJsonConverter              (RedactedEvidenceJsonConverter.cs)
 *
 * AnomalySignal / ThreatVector already live in security.h and are reused
 * verbatim here.
 *
 * Conventions: ca_ prefix, _t types, opaque create/destroy handles,
 * strdup-owning fields with matching *_free, deep-copy getters, errors via
 * NULL / negative rc. In-memory + deterministic; no pthreads. The watchdog's
 * signal stream is an unbounded growable FIFO drained cursor-wise — publishes
 * NEVER block and messages emitted before a reader attaches are retained
 * (mirrors the C# unbounded Channel<AnomalySignal>).
 *
 * Crypto note: C# UhidKeyRing uses ECDSA P-256 (chosen for BCL interop, NOT
 * wire parity — see UhidKeyRing.cs header). The C port has no BCL/ECDSA, so
 * signing is a deterministic keyed HMAC-SHA256 (SHA-256 already ships in
 * model_runtime). The full CONTRACT is preserved bit-for-bit: fresh random
 * ring id + key material on generate/rotate, sign fails after revoke, verify
 * survives revoke, public-key bytes exported, rotate returns a fresh ring and
 * leaves the old one revoked.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "security.h" /* ca_anomaly_signal_t, ca_threat_vector_t, CA_UUID_STR_LEN */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * SecurityResponseKind — SecurityResponse.cs
 * =========================================================================== */

typedef enum {
    CA_RESPONSE_NO_ACTION            = 0,
    CA_RESPONSE_KEY_ROTATION         = 1,
    CA_RESPONSE_SESSION_REVOCATION   = 2,
    CA_RESPONSE_MESH_ISOLATION_SIGNAL = 3,
    CA_RESPONSE_STATE_ROLLBACK       = 4,
    CA_RESPONSE_COMPOSITE            = 5
} ca_security_response_kind_t;

/* ===========================================================================
 * SecurityCheckpoint — immutable, self-verifying state snapshot.
 * =========================================================================== */

typedef struct ca_security_checkpoint ca_security_checkpoint_t;

/*
 * Create a checkpoint over an opaque payload. Computes the SHA-256 of the
 * payload at creation time (verified on ca_security_checkpoint_verify).
 *   uhid_identity_id / module_label : must be non-NULL / non-blank
 *   payload / payload_len           : payload may be NULL only when len == 0
 * Returns NULL on invalid args or OOM. Destroy with
 * ca_security_checkpoint_destroy.
 */
ca_security_checkpoint_t *ca_security_checkpoint_create(
    const char    *uhid_identity_id,
    const char    *module_label,
    const uint8_t *payload,
    size_t         payload_len);

void ca_security_checkpoint_destroy(ca_security_checkpoint_t *cp);

/* Deep copy (deep-copies payload + hash). NULL on OOM / NULL input. */
ca_security_checkpoint_t *ca_security_checkpoint_copy(
    const ca_security_checkpoint_t *cp);

/* true iff the current SHA-256 of the payload matches the stored hash
 * (constant-time compare). false if cp is NULL. */
bool ca_security_checkpoint_verify(const ca_security_checkpoint_t *cp);

/* Accessors — returned pointers are owned by cp; do not free. */
const char    *ca_security_checkpoint_id(const ca_security_checkpoint_t *cp);
const char    *ca_security_checkpoint_uhid(const ca_security_checkpoint_t *cp);
const char    *ca_security_checkpoint_module(const ca_security_checkpoint_t *cp);
const uint8_t *ca_security_checkpoint_payload(const ca_security_checkpoint_t *cp,
                                              size_t *out_len);
/* 32-byte SHA-256 stored at creation time. */
const uint8_t *ca_security_checkpoint_payload_hash(
    const ca_security_checkpoint_t *cp);
int64_t ca_security_checkpoint_created_at_ms(const ca_security_checkpoint_t *cp);

/*
 * Non-sensitive textual form (payload bytes NEVER emitted; only first 8 hash
 * bytes as hex). Writes up to buf_size-1 chars + NUL into buf. Returns the
 * number of chars written (excluding NUL), or -1 if buf is NULL/buf_size 0.
 */
int ca_security_checkpoint_to_string(const ca_security_checkpoint_t *cp,
                                     char *buf, size_t buf_size);

/* ===========================================================================
 * SecurityResponse — action taken by the watchdog for a signal.
 * =========================================================================== */

typedef struct ca_security_response ca_security_response_t;

/* Factories mirror the C# static constructors. signal_id is copied.
 * All return NULL only on OOM. Destroy with ca_security_response_destroy. */
ca_security_response_t *ca_security_response_no_action(
    const char *signal_id, const char *reason);
ca_security_response_t *ca_security_response_for_key_rotation(
    const char *signal_id, const char *description);
/* Records the restored checkpoint by deep copy. restored must be non-NULL. */
ca_security_response_t *ca_security_response_for_rollback(
    const char *signal_id, const ca_security_checkpoint_t *restored);
/* Composite: actions[] is copied. restored_checkpoint may be NULL. */
ca_security_response_t *ca_security_response_composite(
    const char                        *signal_id,
    const ca_security_response_kind_t *actions,
    size_t                             action_count,
    const char                        *description,
    const ca_security_checkpoint_t    *restored_checkpoint);

void ca_security_response_destroy(ca_security_response_t *r);
ca_security_response_t *ca_security_response_copy(const ca_security_response_t *r);

const char *ca_security_response_signal_id(const ca_security_response_t *r);
ca_security_response_kind_t ca_security_response_kind(
    const ca_security_response_t *r);
/* Applied actions (populated only for Composite). Returns array + count. */
const ca_security_response_kind_t *ca_security_response_applied_actions(
    const ca_security_response_t *r, size_t *out_count);
const char *ca_security_response_description(const ca_security_response_t *r);
/* Restored checkpoint (owned by r) or NULL. */
const ca_security_checkpoint_t *ca_security_response_restored_checkpoint(
    const ca_security_response_t *r);
int64_t ca_security_response_responded_at_ms(const ca_security_response_t *r);

/* ===========================================================================
 * UhidKeyRing — ephemeral session key ring (deterministic HMAC-SHA256).
 * =========================================================================== */

typedef struct ca_uhid_key_ring ca_uhid_key_ring_t;

/* Create a fresh ring for uhid_identity_id (must be non-blank). NULL on
 * invalid arg / OOM. Destroy with ca_uhid_key_ring_destroy. */
ca_uhid_key_ring_t *ca_uhid_key_ring_generate_fresh(const char *uhid_identity_id);
void ca_uhid_key_ring_destroy(ca_uhid_key_ring_t *ring);

/*
 * Rotate: revokes this ring and returns a NEW ring for the same identity.
 * This instance remains revoked (still valid for Verify). NULL on OOM.
 */
ca_uhid_key_ring_t *ca_uhid_key_ring_rotate(ca_uhid_key_ring_t *ring);

/* Revoke: after this, Sign fails; Verify still works. Idempotent. */
void ca_uhid_key_ring_revoke(ca_uhid_key_ring_t *ring);

/*
 * Sign data[len] into a 32-byte MAC written to out_sig[32].
 * Returns 0 on success; -1 on bad args; -2 if the ring is revoked.
 */
int ca_uhid_key_ring_sign(ca_uhid_key_ring_t *ring,
                          const uint8_t *data, size_t len,
                          uint8_t out_sig[32]);

/* Verify a 32-byte signature over data[len]. Works after revoke. true on
 * match, false otherwise (or on bad args). */
bool ca_uhid_key_ring_verify(const ca_uhid_key_ring_t *ring,
                             const uint8_t *data, size_t len,
                             const uint8_t *signature, size_t sig_len);

const char *ca_uhid_key_ring_id(const ca_uhid_key_ring_t *ring);       /* UUID */
const char *ca_uhid_key_ring_uhid(const ca_uhid_key_ring_t *ring);
bool        ca_uhid_key_ring_is_revoked(const ca_uhid_key_ring_t *ring);
int64_t     ca_uhid_key_ring_generated_at_ms(const ca_uhid_key_ring_t *ring);
/* -1 when not revoked, else the revocation epoch ms. */
int64_t     ca_uhid_key_ring_revoked_at_ms(const ca_uhid_key_ring_t *ring);
/* Public key bytes (owned by ring). NULL/len 0 only on internal error. */
const uint8_t *ca_uhid_key_ring_public_key(const ca_uhid_key_ring_t *ring,
                                           size_t *out_len);

/* ===========================================================================
 * RedactedEvidenceJsonConverter — evidence redaction helper.
 *
 * Serialises an evidence map so every VALUE is replaced by
 * "sha256:" + lowercase-hex(SHA-256(utf8(value))). Empty/NULL values redact to
 * "sha256:" with no hex (matches the C# HashRedacted null/empty branch).
 * Keys are preserved verbatim, in insertion order.
 * =========================================================================== */

/*
 * Redact a single value into buf. Writes "sha256:" + 64 hex chars (or bare
 * "sha256:" for NULL/empty) + NUL. buf must be >= 72 bytes
 * (CA_REDACTED_VALUE_LEN). Returns chars written excl. NUL, or -1 on bad args.
 */
#define CA_REDACTED_VALUE_LEN 72 /* "sha256:" (7) + 64 hex + NUL */
int ca_redacted_evidence_value(const char *raw, char *buf, size_t buf_size);

/*
 * Serialise a parallel keys[]/values[] map (count entries) as a JSON object
 * with every value redacted. Returns a heap string the caller must free(),
 * or NULL on OOM. count==0 yields "{}".
 */
char *ca_redacted_evidence_to_json(const char *const *keys,
                                   const char *const *values,
                                   size_t count);

/* ===========================================================================
 * ISecurityWatchdog + DefaultSecurityWatchdog
 *
 * The watchdog interface is a vtable so a host can inject its own. The default
 * implementation ships as an opaque handle exposing the same operations plus a
 * cursor-drained signal stream.
 * =========================================================================== */

/* Vtable form of ISecurityWatchdog (for host substitution). */
typedef struct {
    void *self;
    /* Returns an OWNED ca_security_response_t* the caller must destroy, or
     * NULL on failure. checkpoint may be NULL. */
    ca_security_response_t *(*on_anomaly_detected)(
        void *self,
        const ca_anomaly_signal_t      *signal,
        const ca_security_checkpoint_t *checkpoint);
} ca_security_watchdog_t;

typedef struct ca_default_security_watchdog ca_default_security_watchdog_t;

ca_default_security_watchdog_t *ca_default_security_watchdog_create(void);
void ca_default_security_watchdog_destroy(ca_default_security_watchdog_t *w);

/*
 * OnAnomalyDetectedAsync analogue. Applies the graduated response policy and
 * broadcasts the signal to the stream. checkpoint may be NULL.
 * Returns an OWNED response (destroy it) or NULL on bad args / OOM.
 */
ca_security_response_t *ca_default_security_watchdog_on_anomaly(
    ca_default_security_watchdog_t *w,
    const ca_anomaly_signal_t      *signal,
    const ca_security_checkpoint_t *checkpoint);

/* Adapt to the injectable vtable form. */
ca_security_watchdog_t ca_default_security_watchdog_as_interface(
    ca_default_security_watchdog_t *w);

/*
 * StreamSignalsAsync analogue — cursor-drained. Open a reader, then poll it;
 * every signal ever written (including before the reader opened) is delivered
 * in order. Multiple independent readers each see the full stream.
 */
typedef struct ca_watchdog_signal_reader ca_watchdog_signal_reader_t;
ca_watchdog_signal_reader_t *ca_default_security_watchdog_stream(
    ca_default_security_watchdog_t *w);
void ca_watchdog_signal_reader_destroy(ca_watchdog_signal_reader_t *r);
/*
 * Copy the next unread signal into *out and advance. Returns true when a
 * signal was produced, false when the stream is currently drained.
 */
bool ca_watchdog_signal_reader_next(ca_watchdog_signal_reader_t *r,
                                    ca_anomaly_signal_t *out);

/* Diagnostics: total signals observed since creation. */
size_t ca_default_security_watchdog_signal_count(
    const ca_default_security_watchdog_t *w);

/* ===========================================================================
 * IAnomalyEventDispatcher + DefaultAnomalyEventDispatcher
 * =========================================================================== */

typedef enum {
    CA_DISPATCH_DISPATCHED     = 0,
    CA_DISPATCH_DUPLICATE      = 1,
    CA_DISPATCH_BELOW_THRESHOLD = 2,
    CA_DISPATCH_UNVERIFIED     = 3,
    CA_DISPATCH_CANCELLED      = 4
} ca_anomaly_dispatch_outcome_t;

typedef struct ca_default_anomaly_dispatcher ca_default_anomaly_dispatcher_t;

/*
 * Wrap a watchdog vtable. minimum_confidence is clamped to [0,1] (default
 * 0.30 when out of range is NOT applied here — pass 0.30 explicitly to match
 * the C# default). The dispatcher does not own the watchdog.
 */
ca_default_anomaly_dispatcher_t *ca_default_anomaly_dispatcher_create(
    ca_security_watchdog_t watchdog, double minimum_confidence);
void ca_default_anomaly_dispatcher_destroy(
    ca_default_anomaly_dispatcher_t *d);

/*
 * VerifyAndDispatchAsync analogue. cancelled emulates a tripped token.
 *   - cancelled            -> CA_DISPATCH_CANCELLED
 *   - confidence < min     -> CA_DISPATCH_BELOW_THRESHOLD
 *   - signal id seen before -> CA_DISPATCH_DUPLICATE
 *   - otherwise            -> CA_DISPATCH_DISPATCHED, *out_response set (OWNED)
 * out_response may be NULL if the caller does not want the response. When set
 * and outcome != Dispatched, *out_response is set to NULL.
 * Returns the outcome; on OOM returns CA_DISPATCH_UNVERIFIED with no response.
 */
ca_anomaly_dispatch_outcome_t ca_default_anomaly_dispatcher_dispatch(
    ca_default_anomaly_dispatcher_t *d,
    const ca_anomaly_signal_t       *signal,
    const ca_security_checkpoint_t  *checkpoint,
    bool                             cancelled,
    ca_security_response_t         **out_response);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_WATCHDOG_H */
