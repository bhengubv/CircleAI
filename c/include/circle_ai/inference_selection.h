#ifndef CIRCLE_AI_INFERENCE_SELECTION_H
#define CIRCLE_AI_INFERENCE_SELECTION_H

/*
 * inference_selection.h - CircleAI.Inference (C11): choosing a model, getting
 * it onto the device, and deciding how hard to run it.
 *
 * Everything here exists because THE DEVICE IS THE CONSTRAINT. On a phone with
 * 2 GB and a metered SIM, the interesting questions are not about the model -
 * they are whether it fits, whether the download is allowed to happen at all,
 * what to do when nothing fits, and how much battery a reply is worth.
 *
 * TWO RULES RUN THROUGH ALL OF IT.
 *
 * A selection ALWAYS says how good it is. "Nothing fits this device" and "this
 * is the right model" must never be the same return value - the caller that
 * cannot tell them apart ships a model that fails to load, and the only symptom
 * is an assistant that does nothing.
 *
 * A DOWNLOAD IS NEVER SILENT ON A METERED LINK. Four hundred megabytes on
 * somebody's data is real money in this market. The gate refuses by default and
 * the refusal is a value, not an exception nobody catches.
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

/* -- choosing a model ----------------------------------------------------- */

typedef enum {
    /* An entry satisfied the capability flags AND the device gates. */
    CA_SELECTION_QUALITY_GOOD = 0,
    /* Fits the device, but below the caller's quality floor. Consider a cloud
     * fallback, or turning the feature off, and say which. */
    CA_SELECTION_QUALITY_BELOW_FLOOR,
    /* NOTHING fits. The returned model is the smallest candidate and may fail
     * to load or be unusably slow. NEVER silently treat this as normal. */
    CA_SELECTION_QUALITY_NOTHING_FITS,
    /* No model is catalogued for this modality, but a built-in NON-model
     * implementation covers it. The capability WORKS - reduced accuracy, zero
     * download, zero RAM. Distinct from GOOD because it should be said out
     * loud, and distinct from NOTHING_FITS because it is not a failure. */
    CA_SELECTION_QUALITY_NON_MODEL_FALLBACK
} ca_selection_quality_t;

const char *ca_selection_quality_name(ca_selection_quality_t quality);

typedef struct {
    char *model_id;
    ca_selection_quality_t quality;
    /* Plain language, for a person. Empty when GOOD. */
    char *note;
    int64_t approx_ram_bytes;
    int64_t approx_download_bytes;
} ca_model_selection_t;

void ca_model_selection_free(ca_model_selection_t *selection);

typedef struct ca_model_selector {
    void *state;
    /* NEVER returns NULL for "nothing fits" - it returns a selection whose
     * quality says so. A NULL would be discarded by a caller that only checks
     * for it, and the reason would be lost. */
    ca_model_selection_t *(*select)(void *state, const char *modality,
                                    ca_selection_quality_t minimum_quality);
    void (*free_fn)(void *state);
} ca_model_selector_t;

void ca_model_selector_free(ca_model_selector_t *selector);

/* Selects against what the device actually has. The RAM figure comes from the
 * platform probe, and whether it was MEASURED or inferred changes what this is
 * entitled to claim - see core_catalogue.h. */
ca_model_selector_t *ca_device_aware_model_selector_new(int64_t ram_available_bytes,
                                                        int64_t storage_free_bytes);

/* Which modalities to run and where. A single answer for the whole turn, so
 * speech-to-text and synthesis cannot independently decide to be the expensive
 * one on a device that can afford exactly one of them. */
typedef struct {
    char *asr_model_id;
    char *tts_model_id;
    bool asr_on_device;
    bool tts_on_device;
    char *reason;
} ca_modality_plan_t;

void ca_modality_plan_free(ca_modality_plan_t *plan);

typedef struct ca_speech_model_selector {
    void *state;
    ca_modality_plan_t *(*plan)(void *state, const char *language);
    void (*free_fn)(void *state);
} ca_speech_model_selector_t;

void ca_speech_model_selector_free(ca_speech_model_selector_t *selector);

ca_speech_model_selector_t *ca_speech_model_selector_new(ca_model_selector_t *inner);

/* -- getting the bytes onto the device ------------------------------------ */

/* Why a download was refused. An error CODE and not a thrown thing: the caller
 * must be able to show somebody the reason and offer the choice. */
