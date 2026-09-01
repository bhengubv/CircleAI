#ifndef CIRCLE_AI_CORE_SOURCES_H
#define CIRCLE_AI_CORE_SOURCES_H

/*
 * core_sources.h - CircleAI.Core: where models come from, and whether the
 * catalogue that named them can be trusted.
 *
 * core_catalogue.h next door says what a model IS. This says where its bytes
 * live, how the list of them is refreshed, and - the part that matters - how a
 * device decides that list has not been tampered with.
 *
 * THE CATALOGUE IS THE ATTACK SURFACE, NOT THE MODEL. A model file is checked
 * against a hash. The hash comes from the catalogue. So substituting the
 * catalogue substitutes every hash in it, and the download then verifies
 * perfectly against an attacker's number. Signing the catalogue is what makes
 * the per-file hashes mean anything at all.
 *
 * The default verifier REFUSES rather than accepts. A device with no trusted
 * key configured cannot tell a real catalogue from a forged one, and the safe
 * behaviour when you cannot tell is not to proceed.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "circle_ai/core_catalogue.h"

#ifdef __cplusplus
extern "C" {
#endif

/* -- how sure are we this component works --------------------------------- */

/*
 * How far a component has actually been taken.
 *
 * This exists because "done" was being used for four different things, and the
 * gap between them is where every disappointment in this codebase has come
 * from. Compiling is not running; running on a desktop is not running on the
 * phone this is for.
 */
typedef enum {
    /* Written and it compiles. Nothing has been run. */
    CA_VERIFICATION_LEVEL_COMPILED = 0,
    /* Unit tests pass. */
    CA_VERIFICATION_LEVEL_TESTED,
    /* Exercised end to end on a desktop. */
    CA_VERIFICATION_LEVEL_DESKTOP_VERIFIED,
    /* Run on the target hardware, by a person, and observed to work. THE ONLY
     * level that counts as done for anything user-facing. */
    CA_VERIFICATION_LEVEL_DEVICE_VERIFIED,
    /* Measured on the target hardware with numbers written down. */
    CA_VERIFICATION_LEVEL_MEASURED
} ca_verification_level_t;

const char *ca_verification_level_name(ca_verification_level_t level);

/* -- signing the catalogue ------------------------------------------------ */

typedef struct {
    bool verified;
    /* Which key signed it. Reported so a rotation is visible: a catalogue that
     * verifies against an old key still verifies, and somebody should be able
     * to notice that it did. */
    char *key_id;
    /* Always populated. When verification fails this is what a person is shown,
     * and "signature invalid" without a reason is indistinguishable from a
     * network glitch. */
    char *detail;
    int64_t signed_at_unix;
} ca_catalog_signature_result_t;

void ca_catalog_signature_result_free(ca_catalog_signature_result_t *result);

typedef struct ca_catalog_signature_verifier {
    void *state;
    ca_catalog_signature_result_t *(*verify)(void *state, const uint8_t *catalog,
                                             size_t len, const char *signature);
    void (*free_fn)(void *state);
} ca_catalog_signature_verifier_t;

void ca_catalog_signature_verifier_free(ca_catalog_signature_verifier_t *verifier);

/*
 * Verifies NOTHING and reports that plainly.
 *
 * The default, and it is a REFUSAL rather than a pass: `verified` comes back
 * false with a detail saying no trusted key is configured. A null verifier that
 * returned true would make every downstream hash check theatre, and the code
 * doing the checking would look completely correct.
 */
ca_catalog_signature_verifier_t *ca_null_catalog_signature_verifier_new(void);

/* Ed25519 against a pinned public key. `verify_sig` is the host's crypto -
 * this module links no library and holds no key material of its own. */
ca_catalog_signature_verifier_t *ca_catalog_signature_verifier_new(
    const char *key_id, const uint8_t *public_key, size_t key_len,
    bool (*verify_sig)(void *state, const uint8_t *message, size_t message_len,
                       const uint8_t *signature, size_t signature_len),
    void *state);

/* -- the catalogue client ------------------------------------------------- */

/*
 * How often to go looking for a new catalogue.
 *
 * Never by default on a metered link. Refreshing a catalogue is small, but the
 * downloads it makes possible are not, and a device that quietly discovers a
 * newer model on somebody's data has spent their money to do it.
 */
typedef enum {
    /* Only when a person asks. */
    CA_CATALOG_REFRESH_MANUAL = 0,
    CA_CATALOG_REFRESH_DAILY,
    CA_CATALOG_REFRESH_WEEKLY,
    /* On unmetered connections only, whenever one appears. */
    CA_CATALOG_REFRESH_ON_UNMETERED
} ca_catalog_refresh_cadence_t;

const char *ca_catalog_refresh_cadence_name(ca_catalog_refresh_cadence_t cadence);

typedef struct {
    char *endpoint;
    ca_catalog_refresh_cadence_t cadence;
    int timeout_seconds;
    /* Where the last good catalogue is kept, so a refresh that fails leaves the
     * device on the previous one rather than on nothing. */
    char *cache_path;
} ca_model_scope_catalog_options_t;

void ca_model_scope_catalog_options_free(ca_model_scope_catalog_options_t *options);

ca_model_scope_catalog_options_t ca_model_scope_catalog_options_default(void);

typedef struct ca_model_scope_catalog_client ca_model_scope_catalog_client_t;

