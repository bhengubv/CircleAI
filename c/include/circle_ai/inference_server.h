#ifndef CIRCLE_AI_INFERENCE_SERVER_H
#define CIRCLE_AI_INFERENCE_SERVER_H

/*
 * inference_server.h — CircleAI.Inference.Server contracts + in-memory
 * handlers (C11 port).
 *
 * The C# project is an ASP.NET Core minimal-API server. Per the task brief we
 * port the request/response CONTRACTS + the ROUTING LOGIC as in-memory handlers
 * behind interfaces — no real socket server. Ported surface:
 *
 *   - IInferenceBridge seam (the daemon contract the endpoints route to)
 *   - IBridgeFactory + UnconfiguredBridgeFactory (materialise a bridge)
 *   - IInferenceServerModelRegistry + InferenceServerModelRegistry
 *   - IModelLifecycleManager + ModelLifecycleManager (admission gate)
 *   - ICompanionSessionResolver + InMemoryCompanionSessionResolver
 *   - INativeRuntimeStatus + NativeRuntimeStatus
 *   - ApiKeyAuthHandler (constant-time key match, disabled-passthrough)
 *   - ChatCompletion{Request,Response,...} / Embeddings{Request,Response}
 *     / ErrorResponse DTOs + the chat/embeddings handler routing
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * *_free, deep-copied returns, errors via NULL / count == SIZE_MAX / false /
 * a status enum. No pthreads. Pure C11 + libc.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Backend / tier enums (mirror CircleAI.Runtime.Backends)
 * =========================================================================== */

typedef enum {
    CA_BACKEND_CPU       = 0,
    CA_BACKEND_CUDA      = 1,
    CA_BACKEND_VULKAN    = 2,
    CA_BACKEND_OPENCL    = 3,
    CA_BACKEND_METAL     = 4,
    CA_BACKEND_ASCEND    = 5,
    CA_BACKEND_CAMBRICON = 6,
    CA_BACKEND_COREML    = 7
} ca_backend_kind_t;

typedef enum {
    CA_TIER0_TINY     = 0,
    CA_TIER1_SMALL    = 1,
    CA_TIER2_MEDIUM   = 2,
    CA_TIER3_LARGE    = 3,
    CA_TIER4_FRONTIER = 4
} ca_capability_tier_t;

/* Parse a backend / tier name case-insensitively. Returns false on unknown. */
bool ca_backend_kind_parse(const char *s, ca_backend_kind_t *out);
bool ca_capability_tier_parse(const char *s, ca_capability_tier_t *out);

/* ===========================================================================
 * InferenceRequest / InferenceResponse (Hosting.InferenceBridge)
 * =========================================================================== */

typedef enum {
    CA_INFER_COMPLETED         = 0,
    CA_INFER_STOPPED_BY_TOKEN  = 1,
    CA_INFER_STOPPED_BY_LENGTH = 2,
    CA_INFER_FAILED            = 3,
    CA_INFER_CANCELLED         = 4
} ca_inference_status_t;

typedef struct {
    char        *model_id;        /* owned */
    char        *prompt;          /* owned */
    int          max_output_tokens;
    float        temperature;
    float        top_p;
    char       **stop_sequences;  /* owned array of owned strings */
    size_t       stop_count;
} ca_inference_request_t;

/* Free the request's owned fields (not the struct). */
void ca_inference_request_free(ca_inference_request_t *r);

typedef struct {
    char                 *output_text;      /* owned */
    int                   output_token_count;
    int                   prompt_token_count;
    ca_inference_status_t status;
    double                inference_millis;
    char                 *failure_message;  /* owned; NULL unless Failed */
    char                 *reasoning_text;   /* owned; NULL when none */
} ca_inference_response_t;

void ca_inference_response_free(ca_inference_response_t *r);