typedef enum {
    CA_MODEL_DOWNLOAD_ALLOWED = 0,
    CA_MODEL_DOWNLOAD_BLOCKED_METERED,
    CA_MODEL_DOWNLOAD_BLOCKED_NO_NETWORK,
    CA_MODEL_DOWNLOAD_BLOCKED_STORAGE,
    CA_MODEL_DOWNLOAD_BLOCKED_BATTERY,
    CA_MODEL_DOWNLOAD_BLOCKED_POLICY
} ca_model_download_blocked_t;

const char *ca_model_download_blocked_message(ca_model_download_blocked_t reason);

typedef struct ca_model_download_gate {
    void *state;
    ca_model_download_blocked_t (*check)(void *state, int64_t bytes);
    void (*free_fn)(void *state);
} ca_model_download_gate_t;

void ca_model_download_gate_free(ca_model_download_gate_t *gate);

/*
 * Refuses on a metered link unless the person said otherwise for this download.
 *
 * The consent is PER DOWNLOAD, not a setting. "Allow downloads on mobile data"
 * agreed to once, in a dialog about a 40 MB voice, is not agreement to 800 MB
 * of chat model three weeks later.
 */
ca_model_download_gate_t *ca_metered_network_download_gate_new(
    bool (*is_metered)(void *state), void *state);

void ca_metered_network_download_gate_allow_once(ca_model_download_gate_t *gate);

typedef struct ca_bundle_model_loader ca_bundle_model_loader_t;

/* Loads a model bundle from a directory, verifying it before it is used.
 * Verification is not optional: a truncated 400 MB download fails deep inside a
 * runtime with a shape error, and the fix somebody reaches for is reinstalling
 * the app. */
ca_bundle_model_loader_t *ca_bundle_model_loader_new(const char *bundle_directory);
void ca_bundle_model_loader_free(ca_bundle_model_loader_t *loader);

bool ca_bundle_model_loader_verify(ca_bundle_model_loader_t *loader,
                                   char **out_error);

/* How a sideloaded bundle turned out. Sideloading exists because the download
 * gate's honest answer is sometimes "not on this connection, ever" - somebody
 * hands the phone a file instead. */
typedef enum {
    CA_SIDELOAD_OUTCOME_INSTALLED = 0,
    CA_SIDELOAD_OUTCOME_ALREADY_PRESENT,
    CA_SIDELOAD_OUTCOME_BAD_ARCHIVE,
    CA_SIDELOAD_OUTCOME_HASH_MISMATCH,
    CA_SIDELOAD_OUTCOME_UNSUPPORTED,
    CA_SIDELOAD_OUTCOME_NO_SPACE
} ca_sideload_outcome_t;

const char *ca_sideload_outcome_name(ca_sideload_outcome_t outcome);

typedef struct ca_sideloaded_bundle_importer ca_sideloaded_bundle_importer_t;

ca_sideloaded_bundle_importer_t *ca_sideloaded_bundle_importer_new(
    const char *models_root);

void ca_sideloaded_bundle_importer_free(ca_sideloaded_bundle_importer_t *importer);

/* HASH-CHECKED, always. A model handed over on a memory card is exactly the
 * path an attacker would choose, and "it came from a friend" is not
 * provenance. */
ca_sideload_outcome_t ca_sideloaded_bundle_import(
    ca_sideloaded_bundle_importer_t *importer, const char *archive_path,
    const char *expected_sha256);

/* -- is the network even working ------------------------------------------ */

typedef enum {
    /* No fault - the probe succeeded. */
    CA_NETWORK_FAULT_NONE = 0,
    /* No usable interface at all. Aeroplane mode, no wifi, no SIM. */
    CA_NETWORK_FAULT_NO_LINK,
    /* The link is up but name resolution failed. The single most common
     * real-world failure, and the one that looks most like a broken app. */
    CA_NETWORK_FAULT_DNS_FAILURE,
    /* Connected to a network intercepting traffic pending sign-in - hotel,
     * airport, campus. Requests "succeed" with the wrong body, which is why
     * this must be detected rather than inferred from a parse error. */
    CA_NETWORK_FAULT_CAPTIVE_PORTAL,
    /* Name resolved, host refused or unreachable. */
    CA_NETWORK_FAULT_HOST_UNREACHABLE,
    /* TLS handshake or certificate validation failed. */
    CA_NETWORK_FAULT_TLS_FAILURE,
    /* Timed out - slow link, or a stalled transfer. */
    CA_NETWORK_FAULT_TIMEOUT,
    /* The server answered with an error status. */
    CA_NETWORK_FAULT_SERVER_ERROR
} ca_network_fault_t;

