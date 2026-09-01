#ifndef CIRCLE_AI_TOOLING_H
#define CIRCLE_AI_TOOLING_H

/*
 * tooling.h - the remaining seams (C11).
 *
 * CircleAI.Tools, Tools.Catalog, Skills, Plugins, Runtime, SDD, DevTools,
 * Inputs, Search, Languages, Spatial, MediaHub, DocAnalytics,
 * Companion.Proactive, Hosting.InferenceBridge, Presentations, Simulation,
 * Realtime, Observer, Personality, SelfBench, the three telephony carriers,
 * Vision.Cloud and the Integration connectors.
 *
 * Two threads run through all of it.
 *
 * A TOOL IS A CAPABILITY SOMEBODY GRANTED. Everything that can reach outside
 * the process - a credential, an OAuth flow, a scraper, a plugin - is quota'd,
 * scoped and refusable, and the default implementation of each does nothing.
 * The failure this prevents is a model acquiring a capability because a host
 * forgot a line of configuration.
 *
 * SEARCH AND VECTOR MATH ARE THE HOT PATH. They are the only things here that
 * run on every turn, and they are the reason recall can be afforded at all.
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

/* -- search: the hot path ------------------------------------------------- */

/*
 * Splitting text into terms.
 *
 * Unicode-aware, lower-cased, and it does NOT split on every non-letter: an
 * identifier, a version number and a hyphenated surname are each one term, and
 * a tokeniser that shreds them makes them unfindable by the thing somebody
 * actually typed.
 *
 * Caller frees the array and each string.
 */
char **ca_search_tokenisation_split(const char *text, size_t *out_count);

/* Whether a term is worth indexing. Stop words are dropped on the QUERY side
 * only, never at index time - dropping them from the index makes an exact
 * phrase unsearchable, and the phrase is often the whole point. */
bool ca_search_tokenisation_is_stop_word(const char *term);

/*
 * BM25, not plain TF-IDF.
 *
 * The saturation term is what stops a document that repeats one word forty
 * times outranking one that uses it twice in a sentence that means something.
 * k1 = 1.2, b = 0.75 - the standard values, and standard here is right: tuning
 * them per corpus is how two ports stop agreeing on what comes back first.
 */
double ca_search_scoring_bm25(int term_frequency, int document_frequency,
                              int document_count, int document_length,
                              double average_document_length);

double ca_search_scoring_k1(void);
double ca_search_scoring_b(void);

/* Dot product, cosine, and L2 normalisation. Separate from the SIMD versions so
 * there is always a correct reference to check the fast one against - a vector
 * kernel that is quietly wrong produces plausible rankings and no error. */
double ca_vector_math_dot(const float *a, const float *b, size_t dims);
double ca_vector_math_cosine(const float *a, const float *b, size_t dims);
void ca_vector_math_normalise(float *vector, size_t dims);

/* The widened versions, where the platform has them. Same results as
 * ca_vector_math_* within floating-point tolerance, and the tolerance is real:
 * a different summation order gives a different last bit, so a test that
 * demands exact equality between these two fails on some devices and not
 * others. */
double ca_simd_ops_dot(const float *a, const float *b, size_t dims);
void ca_simd_ops_scale(float *vector, size_t dims, float factor);
bool ca_simd_ops_available(void);

/* -- tools ---------------------------------------------------------------- */

typedef struct ca_composio_tool_bridge ca_composio_tool_bridge_t;

/* Imports a third-party tool catalogue. Imported tools arrive DISABLED: a
 * catalogue that could enable itself is a catalogue that decides what this
 * device can do. */
ca_composio_tool_bridge_t *ca_composio_tool_bridge_new(const char *api_key,
                                                       void *http);

void ca_composio_tool_bridge_free(ca_composio_tool_bridge_t *bridge);

/* Battery, storage, thermal, network - the questions an assistant is asked
 * about the device it is running on. Read-only, every one of them. Nothing here
 * changes a setting, toggles a radio, or restarts anything. */
char *ca_device_diagnostics_tools_snapshot_json(void);

/* Face tooling exposed to the agent loop: detect and compare, never enrol and
 * never store. The seam is deliberately narrower than what Vision can do. */
char *ca_facex_tools_describe(void);

typedef struct ca_tool_definition_builder ca_tool_definition_builder_t;

/*
 * Builds a tool definition a model can be shown.
 *
 * Validates the JSON schema as it goes rather than at call time. A malformed
 * schema does not fail when it is built — it fails when a model tries to use
 * the tool, mid-conversation, as a tool call that cannot be parsed.
 */
ca_tool_definition_builder_t *ca_tool_definition_builder_new(const char *name);
void ca_tool_definition_builder_free(ca_tool_definition_builder_t *builder);

bool ca_tool_definition_builder_describe(ca_tool_definition_builder_t *builder,
                                         const char *description);

/* `json_type` is one of the JSON schema primitives. Returns false for anything
 * else, because a type a model does not understand produces arguments nothing
 * can validate. */