ca_model_scope_catalog_client_t *ca_model_scope_catalog_client_new(
    const ca_model_scope_catalog_options_t *options,
    ca_catalog_signature_verifier_t *verifier,
    char *(*get)(void *state, const char *url), void *state);

void ca_model_scope_catalog_client_free(ca_model_scope_catalog_client_t *client);

/* Fetches, VERIFIES, then replaces the cache - in that order. Writing first and
 * verifying after is how a device ends up caching a catalogue it has already
 * decided not to trust. */
bool ca_model_scope_catalog_client_refresh(ca_model_scope_catalog_client_t *client,
                                           char **out_error);

/* The catalogue currently in force, from cache when a refresh has not run.
 * Caller frees. */
char *ca_model_scope_catalog_client_current(ca_model_scope_catalog_client_t *client);

/* -- where bytes actually come from --------------------------------------- */

typedef struct ca_model_source {
    void *state;
    ca_model_source_t kind;
    /* Builds the download URL for a file within a repository. Caller frees. */
    char *(*resolve_url)(void *state, const char *repo, const char *file);
    /* Headers this source needs, as alternating name/value. A source that needs
     * a token says so HERE, so that a 401 is attributable to a source rather
     * than appearing as a mysterious failure at download time. */
    const char **(*headers)(void *state, size_t *out_count);
    void (*free_fn)(void *state);
} ca_model_source_seam_t;

void ca_model_source_seam_free(ca_model_source_seam_t *source);

/* ModelScope. The primary, because it is reachable from this market without a
 * token and without a VPN. */
ca_model_source_seam_t *ca_model_scope_source_new(void);

/*
 * Hugging Face.
 *
 * Two kinds behind one host, and conflating them costs an afternoon every time:
 * a public REPO needs no credential, while a BUCKET returns 401 without one. A
 * 401 from a bucket and a 404 from a repo are different problems, and treating
 * them alike sends somebody looking for a file that is sitting right there.
 */
ca_model_source_seam_t *ca_hugging_face_source_new(const char *token);

/* -- voices that ship in the binary --------------------------------------- */

/*
 * The voice configurations compiled in, so a device with no download can still
 * speak.
 *
 * THE PAD RULE, because it has cost more time than anything else here: a blank
 * pad token means the MODEL's blank, not the literal string "_". MMS pads with
 * id 0 and Piper with id 3, and getting it wrong produces audio that is silent
 * or a burst of noise - never an error.
 */
size_t ca_embedded_voice_configs_count(void);
const char *ca_embedded_voice_configs_id_at(size_t index);

/* Borrowed JSON for one embedded voice, or NULL. */
const char *ca_embedded_voice_configs_get(const char *voice_id);

/* The pad token id for a family. Negative when the family is unknown, which is
 * a real answer: guessing 0 here is exactly the bug above. */
int ca_embedded_voice_configs_pad_id(const char *family);

/* -- platform interop ----------------------------------------------------- */

/*
 * The handful of things only the host can answer.
 *
 * Deliberately tiny. Every entry here is a place where behaviour differs per
 * platform, and each one is a place where a bug can hide on one device and not
 * another - so the seam stays small enough to read in one sitting.
 */
typedef struct {
    void *state;
    /* Borrowed. NULL when the host does not say. */
    const char *(*platform_name)(void *state);
    const char *(*platform_version)(void *state);
    /* THE DEVICE's name, not the process's. On Android
     * Environment.MachineName is "localhost" on every single device, which
     * makes every phone look like the same one in any log that uses it. */
    const char *(*device_name)(void *state);
    const char *(*cache_directory)(void *state);
    const char *(*data_directory)(void *state);
    bool (*is_metered_network)(void *state);
    void (*free_fn)(void *state);
} ca_platform_interop_t;

void ca_platform_interop_free(ca_platform_interop_t *interop);

/* Installs the host's implementation. NULL restores the built-in, which answers
 * what libc can and NULL for the rest - never a plausible-looking guess. */
void ca_platform_interop_set(ca_platform_interop_t *interop);
ca_platform_interop_t *ca_platform_interop_current(void);

/* -- the quantisation codec ----------------------------------------------- */

/*
 * The TurboQuant codec's format, as one place rather than as encode/decode
 * agreeing by accident.
 *
 * The version is written into every payload. A codec with no version is a
 * cache that cannot be read after the codec improves, and here that means
 * re-downloading every model on the device.
 */
int ca_turbo_quant_codec_version(void);

/* Bytes a payload of this shape will occupy, so a caller can decide whether it
 * fits before spending the time to produce it. */
size_t ca_turbo_quant_codec_encoded_size(size_t value_count, int bits_per_value);

/* Whether these parameters are ones this build can encode AND decode. Asked up
 * front because a payload written with an unsupported width is discovered on
 * the way back in, by which time the source data is gone. */
bool ca_turbo_quant_codec_supports(int bits_per_value);

/* -- tenancy -------------------------------------------------------------- */

typedef struct ca_tenant_context {
    void *state;
    /* Borrowed. NULL means single-tenant, which is the normal case on a device
     * and must be distinguishable from "the tenant is not resolved yet" - the
     * second is a bug that would otherwise read data across a boundary. */
    const char *(*tenant_id)(void *state);
    bool (*is_resolved)(void *state);
    void (*free_fn)(void *state);
} ca_tenant_context_t;

void ca_tenant_context_free(ca_tenant_context_t *context);

ca_tenant_context_t *ca_tenant_context_single(void);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CORE_SOURCES_H */