const char *ca_network_fault_name(ca_network_fault_t fault);

/* What somebody should be told, and what they can do about it. The whole
 * purpose of naming faults this precisely: "check your connection" is useless
 * advice to somebody sitting on a captive portal. */
const char *ca_network_fault_advice(ca_network_fault_t fault);

typedef struct {
    ca_network_fault_t fault;
    char *detail;
    int64_t at_unix;
    int64_t round_trip_ms;   /* negative when there was none */
} ca_network_diagnosis_t;

void ca_network_diagnosis_free(ca_network_diagnosis_t *diagnosis);

typedef struct ca_network_preflight {
    void *state;
    /* Runs before a large transfer, not instead of handling its failure. */
    ca_network_diagnosis_t *(*check)(void *state, const char *host);
    void (*free_fn)(void *state);
} ca_network_preflight_t;

void ca_network_preflight_free(ca_network_preflight_t *preflight);
ca_network_preflight_t *ca_network_preflight_new(void);

/* -- how hard to run ------------------------------------------------------ */

/*
 * The energy a reply is allowed to cost.
 *
 * A phone that answers beautifully and is flat by two in the afternoon has not
 * helped anybody. These are the four honest positions, and NONE is first
 * because opting out has to be explicit - a budget that cannot be turned off
 * is a budget that gets worked around.
 */
typedef enum {
    /* Out of automatic control entirely. Max tokens and KV compression are
     * honoured literally. */
    CA_POWER_BUDGET_NONE = 0,
    /* Battery-conscious: ~64 tokens, TQ4 KV, the smaller model in a chain.
     * For short replies below 30% battery or when thermally constrained. */
    CA_POWER_BUDGET_LOW = 1,
    /* Balanced. Honours the caller's max tokens but caps at ~512, TQ4 KV,
     * chain head. Downgrades to LOW automatically below 15%. */
    CA_POWER_BUDGET_BALANCED = 2,
    /* Everything the device has. For a plugged-in device, or a reply somebody
     * is waiting on and has asked for. */
    CA_POWER_BUDGET_FULL = 3
} ca_power_budget_t;

const char *ca_power_budget_name(ca_power_budget_t budget);

typedef struct {
    int max_tokens;
    char *kv_compression;
    char *model_id;
    char *reason;
} ca_power_budget_policy_t;

void ca_power_budget_policy_free(ca_power_budget_policy_t *policy);

/* Resolves a budget against the device right now. `battery_fraction` negative
 * means unknown, and unknown is treated as BALANCED rather than FULL - guessing
 * generously with somebody else's battery is not ours to do. */
ca_power_budget_policy_t *ca_power_budget_resolve(ca_power_budget_t budget,
                                                  double battery_fraction,
                                                  bool is_charging,
                                                  bool thermally_constrained);

/* -- the context window --------------------------------------------------- */

typedef struct ca_context_window_budget_manager ca_context_window_budget_manager_t;

/*
 * Decides what stays in the prompt when it will not all fit.
 *
 * Eviction order matters more than the size: dropping the system prompt to make
 * room for chat history produces an assistant that forgets who it is, and
 * dropping the most recent turn produces one that forgets what was just said.
 * Both look like the model getting worse.
 */
ca_context_window_budget_manager_t *ca_context_window_budget_manager_new(
    int context_tokens, int reserve_for_output);

void ca_context_window_budget_manager_free(ca_context_window_budget_manager_t *manager);

/* How many tokens are available for history, after the system prompt and the
 * output reserve. Negative when the fixed parts alone do not fit - a real
 * answer, and one the caller must handle rather than clamp to zero. */
int ca_context_window_budget_available(const ca_context_window_budget_manager_t *manager,
                                       int system_prompt_tokens);

/* -- prompt templates ----------------------------------------------------- */

typedef struct ca_prompt_template_engine {
    void *state;
    /* Caller frees. */
    char *(*render)(void *state, const char *template_text,
                    const char **keys, const char **values, size_t count);
    void (*free_fn)(void *state);
} ca_prompt_template_engine_t;

void ca_prompt_template_engine_free(ca_prompt_template_engine_t *engine);