bool ca_tool_definition_builder_parameter(ca_tool_definition_builder_t *builder,
                                          const char *name, const char *json_type,
                                          const char *description, bool required);

/* Caller frees. NULL when the definition is incomplete, with *out_error set. */
char *ca_tool_definition_builder_build(ca_tool_definition_builder_t *builder,
                                       char **out_error);

typedef struct ca_tool_manifest_generator ca_tool_manifest_generator_t;

/* Emits the manifest for a set of tools. Generated rather than hand-written so
 * that the list a model is shown and the list the registry will actually
 * dispatch cannot drift apart. */
ca_tool_manifest_generator_t *ca_tool_manifest_generator_new(void);
void ca_tool_manifest_generator_free(ca_tool_manifest_generator_t *generator);

bool ca_tool_manifest_generator_add(ca_tool_manifest_generator_t *generator,
                                    const char *definition_json);

char *ca_tool_manifest_generator_emit(ca_tool_manifest_generator_t *generator);

/* -- credentials and quotas ----------------------------------------------- */

typedef struct {
    char *provider_id;
    char *authorize_url;
    char *token_url;
    char **scopes;
    size_t scope_count;
    char *client_id;
    /* PKCE is not optional. A public client doing OAuth without it can have its
     * authorization code stolen by anything that can register the redirect. */
    bool use_pkce;
} ca_o_auth2_descriptor_t;

void ca_o_auth2_descriptor_free(ca_o_auth2_descriptor_t *descriptor);

typedef struct ca_o_auth2_flow_driver {
    void *state;
    /* Builds the URL a PERSON opens. This module never posts credentials and
     * never handles a password - the person authenticates with the provider,
     * and what comes back is a code. */
    char *(*authorize_url)(void *state, const ca_o_auth2_descriptor_t *descriptor,
                           const char *redirect_uri, char **out_verifier);
    bool (*exchange_code)(void *state, const ca_o_auth2_descriptor_t *descriptor,
                          const char *code, const char *verifier,
                          char **out_access_token, char **out_refresh_token,
                          int64_t *out_expires_unix);
    void (*free_fn)(void *state);
} ca_o_auth2_flow_driver_t;

void ca_o_auth2_flow_driver_free(ca_o_auth2_flow_driver_t *driver);

ca_o_auth2_flow_driver_t *ca_o_auth2_flow_driver_new(void *http);

/* Drives nothing. The default. */
ca_o_auth2_flow_driver_t *ca_null_o_auth2_flow_driver_new(void);

typedef struct ca_credential_store ca_credential_store_t;

/*
 * AES-GCM at rest, with the key from the platform keystore.
 *
 * GCM rather than CBC because it AUTHENTICATES: a tampered ciphertext fails to
 * open instead of decrypting to something. A credential store that returns
 * garbage on tampering hands that garbage to a provider as a token.
 *
 * The nonce is never reused. A repeated nonce under one key in GCM does not
 * degrade the encryption - it breaks it, and both messages become recoverable.
 */
ca_credential_store_t *ca_aes_gcm_credential_store_new(
    const uint8_t *key, size_t key_len);

void ca_credential_store_free(ca_credential_store_t *store);

bool ca_credential_store_put(ca_credential_store_t *store, const char *name,
                             const char *secret);

/* Caller frees, and should zero it when done. */
char *ca_credential_store_get(ca_credential_store_t *store, const char *name);

bool ca_credential_store_remove(ca_credential_store_t *store, const char *name);

typedef struct ca_quota_guard ca_quota_guard_t;

/* A sliding window, not a fixed one. A fixed window lets twice the quota
 * through across a boundary - all of it in the last second of one window and
 * the first of the next - which is exactly when a rate limit matters. */
ca_quota_guard_t *ca_sliding_window_quota_guard_new(int limit,
                                                    int64_t window_seconds);

void ca_quota_guard_free(ca_quota_guard_t *guard);

bool ca_quota_guard_try_acquire(ca_quota_guard_t *guard, const char *key,
                                int64_t now_unix);

int ca_quota_guard_remaining(const ca_quota_guard_t *guard, const char *key,
                             int64_t now_unix);

/* -- skills --------------------------------------------------------------- */

typedef struct ca_skill_store ca_skill_store_t;

/* Skills read from a capability manifest rather than by scanning a directory.
 * A manifest is a list somebody wrote; a scan is whatever happens to be on
 * disk, which is how a skill dropped into a folder becomes active without
 * anybody adding it. */
ca_skill_store_t *ca_capability_manifest_skill_store_open(const char *manifest_path);
void ca_skill_store_free(ca_skill_store_t *store);

size_t ca_skill_store_count(const ca_skill_store_t *store);
const char *ca_skill_store_id_at(const ca_skill_store_t *store, size_t index);

typedef struct ca_skill_context_builder ca_skill_context_builder_t;

/* Assembles the skill text that goes into a prompt, within a character budget.
 * Budgeted because skills are the easiest thing in a prompt to let grow, and
 * every character spent here is one the conversation does not get. */