/* ===========================================================================
 * IInferenceBridge seam
 * ===========================================================================
 *
 * The daemon contract the endpoints route to. Injected as a vtable so the tests
 * (and the real MNN host) supply the behaviour. complete fills *out (owned) and
 * returns true; a false return signals a hard bridge failure.
 */

typedef struct ca_inference_bridge ca_inference_bridge_t;

typedef struct {
    /* Run one completion. Fill *out (caller frees via
     * ca_inference_response_free) and return true; false => bridge failure. */
    bool (*complete)(void *state, const ca_inference_request_t *req,
                     ca_inference_response_t *out);
    /* Optional: dispose owned state on bridge destroy. */
    void (*destroy)(void *state);
    void *state;
} ca_inference_bridge_vtable_t;

/* Wrap a vtable as a bridge (takes ownership of vtable.state via destroy on
 * ca_inference_bridge_destroy). Returns NULL on OOM. */
ca_inference_bridge_t *ca_inference_bridge_create(ca_inference_bridge_vtable_t vt);
void ca_inference_bridge_destroy(ca_inference_bridge_t *b);
bool ca_inference_bridge_complete(ca_inference_bridge_t *b,
                                  const ca_inference_request_t *req,
                                  ca_inference_response_t *out);

/*
 * An "echo" reference bridge — deterministic, no native deps. It returns
 * OutputText = "echo:" + the request prompt, PromptTokenCount / OutputTokenCount
 * as the 1-token-per-4-chars approximation, Status = Completed. Stands in for
 * LocalProcessInferenceBridge in tests.
 */
ca_inference_bridge_t *ca_echo_inference_bridge_create(void);

/* ===========================================================================
 * IBridgeFactory
 * =========================================================================== */

typedef struct ca_bridge_factory ca_bridge_factory_t;

typedef struct {
    /* Materialise a bridge for (model_id, backend, tier). Return NULL to fail
     * the load (mirrors UnconfiguredBridgeFactory throwing). */
    ca_inference_bridge_t *(*create)(void *state, const char *model_id,
                                     ca_backend_kind_t backend, ca_capability_tier_t tier);
    void (*destroy)(void *state);
    void *state;
} ca_bridge_factory_vtable_t;

ca_bridge_factory_t *ca_bridge_factory_create(ca_bridge_factory_vtable_t vt);
void ca_bridge_factory_destroy(ca_bridge_factory_t *f);
ca_inference_bridge_t *ca_bridge_factory_make(ca_bridge_factory_t *f, const char *model_id,
                                              ca_backend_kind_t backend,
                                              ca_capability_tier_t tier);

/* UnconfiguredBridgeFactory — always returns NULL (refuses every load). */
ca_bridge_factory_t *ca_unconfigured_bridge_factory_create(void);

/* An echo factory — returns a fresh ca_echo_inference_bridge for any model
 * (test stand-in for MnnInferenceBridgeFactory). */
ca_bridge_factory_t *ca_echo_bridge_factory_create(void);

/* ===========================================================================
 * ITextEmbedder seam (for /v1/embeddings)
 * =========================================================================== */

typedef struct ca_text_embedder ca_text_embedder_t;

typedef struct {
    /* Embed one string into a freshly-allocated float[*out_dim] (caller frees).
     * Return NULL to fail. */
    float *(*generate)(void *state, const char *text, size_t *out_dim);
    void (*destroy)(void *state);
    void *state;
} ca_text_embedder_vtable_t;

ca_text_embedder_t *ca_text_embedder_create(ca_text_embedder_vtable_t vt);
void ca_text_embedder_destroy(ca_text_embedder_t *e);
float *ca_text_embedder_generate(ca_text_embedder_t *e, const char *text, size_t *out_dim);

/*
 * A deterministic hashing embedder — maps text to a fixed-dim float vector via a
 * stable per-token hash. No native deps; stands in for a real embedder in tests.
 */
ca_text_embedder_t *ca_hashing_text_embedder_create(size_t dim);

