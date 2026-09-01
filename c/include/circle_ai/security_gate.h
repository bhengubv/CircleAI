#ifndef CIRCLE_AI_SECURITY_GATE_H
#define CIRCLE_AI_SECURITY_GATE_H

/*
 * security_gate.h - CircleAI.Security.Antibodies, the authorization gate.
 *
 * security_antibodies.h next door does the assessing. This is what stands in
 * front of it and decides whether an assessment may happen at all.
 *
 * WHY A GATE EXISTS AT ALL. Every capability behind it reads something private
 * to answer a question: a file's contents, the address somebody is about to
 * connect to, the email address they signed up with. Those are exactly the
 * reads a hostile version of this component would want, and "it is for your own
 * protection" is what that version would say too. The gate is the difference
 * between the two, and it is a mechanism rather than a policy so it cannot be
 * argued away.
 *
 * THREE RULES, AND NONE OF THEM HAS AN OVERRIDE.
 *
 *   Consent is per CAPABILITY, not per component. Agreeing to have a download
 *   checked is not agreeing to have your email address looked up.
 *
 *   Consent EXPIRES, and an unbounded grant cannot be constructed. A permission
 *   that never lapses is one nobody remembers giving.
 *
 *   Consent is ATTRIBUTED. Who granted it and for what scope are required
 *   fields, because "the system consented" is how this becomes surveillance
 *   with a changelog.
 *
 * The default gate DENIES. A host that wires nothing gets a component that
 * assesses nothing, which is the only safe way for this particular one to fail.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* ca_threat_severity_t and the corpus seam live next door. */
#include "circle_ai/security_antibodies.h"