ca_skill_context_builder_t *ca_skill_context_builder_new(ca_skill_store_t *store,
                                                         size_t max_characters);

void ca_skill_context_builder_free(ca_skill_context_builder_t *builder);

char *ca_skill_context_builder_build(ca_skill_context_builder_t *builder,
                                     const char *situation);

typedef struct ca_skill_pack_auto_importer ca_skill_pack_auto_importer_t;

/* Notices a skill pack and STAGES it. Never activates one: "auto" here means
 * the person does not have to find the file, not that nobody has to approve
 * it. */
ca_skill_pack_auto_importer_t *ca_skill_pack_auto_importer_new(const char *watch_dir,
                                                               const char *staging_dir);

void ca_skill_pack_auto_importer_free(ca_skill_pack_auto_importer_t *importer);

size_t ca_skill_pack_auto_importer_staged_count(
    const ca_skill_pack_auto_importer_t *importer);

/* -- plugins -------------------------------------------------------------- */

typedef struct {
    char *plugin_id;
    char *name;
    char *version;
    char *entry_point;
    bool enabled;
    int64_t installed_unix;
} ca_registered_plugin_t;

void ca_registered_plugin_free(ca_registered_plugin_t *plugin);

typedef struct {
    char *plugin_id;
    char *name;
    char *publisher;
    char *description;
    /* Whether the publisher's signature verified. NOT whether the plugin is
     * safe - the two get conflated, and a signed plugin is only evidence about
     * who wrote it. */
    bool signature_verified;
    char *homepage;
} ca_marketplace_entry_t;

void ca_marketplace_entry_free(ca_marketplace_entry_t *entry);

typedef struct ca_plugins_root_resolver {
    void *state;
    /* Where plugins live. A seam because the answer differs per platform and
     * getting it wrong means a plugin directory inside a cache the system is
     * free to evict. */
    const char *(*root)(void *state);
    void (*free_fn)(void *state);
} ca_plugins_root_resolver_t;

void ca_plugins_root_resolver_free(ca_plugins_root_resolver_t *resolver);

typedef struct ca_plugin_lifecycle_service ca_plugin_lifecycle_service_t;

/* Install, enable, disable, remove - and DISABLE IS NOT REMOVE. A disabled
 * plugin keeps its data, so re-enabling it does not silently start it from
 * nothing. */
ca_plugin_lifecycle_service_t *ca_plugin_lifecycle_service_new(
    ca_plugins_root_resolver_t *resolver);

void ca_plugin_lifecycle_service_free(ca_plugin_lifecycle_service_t *service);

bool ca_plugin_lifecycle_service_enable(ca_plugin_lifecycle_service_t *service,
                                        const char *plugin_id);

bool ca_plugin_lifecycle_service_disable(ca_plugin_lifecycle_service_t *service,
                                         const char *plugin_id);

/* -- runtime -------------------------------------------------------------- */

typedef enum {
    CA_ARCHITECTURE_UNKNOWN = 0,
    CA_ARCHITECTURE_X64,
    CA_ARCHITECTURE_ARM64,
    CA_ARCHITECTURE_ARM32,
    CA_ARCHITECTURE_RISCV64
} ca_architecture_kind_t;

const char *ca_architecture_kind_name(ca_architecture_kind_t kind);

typedef enum {
    CA_OPERATING_SYSTEM_UNKNOWN = 0,
    CA_OPERATING_SYSTEM_LINUX,
    CA_OPERATING_SYSTEM_ANDROID,
    CA_OPERATING_SYSTEM_WINDOWS,
    CA_OPERATING_SYSTEM_MACOS,
    CA_OPERATING_SYSTEM_IOS,
    CA_OPERATING_SYSTEM_HARMONY
} ca_operating_system_kind_t;

const char *ca_operating_system_kind_name(ca_operating_system_kind_t kind);

/* Android is its OWN entry and not a Linux variant. Almost every decision that
 * branches on the OS - paths, permissions, whether a radio can be read - is
 * different on Android, and collapsing it into Linux is how a desktop
 * assumption reaches a phone. */

typedef struct {
    char *runtime_id;
    char *version;
    ca_architecture_kind_t architecture;
    ca_operating_system_kind_t operating_system;
    char *sha256;
    int64_t bytes;
    char *url;
} ca_native_runtime_bundle_t;

void ca_native_runtime_bundle_free(ca_native_runtime_bundle_t *bundle);

typedef struct {
    char *runtime_id;
    char *install_path;
    char *version;
    int64_t installed_unix;
    bool verified;
} ca_native_runtime_install_t;

void ca_native_runtime_install_free(ca_native_runtime_install_t *install);

typedef struct ca_native_runtime_fetcher {
    void *state;
    /* Fetches and VERIFIES against the bundle's hash before installing. A
     * native runtime is executable code; installing one that failed its hash is
     * running whatever arrived. */
    ca_native_runtime_install_t *(*fetch)(void *state,
                                          const ca_native_runtime_bundle_t *bundle,
                                          char **out_error);
    void (*free_fn)(void *state);
} ca_native_runtime_fetcher_t;