/* ===========================================================================
 * IInferenceServerModelRegistry
 * =========================================================================== */

typedef struct ca_inference_server_registry ca_inference_server_registry_t;

ca_inference_server_registry_t *ca_inference_server_registry_create(void);
/* Destroys the registry AND every bridge/embedder still registered. */
void ca_inference_server_registry_destroy(ca_inference_server_registry_t *r);

/* Register a chat bridge (takes ownership; replaces + destroys any prior one for
 * this id). Returns false on OOM / invalid args. */
bool ca_inference_server_registry_register(ca_inference_server_registry_t *r,
                                           const char *model_id, ca_inference_bridge_t *bridge);
/* Register an embedder (takes ownership; replaces + destroys prior). */
bool ca_inference_server_registry_register_embedder(ca_inference_server_registry_t *r,
                                                    const char *model_id, ca_text_embedder_t *embedder);
/* Remove + destroy the chat bridge for model_id. Returns true when one existed. */
bool ca_inference_server_registry_deregister(ca_inference_server_registry_t *r, const char *model_id);
/* Look up a chat bridge (borrowed; NULL when absent). */
ca_inference_bridge_t *ca_inference_server_registry_resolve(ca_inference_server_registry_t *r,
                                                            const char *model_id);
/* Look up an embedder (borrowed; NULL when absent). */
ca_text_embedder_t *ca_inference_server_registry_resolve_embedder(ca_inference_server_registry_t *r,
                                                                  const char *model_id);
/* All model ids (chat + embed, de-duplicated). *out_count set; freshly-allocated
 * array of owned strings (caller frees each + the array). NULL/0 when empty. */
char **ca_inference_server_registry_all_model_ids(ca_inference_server_registry_t *r, size_t *out_count);
/* Chat-capable model ids only. Same ownership as above. */
char **ca_inference_server_registry_chat_model_ids(ca_inference_server_registry_t *r, size_t *out_count);

/* ===========================================================================
 * IModelLifecycleManager
 * =========================================================================== */

typedef enum {
    CA_LOAD_LOADED            = 0,
    CA_LOAD_ALREADY_LOADED    = 1,
    CA_LOAD_INSUFFICIENT_VRAM = 2,
    CA_LOAD_INSUFFICIENT_RAM  = 3,
    CA_LOAD_FACTORY_FAILED    = 4
} ca_load_outcome_t;

typedef enum {
    CA_UNLOAD_UNLOADED  = 0,
    CA_UNLOAD_NOT_LOADED = 1
} ca_unload_outcome_t;

/* Runtime view of one loaded model (mirrors ModelLoadState). */
typedef struct {
    char                 *model_id;   /* owned */
    ca_backend_kind_t     backend;
    ca_capability_tier_t  tier;
    int64_t               vram_bytes;
    int64_t               ram_bytes;
    int64_t               loaded_at_unix_ms;
} ca_model_load_state_t;

void ca_model_load_state_free(ca_model_load_state_t *s);
void ca_model_load_states_free(ca_model_load_state_t *arr, size_t count);

/* Result of a load attempt. state is present only on Loaded/AlreadyLoaded. */
typedef struct {
    ca_load_outcome_t     outcome;
    bool                  has_state;
    ca_model_load_state_t state;
    char                 *rationale;  /* owned */
} ca_load_result_t;

void ca_load_result_free(ca_load_result_t *r);

typedef struct ca_model_lifecycle_manager ca_model_lifecycle_manager_t;

/*
 * Create over a registry (borrowed) and a fixed host profile (VRAM/RAM
 * ceilings, matching the cached-probe model). Returns NULL on OOM.
 */
ca_model_lifecycle_manager_t *ca_model_lifecycle_manager_create(
    ca_inference_server_registry_t *registry,
    int64_t total_physical_memory_bytes, int64_t gpu_vram_bytes);
void ca_model_lifecycle_manager_destroy(ca_model_lifecycle_manager_t *m);