#ifdef __cplusplus
extern "C" {
#endif

/* -- what may be asked ---------------------------------------------------- */

typedef enum {
    /* "Is a file the user is about to open known-bad?" Assesses a file by its
     * hash against the device's LOCAL corpus and warns before they open it.
     * Reframed from malware intelligence into a pre-open warning about
     * somebody's own downloads. */
    CA_ANTIBODY_CAPABILITY_FILE_REPUTATION_AWARENESS = 0,
    /* "Is a URL, IP or domain the user is about to trust known-bad?" A
     * pre-connect warning, not a block. */
    CA_ANTIBODY_CAPABILITY_NETWORK_INDICATOR_AWARENESS,
    /* "Has the user's OWN identity turned up in a breach corpus?" Hashes the
     * identity and checks the local set so an exposed credential can be
     * rotated. Their own identity ONLY - the capability does not exist for
     * looking up anybody else. */
    CA_ANTIBODY_CAPABILITY_BREACH_EXPOSURE_AWARENESS
} ca_antibody_capability_t;

const char *ca_antibody_capability_name(ca_antibody_capability_t capability);

/* -- consent -------------------------------------------------------------- */

typedef struct {
    char *consent_id;
    ca_antibody_capability_t capability;
    /* Who granted it. Required. */
    char *granted_by;
    /* What it covers - a directory, an account, a device. Required. */
    char *scope;
    int64_t granted_at_unix;
    int64_t expires_at_unix;
} ca_authorized_use_consent_t;

void ca_authorized_use_consent_free(ca_authorized_use_consent_t *consent);

/*
 * Grants for a bounded duration starting now.
 *
 * Returns NULL for a blank granter, a blank scope, or a non-positive duration.
 * An unattributed or unbounded consent is not a stricter grant - it is a
 * permission that cannot be reviewed, revoked on schedule, or explained to the
 * person it was taken on behalf of.
 */
ca_authorized_use_consent_t *ca_authorized_use_consent_grant(
    ca_antibody_capability_t capability, const char *granted_by,
    const char *scope, int64_t duration_seconds, int64_t now_unix);

/* True only when this consent covers that capability AND now is inside the
 * window. Half-open: the expiry instant is already lapsed. */
bool ca_authorized_use_consent_is_active_for(const ca_authorized_use_consent_t *consent,
                                             ca_antibody_capability_t capability,
                                             int64_t now_unix);

typedef struct ca_authorized_use_consent_store {
    void *state;
    bool (*put)(void *state, const ca_authorized_use_consent_t *consent);
    /* Borrowed; NULL when nothing active covers it. */
    const ca_authorized_use_consent_t *(*find_active)(
        void *state, ca_antibody_capability_t capability, int64_t now_unix);
    /* Revocation is IMMEDIATE and there is no soft-delete. A consent somebody
     * withdrew must stop working the moment they say so. */
    bool (*revoke)(void *state, const char *consent_id);
    size_t (*count)(void *state);
    void (*free_fn)(void *state);
} ca_authorized_use_consent_store_t;

void ca_authorized_use_consent_store_free(ca_authorized_use_consent_store_t *store);

ca_authorized_use_consent_store_t *ca_authorized_use_consent_store_new(void);

/* -- asking --------------------------------------------------------------- */

typedef struct {
    ca_antibody_capability_t capability;
    /* What is being assessed. A hash, a host, a hashed identifier - never the
     * plain identity value, which is hashed before it reaches here. */
    char *subject;
    char *scope;
    char *requested_by;
    int64_t at_unix;
} ca_authorized_use_request_t;

void ca_authorized_use_request_free(ca_authorized_use_request_t *request);

typedef struct {
    bool allowed;
    /* ALWAYS populated, including when allowed. A decision without a reason
     * cannot be shown to the person it was made about, and this is the one
     * component where that is the whole point. */
    char *reason;
    char *consent_id;   /* NULL when denied */
    int64_t at_unix;
} ca_authorization_decision_t;

void ca_authorization_decision_free(ca_authorization_decision_t *decision);

typedef struct ca_authorized_use_gate {
    void *state;
    ca_authorization_decision_t *(*authorize)(void *state,
                                              const ca_authorized_use_request_t *request);
    void (*free_fn)(void *state);
} ca_authorized_use_gate_t;

void ca_authorized_use_gate_free(ca_authorized_use_gate_t *gate);

/* Denies everything, with a reason saying no gate is configured.
 *
 * THE DEFAULT. Not a test double: a host that wires nothing should get a
 * component that assesses nothing. The alternative default - allow when
 * unconfigured - is a capability that reads files because somebody forgot a
 * line of setup. */
ca_authorized_use_gate_t *ca_null_authorized_use_gate_new(void);

/* Allows only what an active, unexpired, matching consent covers. Takes the
 * store, does not own it. */
ca_authorized_use_gate_t *ca_explicit_consent_authorized_use_gate_new(
    ca_authorized_use_consent_store_t *store);

/* -- what came back ------------------------------------------------------- */

typedef enum {
    /* NO ASSESSMENT WAS PERFORMED - the gate denied it, or nothing ran. The
     * DEFAULT value, so an unset result reads as "nothing was checked" rather
     * than as a pass. */
    CA_THREAT_AWARENESS_NOT_ASSESSED = 0,
    /* Did not match anything known-bad in the local corpus. NOT a clean bill of
     * health: it means "no known threat", nothing stronger, and a UI that
     * renders it as "safe" is lying on the product's behalf. */
    CA_THREAT_AWARENESS_NO_KNOWN_THREAT,
    CA_THREAT_AWARENESS_SUSPICIOUS,
    CA_THREAT_AWARENESS_KNOWN_BAD
} ca_threat_awareness_verdict_t;

const char *ca_threat_awareness_verdict_name(ca_threat_awareness_verdict_t verdict);

typedef struct {
    ca_threat_severity_t severity;
    char *summary;
    char *source;
    int64_t at_unix;
    /* Where the assessment happened. Local by default and by design: asking a
     * remote service whether an address has been breached tells that service
     * the address AND that its owner is worried. */
    bool assessed_locally;
} ca_defensive_threat_context_t;

void ca_defensive_threat_context_free(ca_defensive_threat_context_t *context);

/* -- the system ----------------------------------------------------------- */

typedef struct ca_defensive_antibody_system {
    void *state;
    /* Each goes through the gate first and returns NOT_ASSESSED when refused -
     * never a verdict inferred from having been stopped. */
    ca_threat_awareness_verdict_t (*assess_file)(void *state, const char *path,
                                                 ca_defensive_threat_context_t *out_context);

    ca_threat_awareness_verdict_t (*assess_network)(void *state,
                                                    const char *host_or_address,
                                                    ca_defensive_threat_context_t *out_context);

    /* `identifier` is hashed before it leaves this call; the plain value is
     * never stored and never sent anywhere. */
    ca_threat_awareness_verdict_t (*assess_breach_exposure)(
        void *state, const char *identifier,
        ca_defensive_threat_context_t *out_context);

    void (*free_fn)(void *state);
} ca_defensive_antibody_system_t;

void ca_defensive_antibody_system_free(ca_defensive_antibody_system_t *system);

/*
 * The assembled system: gate in front, local corpus behind.
 *
 * AWARENESS, NEVER ENFORCEMENT. Nothing here blocks, quarantines or deletes.
 * Collapsing the two would put the component that can read your files in charge
 * of refusing them, and the blast radius of a false positive goes from a
 * notification to a device that will not open its owner's documents.
 */
ca_defensive_antibody_system_t *ca_defensive_antibody_system_new(
    ca_authorized_use_gate_t *gate, ca_local_indicator_corpus_t *corpus);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SECURITY_GATE_H */