void ca_native_runtime_fetcher_free(ca_native_runtime_fetcher_t *fetcher);

/* -- spec-driven development ---------------------------------------------- */

typedef struct ca_specification_validator {
    void *state;
    bool (*validate)(void *state, const char *specification, char **out_error);
    void (*free_fn)(void *state);
} ca_specification_validator_t;

void ca_specification_validator_free(ca_specification_validator_t *validator);

/* Checks the SHAPE of a specification, not its meaning. Being clear about which
 * matters: a spec that validates is well-formed, and nothing here claims it
 * describes something worth building. */
ca_specification_validator_t *ca_json_shape_specification_validator_new(void);
ca_specification_validator_t *ca_null_specification_validator_new(void);

typedef struct ca_spec_to_scaffold {
    void *state;
    /* Generates a project skeleton. Caller frees. */
    char *(*scaffold)(void *state, const char *specification, char **out_error);
    void (*free_fn)(void *state);
} ca_spec_to_scaffold_t;

void ca_spec_to_scaffold_free(ca_spec_to_scaffold_t *scaffold);

ca_spec_to_scaffold_t *ca_hello_world_spec_to_scaffold_new(void);
ca_spec_to_scaffold_t *ca_null_spec_to_scaffold_new(void);

/* -- dev tools ------------------------------------------------------------ */

typedef struct {
    char *file_path;
    char *pattern;
    char *replacement;
    bool regex;
    bool whole_word;
} ca_refactor_request_t;

void ca_refactor_request_free(ca_refactor_request_t *request);

typedef struct ca_refactor_tool ca_refactor_tool_t;

/* Returns a PATCH rather than editing in place. Every refactor is reviewable
 * before it touches anything, which is the difference between a tool and an
 * accident. */
ca_refactor_tool_t *ca_regex_refactor_tool_new(void);
void ca_refactor_tool_free(ca_refactor_tool_t *tool);

char *ca_refactor_tool_plan(ca_refactor_tool_t *tool,
                            const ca_refactor_request_t *request);

typedef struct ca_patch_planner ca_patch_planner_t;

ca_patch_planner_t *ca_pattern_match_patch_planner_new(void);
void ca_patch_planner_free(ca_patch_planner_t *planner);

typedef struct ca_inline_suggester ca_inline_suggester_t;

/* Suggests from what is already in the file, within a token budget. No model,
 * so it is fast enough to run on a keystroke - which is the only latency budget
 * an inline suggestion actually has. */
ca_inline_suggester_t *ca_token_context_inline_suggester_new(size_t max_tokens);
void ca_inline_suggester_free(ca_inline_suggester_t *suggester);

/* -- inputs --------------------------------------------------------------- */

typedef struct ca_stealth_http_client {
    void *state;
    char *(*get)(void *state, const char *url, char **out_error);
    void (*free_fn)(void *state);
} ca_stealth_http_client_t;

void ca_stealth_http_client_free(ca_stealth_http_client_t *client);

/*
 * Fetches a page with an ordinary browser's headers.
 *
 * "Stealth" means NOT LOOKING BROKEN, not evading a block. It sets a normal
 * user agent and accepts normal encodings, because a default client's headers
 * get a different page - or none. It honours robots directives and rate limits,
 * and it does not solve challenges, rotate identities, or work around anything
 * a site put there deliberately.
 */
ca_stealth_http_client_t *ca_stealth_http_client_new(void);

/* Fetches nothing. The default. */
ca_stealth_http_client_t *ca_null_stealth_http_client_new(void);

typedef struct {
    char *job_id;
    char *url;
    char *selector;
    int64_t requested_unix;
    int max_pages;
    /* Seconds between requests. A floor, not a suggestion. */
    int min_interval_seconds;
} ca_mcp_scrape_job_t;

void ca_mcp_scrape_job_free(ca_mcp_scrape_job_t *job);

typedef struct ca_html_scraper ca_html_scraper_t;

ca_html_scraper_t *ca_http_html_scraper_new(ca_stealth_http_client_t *client);
void ca_html_scraper_free(ca_html_scraper_t *scraper);

char *ca_html_scraper_extract(ca_html_scraper_t *scraper,
                              const ca_mcp_scrape_job_t *job, char **out_error);

typedef struct ca_terminal_cast ca_terminal_cast_t;

/* An asciinema-format terminal recording. Plain text and replayable anywhere -
 * a terminal session captured as video is unsearchable and thirty times the
 * size. */
ca_terminal_cast_t *ca_asciinema_terminal_cast_new(int width, int height);
void ca_terminal_cast_free(ca_terminal_cast_t *cast);

bool ca_terminal_cast_append(ca_terminal_cast_t *cast, double at_seconds,
                             const char *data);

char *ca_terminal_cast_to_json(const ca_terminal_cast_t *cast);