/*
 * Load model_id via factory, running the admission gate first (already-loaded?
 * VRAM headroom on GPU backends? RAM headroom?). On CA_LOAD_LOADED the bridge is
 * registered in the registry. *out is filled (caller frees via
 * ca_load_result_free). Returns false only on a NULL/invalid argument.
 */
bool ca_model_lifecycle_manager_load(
    ca_model_lifecycle_manager_t *m, const char *model_id,
    ca_backend_kind_t backend, ca_capability_tier_t tier,
    int64_t vram_required_bytes, int64_t ram_required_bytes,
    ca_bridge_factory_t *factory, ca_load_result_t *out);

/* Unload + deregister + destroy the bridge. */
ca_unload_outcome_t ca_model_lifecycle_manager_unload(ca_model_lifecycle_manager_t *m,
                                                      const char *model_id);

/* Snapshot of every loaded model. Freshly-allocated array of *out_count states
 * (caller frees via ca_model_load_states_free). */
ca_model_load_state_t *ca_model_lifecycle_manager_list(ca_model_lifecycle_manager_t *m,
                                                       size_t *out_count);

int64_t ca_model_lifecycle_manager_total_vram(const ca_model_lifecycle_manager_t *m);
int64_t ca_model_lifecycle_manager_total_ram(const ca_model_lifecycle_manager_t *m);

/* ===========================================================================
 * ICompanionSessionResolver + InMemoryCompanionSessionResolver
 * ===========================================================================
 *
 * Sessions are opaque host objects. The resolver caches one per
 * (session_id, identity_id) and builds misses via an injected factory
 * (mirrors ICompanionSessionFactory). Failed construction does not poison the
 * cache.
 */

typedef struct ca_companion_session_resolver ca_companion_session_resolver_t;

/*
 * Session factory: build a session for identity_id. Return NULL to signal a
 * construction failure (the resolver drops the cache slot and propagates NULL).
 * The returned pointer is owned by the resolver; session_destroy frees it on
 * resolver destroy.
 */
typedef struct {
    void *(*create)(void *state, const char *identity_id);
    void  (*session_destroy)(void *session);
    void  (*state_destroy)(void *state);
    void  *state;
} ca_companion_session_factory_vtable_t;

ca_companion_session_resolver_t *ca_companion_session_resolver_create(
    ca_companion_session_factory_vtable_t vt);
void ca_companion_session_resolver_destroy(ca_companion_session_resolver_t *r);

/*
 * Resolve (or construct) the session for (session_id, identity_id). Returns the
 * borrowed session pointer, or NULL when either id is empty or the factory
 * returned NULL. Cached sessions are returned without re-invoking the factory.
 */
void *ca_companion_session_resolver_resolve(ca_companion_session_resolver_t *r,
                                            const char *session_id, const char *identity_id);
/* Number of cached sessions (diagnostics). */
int ca_companion_session_resolver_cached_count(const ca_companion_session_resolver_t *r);

/* ===========================================================================
 * INativeRuntimeStatus + NativeRuntimeStatus
 * ===========================================================================
 *
 * Holds the last-known native-runtime paths (mnnbridge / MNN core / extracted
 * root) surfaced by the diagnostics endpoint. In C# these come from
 * NativeRuntimePrep.NativeRuntimePaths; here they are three owned strings.
 */

typedef struct {
    char *mnnbridge_path;   /* owned; may be NULL */
    char *mnn_core_path;    /* owned; may be NULL */
    char *extracted_root;   /* owned; may be NULL */
} ca_native_runtime_paths_t;

void ca_native_runtime_paths_free(ca_native_runtime_paths_t *p);

typedef struct ca_native_runtime_status ca_native_runtime_status_t;

ca_native_runtime_status_t *ca_native_runtime_status_create(void);
void ca_native_runtime_status_destroy(ca_native_runtime_status_t *s);

