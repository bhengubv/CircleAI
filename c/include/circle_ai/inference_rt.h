#ifndef CIRCLE_AI_INFERENCE_RT_H
#define CIRCLE_AI_INFERENCE_RT_H

/*
 * inference_rt.h — CircleAI.Inference runtime surface (C11 port).
 *
 * Ports the pure/deterministic surface of CircleAI.Inference that isn't
 * MNN-native:
 *
 *   - ChatCapability / VisionInput / PowerBudget + PowerBudgetPolicy(.Resolve)
 *     + KvCompressionMode
 *   - IChatGenerator (deterministic local generator standing in for
 *     QwenTextGenerator / KimiVlGenerator) + GenerateResponse / Stream
 *     fragments + ContextWindowBudgetManager
 *   - IModelDownloadService + ModelDownloadService (single-file + bundle shapes,
 *     SHA-256 verify, disk-space, installed.json manifest). The network is
 *     abstracted behind an injected fetch callback — no sockets.
 *   - PrefixCacheService (LRU-by-mtime prefix cache)
 *   - ILayerStreamingRunner + LayerStreamingOrchestrator + LayerShardDiscovery
 *   - IFeedbackTrainingQueue + FileBackedFeedbackTrainingQueue
 *   - LoRAAdapterManager seam + NightlyAdapterTrainer (RunOnce; no threads)
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * *_free, deep-copied returned arrays the caller frees, errors via NULL /
 * count == SIZE_MAX / false. No pthreads. Pure C11 + libc.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#include "inference.h"     /* ca_generation_options_t, ca_power_budget_t */
#include "models_v15.h"    /* ca_chat_fragment_t, ca_bundle_file_t */