/* -- languages ------------------------------------------------------------ */

typedef struct ca_language_detector {
    void *state;
    /* Borrowed ISO code, or NULL when it cannot tell. NULL matters: guessing a
     * language picks the wrong voice and the wrong phonemiser, and the result
     * is speech nobody can understand rather than an error anybody can see. */
    const char *(*detect)(void *state, const char *text, double *out_confidence);
    void (*free_fn)(void *state);
} ca_language_detector_t;

void ca_language_detector_free(ca_language_detector_t *detector);

typedef struct {
    char *normalised;
    char *script;
    /* Whether anything actually changed. A caller that re-runs a pipeline on
     * unchanged text is doing work for nothing, and on a phone that is battery. */
    bool changed;
} ca_script_normalisation_result_t;

void ca_script_normalisation_result_free(ca_script_normalisation_result_t *result);

typedef struct ca_script_normaliser {
    void *state;
    bool (*normalise)(void *state, const char *text,
                      ca_script_normalisation_result_t *out_result);
    void (*free_fn)(void *state);
} ca_script_normaliser_t;

void ca_script_normaliser_free(ca_script_normaliser_t *normaliser);

/* isiZulu. The pack carries orthography, the click letters, and the noun-class
 * prefixes that make naive stemming wrong. */
const char *ca_isi_zulu_language_pack_id(void);
size_t ca_isi_zulu_language_pack_prefix_count(void);
const char *ca_isi_zulu_language_pack_prefix_at(size_t index);

typedef struct ca_live_translator {
    void *state;
    /* Translates a partial utterance and may REVISE it as more arrives - the
     * one thing live translation must do that batch translation must not. */
    char *(*translate)(void *state, const char *text, const char *from_iso,
                       const char *to_iso, bool is_final);
    void (*free_fn)(void *state);
} ca_live_translator_t;

void ca_live_translator_free(ca_live_translator_t *translator);

ca_live_translator_t *ca_llm_translation_engine_new(void *generator);

/* -- spatial -------------------------------------------------------------- */

typedef struct {
    char *scene_id;
    char *nodes_json;
    int64_t at_unix;
} ca_scene3_d_t;

void ca_scene3_d_free(ca_scene3_d_t *scene);

typedef struct ca_scene3_d_renderer {
    void *state;
    char *(*render)(void *state, const ca_scene3_d_t *scene);
    void (*free_fn)(void *state);
} ca_scene3_d_renderer_t;

void ca_scene3_d_renderer_free(ca_scene3_d_renderer_t *renderer);

ca_scene3_d_renderer_t *ca_json_scene3_d_renderer_new(void);
ca_scene3_d_renderer_t *ca_null_scene3_d_renderer_new(void);

/* Synthetic, and named so. Both produce plausible readings from a model rather
 * than from hardware - useful for building a display, and never to be mistaken
 * for a sensor. */
char *ca_synthetic_radar_readout(int64_t at_unix);
char *ca_synthetic_sky_tracker(int64_t at_unix, double latitude, double longitude);

/* -- media hub ------------------------------------------------------------ */

typedef struct {
    char *media_id;
    double position_seconds;
    bool playing;
    int64_t at_unix_ms;
} ca_playback_position_t;

typedef struct ca_synced_playback {
    void *state;
    bool (*report)(void *state, const char *device_id,
                   const ca_playback_position_t *position);
    /* Where everybody should be now. Accounts for the time since each device
     * reported - a position echoed back unadjusted is already stale by the
     * round trip, and the drift compounds every sync. */
    bool (*consensus)(void *state, int64_t now_unix_ms,
                      ca_playback_position_t *out_position);
    void (*free_fn)(void *state);
} ca_synced_playback_t;

void ca_synced_playback_free(ca_synced_playback_t *playback);

ca_synced_playback_t *ca_synced_playback_new(void);
ca_synced_playback_t *ca_null_synced_playback_new(void);

/* -- document analytics --------------------------------------------------- */

typedef struct {
    char *document_id;
    char *viewer_id;
    int64_t opened_unix;
    int64_t dwell_ms;
    int pages_viewed;
} ca_document_view_t;

void ca_document_view_free(ca_document_view_t *view);

typedef struct {
    char *document_id;
    char *summary;
    /* Aggregate only. Per-viewer behaviour is available to the document's OWNER
     * and is never surfaced to anybody else - "who read my document and for how
     * long" is a reasonable question from an author and surveillance from
     * everybody else. */
    int view_count;
    int64_t median_dwell_ms;
} ca_document_insight_t;

void ca_document_insight_free(ca_document_insight_t *insight);

typedef struct ca_document_insights {
    void *state;
    bool (*record)(void *state, const ca_document_view_t *view);
    ca_document_insight_t *(*summarise)(void *state, const char *document_id);
    void (*free_fn)(void *state);
} ca_document_insights_t;

void ca_document_insights_free(ca_document_insights_t *insights);
ca_document_insights_t *ca_null_document_insights_new(void);