/* Record a prep result (deep-copies the strings). Returns false on OOM. */
bool ca_native_runtime_status_update(ca_native_runtime_status_t *s,
                                     const char *mnnbridge_path,
                                     const char *mnn_core_path,
                                     const char *extracted_root);
/*
 * Latest paths (deep copy into *out; caller frees via
 * ca_native_runtime_paths_free). Returns false when no prep has happened yet
 * (out zeroed).
 */
bool ca_native_runtime_status_latest(const ca_native_runtime_status_t *s,
                                     ca_native_runtime_paths_t *out);

/* ===========================================================================
 * ApiKeyAuthHandler
 * =========================================================================== */

typedef struct ca_api_key_auth ca_api_key_auth_t;

typedef enum {
    CA_AUTH_SUCCESS_ANONYMOUS = 0, /* auth disabled — anonymous principal */
    CA_AUTH_SUCCESS           = 1, /* key matched */
    CA_AUTH_NO_RESULT         = 2, /* header absent/blank */
    CA_AUTH_FAIL              = 3  /* key present but not in allow-list */
} ca_auth_result_t;

/*
 * Create an API-key handler. enabled=false makes every call succeed anonymously.
 * header_name is the header the caller presents the key in (e.g. "X-Api-Key").
 * keys[] is the allow-list (deep-copied). Returns NULL on OOM.
 */
ca_api_key_auth_t *ca_api_key_auth_create(bool enabled, const char *header_name,
                                          const char *const *keys, size_t key_count);
void ca_api_key_auth_destroy(ca_api_key_auth_t *h);

/*
 * Authenticate a presented header value (as read from header_name).
 * presented==NULL/blank => CA_AUTH_NO_RESULT (when enabled). Matching is
 * constant-time over equal-length keys (mirrors CryptographicOperations
 * .FixedTimeEquals).
 */
ca_auth_result_t ca_api_key_auth_authenticate(const ca_api_key_auth_t *h, const char *presented);
/* The configured header name (borrowed). */
const char *ca_api_key_auth_header_name(const ca_api_key_auth_t *h);

/* ===========================================================================
 * Chat completion + embeddings DTOs and handler routing
 * =========================================================================== */

/* One message in the chat-completion conversation (mirrors ChatCompletionMessage). */
typedef struct {
    char *role;              /* owned */
    char *content;           /* owned */
    char *name;              /* owned; may be NULL */
    char *reasoning_content; /* owned; may be NULL */
} ca_chat_completion_message_t;

/* Request body (mirrors ChatCompletionRequest). */
typedef struct {
    char                         *model;         /* owned */
    ca_chat_completion_message_t *messages;      /* owned array */
    size_t                        message_count;
    bool                          has_temperature; float temperature;
    bool                          has_top_p;       float top_p;
    bool                          has_max_tokens;  int   max_tokens;
    bool                          stream;
    char                        **stop;           /* owned array of owned strings; may be NULL */
    size_t                        stop_count;
    char                         *user;           /* owned; may be NULL */
} ca_chat_completion_request_t;

void ca_chat_completion_request_free(ca_chat_completion_request_t *r);

/* Token-usage block. */
typedef struct {
    int prompt_tokens;
    int completion_tokens;
    int total_tokens;
} ca_usage_info_t;

/* One non-streaming choice (mirrors ChatCompletionChoice). */
typedef struct {
    int                          index;
    ca_chat_completion_message_t message; /* owned */
    char                        *finish_reason; /* owned */
} ca_chat_completion_choice_t;

/* Response body (mirrors ChatCompletionResponse). */
typedef struct {
    char                        *id;       /* owned */
    char                        *object;   /* owned; "chat.completion" */
    int64_t                      created;  /* Unix seconds */
    char                        *model;    /* owned */
    ca_chat_completion_choice_t *choices;  /* owned array */
    size_t                       choice_count;
    ca_usage_info_t              usage;
} ca_chat_completion_response_t;