/* No conditionals and no loops, deliberately. A template language inside a
 * prompt becomes a place where behaviour hides from everybody reading the
 * code, and the failures show up as a model behaving differently for reasons
 * nobody can grep for. */
ca_prompt_template_engine_t *ca_prompt_template_engine_new(void);

/* -- streaming a model that does not fit ---------------------------------- */

typedef struct {
    int layer_index;
    float *hidden;
    size_t hidden_count;
} ca_layer_activations_t;

void ca_layer_activations_free(ca_layer_activations_t *activations);

typedef struct ca_layer_shard_discovery ca_layer_shard_discovery_t;

/* Finds the weight shards on disk and what each costs to load. */
ca_layer_shard_discovery_t *ca_layer_shard_discovery_new(const char *bundle_directory);
void ca_layer_shard_discovery_free(ca_layer_shard_discovery_t *discovery);

size_t ca_layer_shard_discovery_count(const ca_layer_shard_discovery_t *discovery);
int64_t ca_layer_shard_discovery_bytes_at(const ca_layer_shard_discovery_t *discovery,
                                          size_t index);

typedef struct ca_layer_streaming_orchestrator ca_layer_streaming_orchestrator_t;

/*
 * Runs a model larger than RAM by loading layers as it reaches them.
 *
 * Slow, and the point is that it WORKS AT ALL on hardware that would otherwise
 * be excluded. `resident_layers` is the trade in one number: more resident is
 * faster and closer to the ceiling that made this necessary.
 */
ca_layer_streaming_orchestrator_t *ca_layer_streaming_orchestrator_new(
    ca_layer_shard_discovery_t *discovery, size_t resident_layers);

void ca_layer_streaming_orchestrator_free(ca_layer_streaming_orchestrator_t *orchestrator);

bool ca_layer_streaming_orchestrator_run_layer(
    ca_layer_streaming_orchestrator_t *orchestrator, int layer_index,
    const ca_layer_activations_t *input, ca_layer_activations_t *out_output);

/* -- offloading to a peer ------------------------------------------------- */

typedef struct {
    bool should_offload;
    char *target_peer_id;
    /* Always populated, including when the answer is no. The reason is what
     * makes an offload decision reviewable instead of magic. */
    char *reason;
} ca_offload_verdict_t;

void ca_offload_verdict_free(ca_offload_verdict_t *verdict);

typedef struct ca_mesh_offload_strategy ca_mesh_offload_strategy_t;

/*
 * Whether a nearby device should run this instead.
 *
 * Refuses far more often than it accepts. Offloading sends the prompt to
 * another person's hardware, so the bar is not "would it be faster" - it is a
 * peer already trusted, on a link already up, for work that is worth the round
 * trip. Latency alone is never sufficient.
 */
ca_mesh_offload_strategy_t *ca_mesh_offload_strategy_new(void);
void ca_mesh_offload_strategy_free(ca_mesh_offload_strategy_t *strategy);

ca_offload_verdict_t *ca_mesh_offload_strategy_decide(
    ca_mesh_offload_strategy_t *strategy, const char *model_id,
    int64_t local_ram_bytes, double local_load);

/* -- KV compression ------------------------------------------------------- */

typedef struct {
    bool applied;
    char *mode;
    int64_t bytes_before;
    int64_t bytes_after;
    char *note;
} ca_kv_compression_apply_result_t;

void ca_kv_compression_apply_result_free(ca_kv_compression_apply_result_t *result);

/* -- learning from feedback ----------------------------------------------- */

typedef struct ca_feedback_training_queue ca_feedback_training_queue_t;

/*
 * Corrections, queued on disk for whenever training happens elsewhere.
 *
 * ON DISK rather than in memory because the value of feedback is that it
 * survives: the correction somebody gave before closing the app is the one
 * worth having. Nothing here trains anything - this codebase sources models,
 * and the queue is what it hands over.
 */
ca_feedback_training_queue_t *ca_file_backed_feedback_training_queue_open(
    const char *path);

void ca_feedback_training_queue_close(ca_feedback_training_queue_t *queue);

bool ca_feedback_training_queue_enqueue(ca_feedback_training_queue_t *queue,
                                        const char *prompt, const char *bad_output,
                                        const char *corrected_output);

size_t ca_feedback_training_queue_count(const ca_feedback_training_queue_t *queue);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_INFERENCE_SELECTION_H */