/* -- proactive tasks ------------------------------------------------------ */

typedef enum {
    CA_PROACTIVE_TASK_LOAD_OK = 0,
    CA_PROACTIVE_TASK_LOAD_NOT_FOUND,
    CA_PROACTIVE_TASK_LOAD_MALFORMED,
    CA_PROACTIVE_TASK_LOAD_UNSUPPORTED_VERSION,
    CA_PROACTIVE_TASK_LOAD_REFUSED
} ca_proactive_task_load_error_t;

const char *ca_proactive_task_load_error_message(ca_proactive_task_load_error_t error);

typedef struct {
    bool ran;
    char *output;
    /* Whether it produced something worth interrupting a person for. Separate
     * from `ran` because most proactive runs correctly produce nothing, and a
     * runner that speaks every time it runs is one people turn off. */
    bool worth_surfacing;
    char *reason;
    int64_t duration_ms;
} ca_proactive_task_run_result_t;

void ca_proactive_task_run_result_free(ca_proactive_task_run_result_t *result);

typedef struct ca_proactive_task_runner {
    void *state;
    ca_proactive_task_run_result_t *(*run)(void *state, const char *task_id);
    void (*free_fn)(void *state);
} ca_proactive_task_runner_t;

void ca_proactive_task_runner_free(ca_proactive_task_runner_t *runner);

/* Runs nothing and surfaces nothing. THE DEFAULT, because proactive behaviour
 * is the one capability that should never appear because nobody configured it
 * off. */
ca_proactive_task_runner_t *ca_null_proactive_task_runner_new(void);

ca_proactive_task_runner_t *ca_delegate_proactive_task_runner_new(
    ca_proactive_task_run_result_t *(*fn)(void *fn_state, const char *task_id),
    void *fn_state);

/* -- the inference bridge ------------------------------------------------- */

typedef enum {
    CA_INFERENCE_FRAGMENT_TOKEN = 0,
    CA_INFERENCE_FRAGMENT_TOOL_CALL,
    CA_INFERENCE_FRAGMENT_REASONING,
    CA_INFERENCE_FRAGMENT_DONE,
    CA_INFERENCE_FRAGMENT_ERROR
} ca_inference_fragment_kind_t;

const char *ca_inference_fragment_kind_name(ca_inference_fragment_kind_t kind);

typedef struct {
    ca_inference_fragment_kind_t kind;
    char *text;
    int64_t at_unix_ms;
} ca_inference_fragment_t;

void ca_inference_fragment_free(ca_inference_fragment_t *fragment);

/* REASONING is its own kind so a caller can choose not to show it. A model's
 * intermediate thinking rendered as the answer is confusing; discarded silently
 * it is unavailable for debugging. Naming it lets both be a decision. */

typedef struct ca_inference_bridge {
    void *state;
    bool (*stream)(void *state, const char *prompt,
                   bool (*on_fragment)(void *fragment_state,
                                       const ca_inference_fragment_t *fragment),
                   void *fragment_state, char **out_error);
    void (*free_fn)(void *state);
} ca_inference_bridge_t;

void ca_inference_bridge_free(ca_inference_bridge_t *bridge);

/* Runs a model in a separate process, so a native crash takes the process and
 * not the app. On a phone, a segfault inside the runtime would otherwise close
 * an assistant somebody is mid-sentence with. */
ca_inference_bridge_t *ca_local_process_inference_bridge_new(const char *executable);

/* Deterministic scripted fragments. What the loop is tested against. */
ca_inference_bridge_t *ca_mock_inference_bridge_new(const char **fragments,
                                                    size_t count);

/* -- realtime events ------------------------------------------------------ */

typedef struct {
    char *session_id;
    char *delta;
    bool is_final;
    int64_t at_unix_ms;
} ca_transcript_delta_event_t;

void ca_transcript_delta_event_free(ca_transcript_delta_event_t *event);

typedef struct {
    char *session_id;
    char *full_text;
    int64_t at_unix_ms;
    int64_t duration_ms;
} ca_turn_complete_event_t;

void ca_turn_complete_event_free(ca_turn_complete_event_t *event);

typedef struct {
    char *session_id;
    char *code;
    char *message;
    /* Whether the session survives. A recoverable error and a dead session
     * demand opposite reactions, and a caller that cannot tell reconnects on
     * every hiccup or on none. */
    bool fatal;
    int64_t at_unix_ms;
} ca_session_error_event_t;

void ca_session_error_event_free(ca_session_error_event_t *event);

/* -- presentations -------------------------------------------------------- */

typedef struct {
    char *deck_id;
    char *title;
    char **slide_titles;
    char **slide_bodies;
    size_t slide_count;
    char *theme;
} ca_deck_t;

void ca_deck_free(ca_deck_t *deck);

typedef struct ca_deck_engine {
    void *state;
    uint8_t *(*render)(void *state, const ca_deck_t *deck, size_t *out_len);
    const char *(*mime_type)(void *state);
    void (*free_fn)(void *state);
} ca_deck_engine_t;

