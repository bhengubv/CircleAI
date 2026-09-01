#ifndef CIRCLE_AI_SECURITY_ANTIBODIES_H
#define CIRCLE_AI_SECURITY_ANTIBODIES_H

/*
 * security_antibodies.h - CircleAI.Security.Antibodies (C11).
 *
 * Telling somebody what has happened to them: an address in a breach, a file
 * that looks dangerous, a network that is not what it claims.
 *
 * AWARENESS, NOT ENFORCEMENT. These report what they SEE and nothing acts on it
 * here. Collapsing the two would put the component that can read your files in
 * charge of blocking them, and the blast radius of a false positive goes from a
 * notification to a device that will not open its owner's documents.
 *
 * THE CORPUS IS LOCAL AND SO IS THE MATCHING. A device does not ask a remote
 * service "has this address been breached", because that question tells the
 * service the address AND that its owner is worried. An implementation that
 * must reach a service uses a k-anonymity prefix; the corpus seam exists so
 * that choice is visible rather than assumed away.
 *
 * NOTHING HERE IS A VERDICT. An assessment says what was observed and how
 * confident it is. "This file is safe" is a promise no local check can keep,
 * and a UI that renders one is lying on the product's behalf.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* How bad, on ONE scale, so three different sources can be compared at all. */
typedef enum {
    CA_THREAT_INFORMATIONAL = 0,
    CA_THREAT_LOW,
    CA_THREAT_MEDIUM,
    CA_THREAT_HIGH,
    CA_THREAT_CRITICAL
} ca_threat_severity_t;

const char *ca_threat_severity_name(ca_threat_severity_t severity);

/* ── what was found ───────────────────────────────────────────────────────── */

typedef struct {
    ca_threat_severity_t severity;
    /* Said to a PERSON. This is the line that appears in a notification, so it
     * names the thing rather than the rule that fired. */
    char *summary;
    char *detail;
    /* Which corpus or check produced it. "Flagged" is not actionable without
     * "by whom" — one source's false positive is another's deliberate policy. */
    char *source;
    /* 0..1. Reported rather than thresholded here, because what counts as
     * enough differs per surface: a banking screen and a photo gallery should
     * not share one cutoff. */
    double confidence;
    int64_t at_unix;
} ca_threat_awareness_result_t;

void ca_threat_awareness_result_free(ca_threat_awareness_result_t *result);

/* ── indicators ───────────────────────────────────────────────────────────── */

typedef struct {
    /* An email address, phone number or handle — HASHED, never the value. The
     * corpus never needs the original, and holding one turns a protective
     * feature into a second copy of the thing being protected. */
    char *identifier_sha256;
    char *breach_name;
    int64_t breach_unix;
    /* What was exposed: "password", "id number", "address". The part people
     * actually need in order to decide what to change. */
    char **exposed_fields;
    size_t exposed_field_count;
} ca_identity_indicator_t;

void ca_identity_indicator_free(ca_identity_indicator_t *indicator);

typedef struct {
    char *value;          /* domain, address or CIDR */
    char *category;       /* "phishing", "c2", "tracker", "malware" */
    ca_threat_severity_t severity;
    char *source;
} ca_network_indicator_t;

void ca_network_indicator_free(ca_network_indicator_t *indicator);

/* ── the corpus ───────────────────────────────────────────────────────────── */

typedef struct ca_local_indicator_corpus {
    void *state;
    const char *(*name)(void *state);

    /* Looks up a HASHED identifier. Fills `out` and returns true on a hit. */
    bool (*find_identity)(void *state, const char *identifier_sha256,
                          ca_identity_indicator_t *out_indicator);

    bool (*find_network)(void *state, const char *value,
                         ca_network_indicator_t *out_indicator);

    size_t (*count)(void *state);
    void (*free_fn)(void *state);
} ca_local_indicator_corpus_t;

void ca_local_indicator_corpus_free(ca_local_indicator_corpus_t *corpus);

/*
 * A corpus with nothing in it.
 *
 * The DEFAULT, deliberately. Shipping a populated corpus would mean shipping
 * somebody else's list and its politics; a host loads one it chose. Empty means
 * every assessment comes back "nothing known", which is honest, rather than
 * "clean", which is not.
 */
ca_local_indicator_corpus_t *ca_empty_indicator_corpus_new(void);

/* Takes ownership of both arrays and of every string in them. */
ca_local_indicator_corpus_t *ca_in_memory_indicator_corpus_new(
    ca_identity_indicator_t *identities, size_t identity_count,
    ca_network_indicator_t *networks, size_t network_count,
    const char *name);

/* ── breach exposure ──────────────────────────────────────────────────────── */

typedef struct ca_breach_exposure_awareness {
    void *state;
    /* `identifier` is the plain value; it is hashed HERE and the plain form
     * never leaves this call. Returns a heap array of `*out_count`. */
    ca_threat_awareness_result_t *(*assess)(void *state, const char *identifier,
                                            size_t *out_count);
    void (*free_fn)(void *state);
} ca_breach_exposure_awareness_t;

void ca_breach_exposure_awareness_free(ca_breach_exposure_awareness_t *awareness);

ca_breach_exposure_awareness_t *ca_breach_exposure_assessor_new(
    ca_local_indicator_corpus_t *corpus);

/* ── file threats ─────────────────────────────────────────────────────────── */

typedef struct ca_file_threat_awareness {
    void *state;
    /* Empty is not a certificate. "No observations" and "clean" are the same
     * answer here, and pretending to certify a file as safe is a promise no
     * local check can keep. */
    ca_threat_awareness_result_t *(*assess)(void *state, const char *path,
                                            size_t *out_count);
    void (*free_fn)(void *state);
} ca_file_threat_awareness_t;

void ca_file_threat_awareness_free(ca_file_threat_awareness_t *awareness);

/*
 * Hashes the file and asks the corpus, and separately notices the shapes that
 * are suspicious regardless of any list: a double extension, an executable
 * arriving as a document, a name using right-to-left override to disguise it.
 *
 * The RLO check matters more than it looks — it is the trick that makes
 * "photo_annexe.exe" render as "photo_exe.ennexa", and no hash list catches a
 * file nobody has seen before.
 */
ca_file_threat_awareness_t *ca_file_threat_awareness_assessor_new(
    ca_local_indicator_corpus_t *corpus);

/* ── network threats ──────────────────────────────────────────────────────── */

typedef struct ca_network_threat_awareness {
    void *state;
    ca_threat_awareness_result_t *(*assess)(void *state, const char *host_or_address,
                                            size_t *out_count);
    void (*free_fn)(void *state);
} ca_network_threat_awareness_t;

void ca_network_threat_awareness_free(ca_network_threat_awareness_t *awareness);

ca_network_threat_awareness_t *ca_network_threat_awareness_assessor_new(
    ca_local_indicator_corpus_t *corpus);

/* Hex SHA-256 of a string, lower case. Exposed because a caller holding an
 * identifier should hash it once and pass the hash around, rather than passing
 * the plain value to three different components. Caller frees. */
char *ca_antibodies_sha256_hex(const char *text);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_SECURITY_ANTIBODIES_H */