void ca_chat_completion_response_free(ca_chat_completion_response_t *r);

/* One SSE delta fragment produced by the streaming router. kind: 0 content,
 * 1 reasoning; the terminal fragment has is_final=true + finish_reason. */
typedef struct {
    int   kind;          /* 0 content, 1 reasoning */
    char *text;          /* owned; may be NULL on the final frame */
    bool  is_final;
    char *finish_reason; /* owned; set on the final frame */
} ca_chat_stream_delta_t;

/* Handler outcome (mirrors the HTTP status the endpoint would return). */
typedef enum {
    CA_HANDLER_OK             = 200,
    CA_HANDLER_BAD_REQUEST    = 400,
    CA_HANDLER_NOT_FOUND      = 404,
    CA_HANDLER_INTERNAL_ERROR = 500,
    CA_HANDLER_TIMEOUT        = 504
} ca_handler_status_t;

/* An OpenAI-shaped error (mirrors ErrorResponse.Of). All owned. */
typedef struct {
    char *message;  /* owned */
    char *type;     /* owned */
    char *code;     /* owned; may be NULL */
} ca_error_response_t;

void ca_error_response_free(ca_error_response_t *e);

/*
 * Non-streaming /v1/chat/completions routing. Validates the body, resolves the
 * bridge, calls complete, and builds a ChatCompletionResponse. On CA_HANDLER_OK
 * *out_resp is filled (caller frees via ca_chat_completion_response_free) and
 * *out_err is zeroed. On any error status *out_err is filled (caller frees via
 * ca_error_response_free) and *out_resp is zeroed. Always returns the status.
 */
ca_handler_status_t ca_handle_chat_completion(
    ca_inference_server_registry_t *registry, const ca_chat_completion_request_t *body,
    ca_chat_completion_response_t *out_resp, ca_error_response_t *out_err);

/*
 * Streaming /v1/chat/completions routing. Drives the injected on_delta callback
 * with the role frame, each content/reasoning frame, and the final stop frame
 * (mirrors StreamResponseAsync). Returns the status; on a validation/resolve
 * error the callback is not invoked and *out_err is filled.
 */
typedef void (*ca_chat_stream_delta_fn)(const ca_chat_stream_delta_t *delta, void *user);

ca_handler_status_t ca_handle_chat_completion_stream(
    ca_inference_server_registry_t *registry, const ca_chat_completion_request_t *body,
    ca_chat_stream_delta_fn on_delta, void *user, ca_error_response_t *out_err);

/* Embeddings request (mirrors EmbeddingsRequest; input is a list of strings). */
typedef struct {
    char  *model;         /* owned */
    char **inputs;        /* owned array of owned strings */
    size_t input_count;
    char  *user;          /* owned; may be NULL */
} ca_embeddings_request_t;

void ca_embeddings_request_free(ca_embeddings_request_t *r);

/* One embedding row (mirrors EmbeddingDatum). */
typedef struct {
    int     index;
    float  *embedding;   /* owned */
    size_t  dim;
} ca_embedding_datum_t;

/* Embeddings response (mirrors EmbeddingsResponse). */
typedef struct {
    char                 *object;  /* owned; "list" */
    ca_embedding_datum_t *data;    /* owned array */
    size_t                data_count;
    char                 *model;   /* owned */
    ca_usage_info_t       usage;
} ca_embeddings_response_t;

void ca_embeddings_response_free(ca_embeddings_response_t *r);

/*
 * /v1/embeddings routing. Validates, resolves the embedder, embeds each input,
 * and builds the response (input-token usage only, matching the endpoint). Same
 * *out_resp / *out_err ownership contract as ca_handle_chat_completion.
 */
ca_handler_status_t ca_handle_embeddings(
    ca_inference_server_registry_t *registry, const ca_embeddings_request_t *body,
    ca_embeddings_response_t *out_resp, ca_error_response_t *out_err);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_INFERENCE_SERVER_H */