void ca_deck_engine_free(ca_deck_engine_t *engine);

/* A worked example, clearly marked as a sample. */
ca_deck_t *ca_sample_deck_new(void);

/* -- simulation ----------------------------------------------------------- */

typedef struct ca_network_health_simulator ca_network_health_simulator_t;

/*
 * Loss, latency and partitions, on demand.
 *
 * The only way to test a mesh honestly. Every transport bug in this project
 * lives in the states a real network reaches rarely and a simulated one reaches
 * on request: a link that is up but carrying nothing, a peer that answers
 * slowly, a partition that heals.
 */
ca_network_health_simulator_t *ca_network_health_simulator_new(uint64_t seed);
void ca_network_health_simulator_free(ca_network_health_simulator_t *simulator);

void ca_network_health_simulator_set_loss(ca_network_health_simulator_t *simulator,
                                          double loss_fraction);

void ca_network_health_simulator_set_latency(ca_network_health_simulator_t *simulator,
                                             int64_t latency_ms, int64_t jitter_ms);

bool ca_network_health_simulator_should_drop(ca_network_health_simulator_t *simulator);

typedef struct ca_threat_propagation_scenario ca_threat_propagation_scenario_t;

/* How something spreads across a mesh, for testing the awareness layer. A
 * MODEL, not a tool: it produces a scenario to reason about and touches no real
 * device. */
ca_threat_propagation_scenario_t *ca_threat_propagation_scenario_new(size_t node_count,
                                                                     uint64_t seed);

void ca_threat_propagation_scenario_free(ca_threat_propagation_scenario_t *scenario);

size_t ca_threat_propagation_scenario_step(ca_threat_propagation_scenario_t *scenario);

typedef struct ca_miro_fish_adapter ca_miro_fish_adapter_t;

ca_miro_fish_adapter_t *ca_miro_fish_adapter_new(void);
void ca_miro_fish_adapter_free(ca_miro_fish_adapter_t *adapter);

/* -- observer, personality, bench ----------------------------------------- */

typedef struct {
    char *decision_id;
    char *action;
    char *rationale;
    double confidence;
    int64_t at_unix;
    /* Whether it was acted on. An observer that records only its own decisions
     * cannot tell whether they were any good. */
    bool acted_on;
} ca_observer_decision_t;

void ca_observer_decision_free(ca_observer_decision_t *decision);

typedef struct ca_sensor_recorder ca_sensor_recorder_t;

/* Bounded and in memory. A sensor recorder that grew without limit would fill
 * a phone with readings nobody asked for. */
ca_sensor_recorder_t *ca_sensor_recorder_new(size_t capacity);
void ca_sensor_recorder_free(ca_sensor_recorder_t *recorder);

bool ca_sensor_recorder_record(ca_sensor_recorder_t *recorder, const char *sensor,
                               double value, int64_t at_unix);

size_t ca_sensor_recorder_count(const ca_sensor_recorder_t *recorder);

typedef struct ca_persona_conflict_resolver {
    void *state;
    /* When two persona traits disagree, which wins and why. Returning the
     * reason matters: an assistant whose personality shifts with no
     * explanation reads as unreliable rather than adaptive. */
    char *(*resolve)(void *state, const char *trait_a, const char *trait_b);
    void (*free_fn)(void *state);
} ca_persona_conflict_resolver_t;

void ca_persona_conflict_resolver_free(ca_persona_conflict_resolver_t *resolver);

typedef struct ca_persona_prompt_builder ca_persona_prompt_builder_t;

ca_persona_prompt_builder_t *ca_persona_prompt_builder_new(
    ca_persona_conflict_resolver_t *resolver);

void ca_persona_prompt_builder_free(ca_persona_prompt_builder_t *builder);

char *ca_persona_prompt_builder_build(ca_persona_prompt_builder_t *builder,
                                      const char *persona_json);

typedef struct ca_scorer {
    void *state;
    const char *(*scorer_id)(void *state);
    /* 0..1. */
    double (*score)(void *state, const char *expected, const char *actual);
    void (*free_fn)(void *state);
} ca_scorer_t;

void ca_scorer_free(ca_scorer_t *scorer);

/* Exact match after trimming and case folding - and nothing else. Deliberately
 * strict: a scorer that is lenient in ways nobody wrote down makes every
 * benchmark unrepeatable. */
ca_scorer_t *ca_exact_match_scorer_new(void);

size_t ca_built_in_scorers_count(void);
ca_scorer_t *ca_built_in_scorers_at(size_t index);

/* -- telephony carriers --------------------------------------------------- */

/*
 * Twilio, Telnyx and Plivo.
 *
 * Three carriers behind the one telephony seam, each with its own session type
 * because the media paths genuinely differ - and each taking its credentials
 * from the host at construction. None reads an environment variable, and one
 * with no credentials is absent rather than broken. The failure that prevents
 * is a build that can place real calls because a variable was set.
 */