#ifdef __cplusplus
extern "C" {
#endif

/*
 * NOTE: CircleAI.Inference.ChatCapability is already ported as
 * ca_chat_capability_t / CA_CHAT_CAP_* in selector.h — included via the umbrella
 * header. It is not re-declared here to avoid a redefinition.
 */

/* ===========================================================================
 * VisionInput (CircleAI.Inference.VisionInput)
 * =========================================================================== */

typedef struct {
    uint8_t *image_bytes;   /* owned deep copy */
    size_t   image_len;
    char    *mime_type;     /* owned; may be NULL */
} ca_vision_input_t;

/* Create with a deep copy of image_bytes[len]. mime_type may be NULL. Returns
 * NULL when image_bytes is NULL, len == 0, or on OOM. */
ca_vision_input_t *ca_vision_input_create(const uint8_t *image_bytes, size_t len,
                                          const char *mime_type);
void ca_vision_input_destroy(ca_vision_input_t *v);

/* ===========================================================================
 * KvCompressionMode + PowerBudget policy (CircleAI.Inference.PowerBudgetPolicy)
 * =========================================================================== */

typedef enum {
    CA_KV_OFF               = 0,   /* full FP16 KV */
    CA_KV_TURBO_QUANT_4BIT  = 1    /* TQ4 */
} ca_kv_compression_mode_t;

/* Resolved budget mirrors PowerBudgetPolicy.Resolution. */
typedef struct {
    int                      max_tokens;
    ca_kv_compression_mode_t preferred_kv_mode;
    bool                     prefer_smaller_model_in_chain;
} ca_power_budget_resolution_t;

/*
 * Map a ca_power_budget_t to concrete knobs, matching PowerBudgetPolicy.Resolve:
 *   - Normal + battery < 15  -> downgraded to Low
 *   - High   + thermalThrottled -> downgraded to Normal
 * Token caps: None=requested, Low=min(req,64), Normal=min(req,512),
 * High=min(req,2048). battery_level_percent < 0 means "unknown".
 */
ca_power_budget_resolution_t ca_power_budget_resolve(
    ca_power_budget_t budget, int requested_max_tokens,
    int battery_level_percent /* <0 = unknown */, bool thermal_throttled);

/* ===========================================================================
 * IChatGenerator — deterministic local generator
 * ===========================================================================
 *
 * A concrete, deterministic IChatGenerator standing in for the MNN-native
 * QwenTextGenerator / KimiVlGenerator. It builds a Qwen ChatML prompt from the
 * chat history and produces a deterministic reply derived from the last user
 * turn (echo-style), optionally emitting a <think> reasoning block that the
 * fragment router splits into reasoning vs content — exactly as the native
 * generators would surface it to callers.
 */

typedef struct ca_local_chat_generator ca_local_chat_generator_t;

/* One chat message (role + content + optional image, mirrors ChatMessage). */
typedef struct {
    const char    *role;         /* "system"/"user"/"assistant"/"tool" */
    const char    *content;
    const uint8_t *image_bytes;  /* may be NULL */
    size_t         image_len;
} ca_chat_msg_t;

/*
 * Create a deterministic generator.
 *   model_id       : logical id echoed in prompt-cache keys (borrowed; copied).
 *   context_tokens : context window hint (> 0).
 * Returns NULL on invalid args / OOM.
 */
ca_local_chat_generator_t *ca_local_chat_generator_create(const char *model_id,
                                              int context_tokens);
void ca_local_chat_generator_destroy(ca_local_chat_generator_t *g);

/*
 * Generate a complete reply. Returns a freshly-allocated UTF-8 string (caller
 * frees) — content only, reasoning stripped. NULL on invalid args / OOM.
 * opts may be NULL (defaults applied).
 */
char *ca_local_chat_generator_generate(ca_local_chat_generator_t *g,
                                 const ca_chat_msg_t *messages, size_t count,
                                 const ca_generation_options_t *opts);

/*
 * Structured response (mirrors GenerateResponseAsync). *out is filled with
 * freshly-allocated text + reasoning_content (both caller-frees via
 * ca_chat_response_free). token counts are the word-split approximation from the
 * default IChatGenerator. Returns false on invalid args / OOM.
 */
typedef struct {
    char              *text;               /* owned */
    int                tokens_in;
    int                tokens_out;
    double             latency_ms;
    ca_finish_reason_t finish_reason;
    char              *reasoning_content;  /* owned; NULL when none */
} ca_chat_gen_response_t;

bool ca_local_chat_generator_generate_response(ca_local_chat_generator_t *g,
                                         const ca_chat_msg_t *messages, size_t count,
                                         const ca_generation_options_t *opts,
                                         ca_chat_gen_response_t *out);
void ca_chat_gen_response_free(ca_chat_gen_response_t *r);

/*
 * Fragment streaming (mirrors StreamFragmentsAsync). The callback fires once per
 * emitted fragment; fragment.text is valid only for the duration of the call.
 * Returns false on invalid args. Drives content-only for callers that filter,
 * plus a leading reasoning fragment when opts->include_reasoning != 0.
 */
typedef void (*ca_chat_stream_fn)(const ca_chat_fragment_t *fragment, void *user);

bool ca_local_chat_generator_stream_fragments(ca_local_chat_generator_t *g,
                                        const ca_chat_msg_t *messages, size_t count,
                                        const ca_generation_options_t *opts,
                                        ca_chat_stream_fn on_fragment, void *user);

/*
 * SaveSession / LoadSession default-marker round-trip (mirrors the interface
 * default methods). Save writes a portable marker file; Load verifies it starts
 * with "circleai-session-marker". Returns false on IO error / empty path.
 */
bool ca_local_chat_generator_save_session(ca_local_chat_generator_t *g, const char *path);
bool ca_local_chat_generator_load_session(ca_local_chat_generator_t *g, const char *path);

/* Build a Qwen ChatML prompt (exposed for tests; mirrors BuildQwenChatPrompt).
 * Returns a freshly-allocated string (caller frees). */
char *ca_build_qwen_chat_prompt(const ca_chat_msg_t *messages, size_t count);

/* ===========================================================================
 * ContextWindowBudgetManager
 * =========================================================================== */

typedef struct ca_context_window_budget ca_context_window_budget_t;

/* context_size > 0; eviction_threshold in [0,1] (default 0.85). Returns NULL on
 * an invalid argument. */
ca_context_window_budget_t *ca_context_window_budget_create(int context_size,
                                                            double eviction_threshold);
void ca_context_window_budget_destroy(ca_context_window_budget_t *b);

int    ca_context_window_budget_context_size(const ca_context_window_budget_t *b);
int    ca_context_window_budget_used_tokens(const ca_context_window_budget_t *b);
int    ca_context_window_budget_remaining_tokens(const ca_context_window_budget_t *b);
double ca_context_window_budget_fill_ratio(const ca_context_window_budget_t *b);
double ca_context_window_budget_eviction_threshold(const ca_context_window_budget_t *b);
bool   ca_context_window_budget_should_evict(const ca_context_window_budget_t *b);

/* Records one exchange. Returns false when either count is negative. */
bool ca_context_window_budget_record_exchange(ca_context_window_budget_t *b,
                                              int prompt_tokens, int completion_tokens);
/* Tokens to drop so fill returns to target_fill_ratio (in [0,1]); 0 when already
 * at/below. Returns -1 on an out-of-range target. */
int  ca_context_window_budget_calculate_eviction_count(const ca_context_window_budget_t *b,
                                                       double target_fill_ratio);
void ca_context_window_budget_reset(ca_context_window_budget_t *b);

/* ===========================================================================
 * PrefixCacheService
 * =========================================================================== */

typedef struct ca_prefix_cache ca_prefix_cache_t;

/* Root the cache at root (created on demand). Returns NULL when root is empty. */
ca_prefix_cache_t *ca_prefix_cache_create(const char *root);
void ca_prefix_cache_destroy(ca_prefix_cache_t *c);

/*
 * Compute the cache key for (model_id, system_prompt). Returns a freshly-
 * allocated "<modelHash16>_<systemHash16>" (caller frees), or NULL when model_id
 * is empty/whitespace or system_prompt is NULL/empty (nothing to key on).
 */
char *ca_prefix_cache_key_for(const char *model_id, const char *system_prompt);

/* Session file path for key (freshly-allocated; caller frees). NULL on OOM. */
char *ca_prefix_cache_path_for(const ca_prefix_cache_t *c, const char *key);
/* True when a cached entry exists for key. */
bool  ca_prefix_cache_has_entry(const ca_prefix_cache_t *c, const char *key);
/* Touch the entry mtime (best-effort). */
void  ca_prefix_cache_touch(const ca_prefix_cache_t *c, const char *key);
/* Evict oldest *.session files until the dir is under the 500 MB cap. */
void  ca_prefix_cache_evict_if_needed(ca_prefix_cache_t *c);

/* ===========================================================================
 * IModelDownloadService + ModelDownloadService
 * ===========================================================================
 *
 * Bundle-shape downloader with SHA-256 verification. The network is injected via
 * a fetch callback: given a URL, write the file at dest_path and return true.
 * The bundled test fetch fills dest_path with bytes registered per-URL. When no
 * fetch is set, downloads fail (returns false).
 */

typedef struct ca_model_download_service ca_model_download_service_t;

/* One bundle file spec (mirrors BundleFileSpec). */
typedef struct {
    const char *name;         /* relative path */
    const char *sha256;       /* "sha256:<hex>" or bare hex */
    int64_t     size_bytes;
} ca_bundle_file_spec_t;

/* Progress callback: p in [0,1]. Optional. */
typedef void (*ca_download_progress_ratio_fn)(void *user, double p);

/* Fetch callback: write the resource at url into dest_path; return true on
 * success. per_file progress (0..1) may be reported via progress/progress_user
 * (both may be NULL). */
typedef bool (*ca_model_fetch_fn)(void *user, const char *url, const char *dest_path,
                                  ca_download_progress_ratio_fn progress,
                                  void *progress_user);

/* Create a service rooted at storage_directory (created on demand). fetch may be
 * NULL. fetch_user is passed through. Returns NULL when storage_directory is
 * empty or on OOM. */
ca_model_download_service_t *ca_model_download_service_create(
    const char *storage_directory, ca_model_fetch_fn fetch, void *fetch_user);
void ca_model_download_service_destroy(ca_model_download_service_t *s);

/*
 * Ensure a single model file at <storage>/<modelId>.gguf, verifying
 * expected_sha256 when non-NULL (a mismatch deletes the file and re-downloads;
 * a fresh-download mismatch fails and deletes the partial). download_uri is the
 * URL passed to the fetch callback. On success *out_path is a freshly-allocated
 * absolute path (caller frees). progress may be NULL. Returns false on failure.
 */
bool ca_model_download_service_ensure_model(
    ca_model_download_service_t *s, const char *model_id, const char *download_uri,
    const char *expected_sha256 /* may be NULL */,
    ca_download_progress_ratio_fn progress, void *progress_user, char **out_path);

/*
 * Ensure every file in bundle_files[] under <storage>/<modelId>/, each verified
 * against its pinned SHA-256. Cached+valid files are skipped. repo builds the
 * primary + fallback URLs. On success *out_dir is the model directory (caller
 * frees). Returns false on any failure (empty repo, empty list, a file with no
 * name, a SHA mismatch, or a fetch failure on both URLs).
 */
bool ca_model_download_service_ensure_bundle(
    ca_model_download_service_t *s, const char *model_id, const char *repo,
    const ca_bundle_file_spec_t *bundle_files, size_t count,
    ca_download_progress_ratio_fn progress, void *progress_user, char **out_dir);

/* True when the single-file OR the bundle directory exists for model_id. */
bool ca_model_download_service_is_model_cached(ca_model_download_service_t *s,
                                               const char *model_id);
/* Delete the single file and/or bundle directory (no-op when absent). */
void ca_model_download_service_delete_model(ca_model_download_service_t *s,
                                            const char *model_id);
/* Free bytes on the drive hosting the storage directory (-1 when unknown). */
int64_t ca_model_download_service_available_disk_space(ca_model_download_service_t *s);

/*
 * Stamp installed.json in model_dir (best-effort; mirrors
 * WriteInstalledManifestAsync). Returns false on OOM but never a hard error
 * otherwise. version may be NULL, repo may be NULL.
 */
bool ca_model_download_service_write_installed_manifest(
    const char *model_dir, const char *model_id, const char *version,
    const char *repo, const ca_bundle_file_spec_t *bundle_files, size_t count);

/* Strip an optional "sha256:" style algorithm prefix (mirrors
 * StripShaAlgorithmPrefix). Returns freshly-allocated hex (caller frees). */
char *ca_strip_sha_algorithm_prefix(const char *raw);

/* Build the ModelScope primary/fallback URLs for (repo, file_name). Freshly
 * allocated; caller frees. */
char *ca_modelscope_primary_url(const char *repo, const char *file_name);
char *ca_modelscope_fallback_url(const char *repo, const char *file_name);

/* ===========================================================================
 * ILayerStreamingRunner + LayerStreamingOrchestrator + LayerShardDiscovery
 * =========================================================================== */

typedef struct {
    int         layer_index;
    char       *weight_shard_path;  /* owned */
    int64_t     approx_bytes;
} ca_layer_weight_shard_t;

typedef struct {
    char                     *model_id;   /* owned */
    int                       total_layers;
    ca_layer_weight_shard_t  *shards;     /* owned array */
    size_t                    shard_count;
    int64_t                   approx_parameter_bytes;
} ca_layer_streaming_plan_t;

void ca_layer_streaming_plan_free(ca_layer_streaming_plan_t *p);

/*
 * Host-supplied runner. run_layer forwards one layer: given the shard and the
 * input hidden state (in_hidden[in_len]), it writes the layer output into a
 * freshly-allocated *out_hidden (of *out_len floats — the orchestrator frees
 * each intermediate) and returns true. evict drops the layer (may be NULL).
 * user is passed through.
 */
typedef struct {
    const char *backend_id;
    bool        is_available;
    bool (*run_layer)(void *user, const ca_layer_weight_shard_t *shard,
                      const float *in_hidden, size_t in_len,
                      float **out_hidden, size_t *out_len);
    void (*evict)(void *user, int layer_index);
    void       *user;
} ca_layer_streaming_runner_t;

/*
 * Stream every layer in plan, evicting after each. *out_hidden is the final
 * hidden state (freshly-allocated; caller frees), *out_len its length. on_layer
 * (may be NULL) fires after each layer with (layer_index, hidden, len). Returns
 * false when the plan has no shards, the runner is unavailable, or a layer fails.
 */
bool ca_layer_streaming_forward(
    const ca_layer_streaming_runner_t *runner,
    const ca_layer_streaming_plan_t *plan,
    const float *initial_hidden, size_t initial_len,
    void (*on_layer)(void *user, int layer_index, const float *hidden, size_t len),
    void *on_layer_user,
    float **out_hidden, size_t *out_len);

/*
 * Discover "layer_NNN.*" shard files under model_directory and build a plan,
 * sorted by layer index (mirrors LayerShardDiscovery.Discover). Returns false
 * when model_id is empty or the directory can't be read. *out is filled; free
 * with ca_layer_streaming_plan_free.
 */
bool ca_layer_shard_discover(const char *model_id, const char *model_directory,
                             ca_layer_streaming_plan_t *out);

/* ===========================================================================
 * IFeedbackTrainingQueue + FileBackedFeedbackTrainingQueue
 * =========================================================================== */

/* One feedback-tagged turn (mirrors TrainingSample). */
typedef struct {
    char   *user_text;        /* owned */
    char   *assistant_text;   /* owned */
    char   *preferred_text;   /* owned */
    int     polarity;         /* +1 / -1 / 0 */
    int64_t at_unix_ms;
} ca_training_sample_t;

/* Deep-free one sample's owned strings (not the struct). */
void ca_training_sample_free(ca_training_sample_t *s);
/* Free an array of samples (each sample's strings + the array). */
void ca_training_samples_free(ca_training_sample_t *arr, size_t count);

typedef struct ca_feedback_training_queue ca_feedback_training_queue_t;

/* Append-only line-delimited file queue rooted at path (dir + empty file
 * created). Returns NULL when path is empty or on OOM. */
ca_feedback_training_queue_t *ca_feedback_training_queue_create(const char *path);
void ca_feedback_training_queue_destroy(ca_feedback_training_queue_t *q);

/* Number of pending samples (line count). */
int  ca_feedback_training_queue_pending(ca_feedback_training_queue_t *q);
/* Append one sample (deep-copied into a serialised line). Returns false on OOM /
 * IO error. */
bool ca_feedback_training_queue_enqueue(ca_feedback_training_queue_t *q,
                                        const ca_training_sample_t *sample);
/*
 * Drain up to max_samples from the front, rewriting the file with the remainder.
 * On success *out_arr is a freshly-allocated array of *out_count samples (caller
 * frees with ca_training_samples_free). Returns false when max_samples <= 0.
 * When the file is empty, *out_arr is NULL and *out_count is 0.
 */
bool ca_feedback_training_queue_drain(ca_feedback_training_queue_t *q, int max_samples,
                                      ca_training_sample_t **out_arr, size_t *out_count);

/* ===========================================================================
 * LoRAAdapterManager seam + NightlyAdapterTrainer
 * ===========================================================================
 *
 * LoRAAdapterManager is native in C#. Here it's an injected seam: train_step
 * returns a loss (or a negative sentinel to signal "training not supported",
 * mirroring the C# NotSupportedException re-queue path). save_adapter / apply
 * persist + swap the adapter.
 */

typedef struct {
    /* Return >= 0 loss on success; return < 0 to signal "MNN not built with
     * training" (the trainer re-queues the batch and skips the run). */
    float (*train_step)(void *user, const int *input, size_t input_len,
                        const int *target, size_t target_len,
                        float learning_rate, int lora_rank);
    bool  (*save_adapter)(void *user, const char *path);
    bool  (*apply)(void *user, const char *path);
    void  *user;
} ca_lora_adapter_manager_t;

/* Options mirror NightlyAdapterTrainerOptions (thread/interval fields omitted —
 * RunOnce is synchronous here). tokenizer may be NULL (char-level fallback). */
typedef struct {
    int         min_batch_size;      /* default 16 */
    int         max_samples_per_run; /* default 256 */
    float       learning_rate;       /* default 1e-4 */
    int         lora_rank;           /* default 8 */
    const char *adapter_path;        /* default "circleai-lora.mnn" */
    /* Optional tokenizer: writes up to *len ids into out (caller-sized via a
     * two-call protocol: pass out==NULL to query length). Return the id count.
     * NULL => char-level (each UTF-16-style code unit becomes an id; here each
     * byte becomes an id). */
    size_t (*tokenizer)(void *user, const char *text, int *out, size_t out_cap);
    void       *tokenizer_user;
} ca_nightly_trainer_options_t;

/* Fill opts with the C# defaults. */
void ca_nightly_trainer_options_init(ca_nightly_trainer_options_t *opts);

/*
 * Drain + train in one pass (mirrors RunOnceAsync). Returns:
 *   result of the run via *out_steps (steps taken) and *out_avg_loss.
 * Behaviour:
 *   - pending < min_batch_size            -> *out_steps = 0, returns true (skip)
 *   - a train_step returns < 0 (unsupported) -> re-queue drained samples, skip,
 *     *out_steps = 0, returns true
 *   - otherwise trains, then save_adapter + apply; *out_steps > 0.
 * Returns false only on a NULL/invalid argument.
 */
bool ca_nightly_adapter_trainer_run_once(
    ca_feedback_training_queue_t *queue, const ca_lora_adapter_manager_t *adapter,
    const ca_nightly_trainer_options_t *opts, int *out_steps, float *out_avg_loss);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_INFERENCE_RT_H */