typedef struct ca_carrier_call_session ca_carrier_call_session_t;

void ca_carrier_call_session_free(ca_carrier_call_session_t *session);

bool ca_carrier_call_session_send_audio(ca_carrier_call_session_t *session,
                                        const uint8_t *pcm, size_t len);

bool ca_carrier_call_session_hangup(ca_carrier_call_session_t *session);

struct ca_telephony_carrier;

struct ca_telephony_carrier *ca_twilio_carrier_new(const char *account_sid,
                                                   const char *auth_token,
                                                   void *http);

struct ca_telephony_carrier *ca_telnyx_carrier_new(const char *api_key, void *http);

struct ca_telephony_carrier *ca_plivo_carrier_new(const char *auth_id,
                                                  const char *auth_token,
                                                  void *http);

ca_carrier_call_session_t *ca_twilio_call_session_open(const char *call_sid,
                                                       void *transport);

ca_carrier_call_session_t *ca_telnyx_call_session_open(const char *call_control_id,
                                                       void *transport);

ca_carrier_call_session_t *ca_plivo_call_session_open(const char *call_uuid,
                                                      void *transport);

/* -- image generation and integrations ------------------------------------ */

typedef struct {
    const char *api_key;
    char *endpoint;
    char *model;
    int width;
    int height;
    int count;
} ca_open_ai_image_options_t;

typedef struct {
    const char *api_key;
    char *endpoint;
    char *engine;
    int steps;
    double cfg_scale;
} ca_stability_image_options_t;

typedef struct ca_image_generator {
    void *state;
    const char *(*generator_id)(void *state);
    bool (*is_configured)(void *state);
    uint8_t *(*generate)(void *state, const char *prompt, size_t *out_len,
                         char **out_error);
    void (*free_fn)(void *state);
} ca_image_generator_t;

void ca_image_generator_free(ca_image_generator_t *generator);

ca_image_generator_t *ca_open_ai_image_generator_new(
    const ca_open_ai_image_options_t *options, void *http);

/* The generator id strings, in one place. A dashboard and a router that spell
 * the same backend differently silently split a metric in two. */
const char *ca_generator_ids_open_ai(void);
const char *ca_generator_ids_stability(void);
const char *ca_generator_ids_procedural(void);

typedef struct ca_calendar_connector ca_calendar_connector_t;
typedef struct ca_email_connector ca_email_connector_t;

void ca_calendar_connector_free(ca_calendar_connector_t *connector);
void ca_email_connector_free(ca_email_connector_t *connector);

typedef struct {
    char *base_url;
    const char *username;
    const char *password;
    char *calendar_path;
} ca_cal_dav_calendar_options_t;

typedef struct {
    const char *access_token;
    char *tenant_id;
    char *calendar_id;
} ca_ms_graph_calendar_options_t;

typedef struct {
    const char *access_token;
    char *tenant_id;
    char *mailbox;
} ca_ms_graph_email_options_t;

/* CalDAV and IMAP are listed LAST in the connector registries and built first
 * here, because they are the ones that mean somebody with an unlisted provider
 * is not shut out. */
ca_calendar_connector_t *ca_cal_dav_calendar_connector_new(
    const ca_cal_dav_calendar_options_t *options, void *http);

ca_calendar_connector_t *ca_google_calendar_connector_new(const char *access_token,
                                                          void *http);

ca_calendar_connector_t *ca_ms_graph_calendar_connector_new(
    const ca_ms_graph_calendar_options_t *options, void *http);

ca_email_connector_t *ca_imap_email_connector_new(const char *host, int port,
                                                  const char *username,
                                                  const char *password);

ca_email_connector_t *ca_gmail_email_connector_new(const char *access_token,
                                                   void *http);

ca_email_connector_t *ca_ms_graph_email_connector_new(
    const ca_ms_graph_email_options_t *options, void *http);

typedef struct ca_home_automation_connector {
    void *state;
    bool (*set_state)(void *state, const char *entity_id, const char *value);
    char *(*get_state)(void *state, const char *entity_id);
    void (*free_fn)(void *state);
} ca_home_automation_connector_t;

void ca_home_automation_connector_free(ca_home_automation_connector_t *connector);

ca_home_automation_connector_t *ca_home_automation_connector_new(void);

ca_home_automation_connector_t *ca_home_assistant_connector_new(const char *base_url,
                                                                const char *token,
                                                                void *http);

/* Weather and routing from open services - no key, no account, no tracking of
 * who asked. */
char *ca_open_meteo_weather_provider_fetch(double latitude, double longitude,
                                           void *http, char **out_error);

char *ca_osrm_routing_provider_route(double from_lat, double from_lon,
                                     double to_lat, double to_lon,
                                     void *http, char **out_error);

char *ca_rss_news_source_fetch(const char *feed_url, void *http, char **out_error);
char *ca_news_api_source_fetch(const char *api_key, const char *query, void *http,
                               char **out_error);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_TOOLING_H */
