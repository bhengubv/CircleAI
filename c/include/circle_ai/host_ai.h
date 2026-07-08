#ifndef CIRCLE_AI_HOST_AI_H
#define CIRCLE_AI_HOST_AI_H

/*
 * host_ai.h — CircleAI.Hosting core runtime (C11 port).
 *
 * Ports the long-lived butler surface and its transports from
 * src/CircleAI.Hosting:
 *
 *   IAIService                     — the single butler contract (vtable seam)
 *   AIService                      — deterministic default impl over a local
 *                                    IChatGenerator (ca_local_chat_generator_t),
 *                                    with the Qwen <tool_call> agentic loop,
 *                                    feedback signals, brownout hot-swap, and
 *                                    observer fan-out
 *   FallbackAIService              — local-first / cloud-fallback wrapper
 *                                    (RAM-threshold gate; the "available RAM"
 *                                    reading is an injected probe)
 *   AIApiClient                    — IAIService that proxies to a remote
 *                                    ButlerAPI over an injected HTTP transport
 *                                    seam (no sockets; SSE parse is real)
 *   IAIEndpoint / InProcessEndpoint / HttpLoopbackEndpoint
 *                                  — transport-agnostic exposure of an
 *                                    IAIService. The loopback endpoint's wire
 *                                    is an injected request/stream seam; the
 *                                    token auth + constant-time compare + route
 *                                    dispatch are real.
 *   IAIObserver + event records    — neutral observability hook
 *   PushAIObserver / AetherAIObserver + IPushNotificationSender /
 *                                    ICircleAetherTransport — bridges
 *   IMemoryPressureSource + Null/Manual sources + MemoryPressureLevel
 *   BrownoutReason
 *   AIOptions                      — the butler configuration bag
 *   ParseToolCall                  — Qwen3 <tool_call> extraction
 *
 * The butler holds a borrowed IAIService vtable; every impl (AIService,
 * FallbackAIService, AIApiClient) exposes a `ca_ai_service_t *` view whose
 * `vt` field routes the calls. External/native/cloud dependencies (HTTP,
 * RAM probe, generator factory) are injected function-pointer seams with a
 * deterministic in-memory default so the whole thing runs and tests headless.
 *
 * Conventions: ca_ prefix, _t types, opaque handles create/destroy, strdup
 * owning fields with matching *_free, returned strings/arrays are deep copies
 * the caller frees, errors via NULL. Times are Unix ms UTC. No pthreads.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#include "inference.h"          /* ca_generation_options_t */
#include "inference_rt.h"       /* ca_local_chat_generator_t, ca_chat_msg_t */
#include "feedback_analyser.h"  /* ca_feedback_signal_rec_t, ca_feedback_polarity_t */

#ifdef __cplusplus
extern "C" {
#endif

/* The butler's SubmitFeedbackAsync consumes the CircleAI.Memory FeedbackSignal
 * already ported as ca_feedback_signal_rec_t (feedback_analyser.h). Polarity
 * uses ca_feedback_polarity_t (Positive=1, Negative=-1, Correction/Neutral=0). */

/* ===========================================================================
 * BrownoutReason (IAIObserver.cs)
 * =========================================================================== */

typedef enum {
    CA_BROWNOUT_MEMORY_PRESSURE  = 0,
    CA_BROWNOUT_BATTERY_FLOOR    = 1,
    CA_BROWNOUT_THERMAL_CRITICAL = 2,
    CA_BROWNOUT_MANUAL           = 3
} ca_brownout_reason_t;

const char *ca_brownout_reason_name(ca_brownout_reason_t r);

/* ===========================================================================
 * IAIObserver — event records + vtable
 * =========================================================================== */

/* AIChatEvent / AIStreamEvent / AIToolEvent carry borrowed pointers valid only
 * for the duration of the observer callback. */
typedef struct {
    const char *correlation_id;             /* 32-hex, borrowed */
    const ca_chat_msg_t *messages;          /* borrowed */
    size_t      message_count;
    const char *response;                   /* borrowed */
    double      elapsed_ms;
    int64_t     timestamp_ms;
} ca_ai_chat_event_t;

typedef struct {
    const char *correlation_id;
    const ca_chat_msg_t *messages;
    size_t      message_count;
    double      elapsed_ms;
    int         token_count;
    int64_t     timestamp_ms;
} ca_ai_stream_event_t;

typedef struct {
    const char *correlation_id;
    const char *tool_name;                  /* borrowed */
    const char *arguments_json;             /* borrowed, or NULL */
    bool        success;
    const char *result_json;                /* borrowed, or NULL */
    const char *error_message;              /* borrowed, or NULL */
    double      elapsed_ms;
    int64_t     timestamp_ms;
} ca_ai_tool_event_t;

/* Any callback may be NULL. */
typedef struct {
    void (*on_started)(void *user);
    void (*on_stopped)(void *user);
    void (*on_chat_completed)(void *user, const ca_ai_chat_event_t *ev);
    void (*on_stream_started)(void *user, const ca_ai_stream_event_t *ev);
    void (*on_stream_completed)(void *user, const ca_ai_stream_event_t *ev);
    void (*on_tool_invoked)(void *user, const ca_ai_tool_event_t *ev);
    void (*on_model_fetching)(void *user, const char *model_id, bool auto_selected);
    void (*on_brownout)(void *user, const char *from, const char *to,
                        ca_brownout_reason_t reason);
    void *user;
} ca_ai_observer_v2_t;

/* ===========================================================================
 * IPushNotificationSender + PushAIObserver
 * =========================================================================== */

/* Send seam: returns true on success. */
typedef bool (*ca_push_send_fn)(void *user, const char *device_token,
                                const char *title, const char *body);

typedef struct ca_push_observer ca_push_observer_t;

/* device_token must be non-blank. Returns NULL on invalid args / OOM. */
ca_push_observer_t *ca_push_observer_create(ca_push_send_fn send, void *send_user,
                                            const char *device_token);
void ca_push_observer_destroy(ca_push_observer_t *o);
/* A ca_ai_observer_v2_t view (chat-completed -> push; body truncated to 100 +
 * ellipsis). Borrowed; valid while the observer lives. */
ca_ai_observer_v2_t ca_push_observer_as_observer(ca_push_observer_t *o);
/* PushAIObserver.OnError — sends a "B! Error" push (body truncated to 100). */
void ca_push_observer_on_error(ca_push_observer_t *o, const char *message);

/* ===========================================================================
 * ICircleAetherTransport + AetherAIObserver
 * =========================================================================== */

/* Publish seam: returns true on success. payload is borrowed for the call. */
typedef bool (*ca_aether_publish_fn)(void *user, const char *topic,
                                     const uint8_t *payload, size_t payload_len);

typedef struct ca_aether_observer ca_aether_observer_t;

ca_aether_observer_t *ca_aether_observer_create(ca_aether_publish_fn publish, void *publish_user);
void ca_aether_observer_destroy(ca_aether_observer_t *o);
/* Observer view: chat-completed publishes {"response":...} JSON to
 * "butler/response". */
ca_ai_observer_v2_t ca_aether_observer_as_observer(ca_aether_observer_t *o);
/* Publishes {"error":name,"message":msg} to "butler/error". */
void ca_aether_observer_on_error(ca_aether_observer_t *o, const char *error_name,
                                 const char *message);

/* ===========================================================================
 * IMemoryPressureSource
 * =========================================================================== */

typedef enum {
    CA_MEM_PRESSURE_NORMAL   = 0,
    CA_MEM_PRESSURE_TRIM     = 1,
    CA_MEM_PRESSURE_CRITICAL = 2
} ca_memory_pressure_level_t;

/* handler(user, old_level, new_level). */
typedef void (*ca_memory_pressure_handler_fn)(void *user,
                                              ca_memory_pressure_level_t old_level,
                                              ca_memory_pressure_level_t new_level);

typedef struct ca_memory_pressure_source ca_memory_pressure_source_t;

/* NullMemoryPressureSource — always Normal, never fires. */
ca_memory_pressure_source_t *ca_null_memory_pressure_source(void);
/* ManualMemoryPressureSource — host/tests call raise(). */
ca_memory_pressure_source_t *ca_manual_memory_pressure_source_create(void);
void ca_memory_pressure_source_destroy(ca_memory_pressure_source_t *s);

ca_memory_pressure_level_t ca_memory_pressure_current(const ca_memory_pressure_source_t *s);
/* Subscribe; returns a token (>0) used to unsubscribe, or 0 on failure/Null
 * source. */
int  ca_memory_pressure_subscribe(ca_memory_pressure_source_t *s,
                                  ca_memory_pressure_handler_fn handler, void *user);
void ca_memory_pressure_unsubscribe(ca_memory_pressure_source_t *s, int token);
/* Raise a new level. Only transitions fire handlers (idempotent for same
 * level). No-op on the Null source. */
void ca_memory_pressure_raise(ca_memory_pressure_source_t *s,
                             ca_memory_pressure_level_t level);

/* ===========================================================================
 * IToolBridge seam (CircleAI.Tools.IToolBridge — the piece AIService needs)
 * =========================================================================== */

/* Invoke seam: fill *out_result_json / *out_error (malloc'd, caller/service
 * frees) and return success. When success, out_error should be NULL; on
 * failure out_result_json should be NULL. */
typedef struct {
    bool  (*invoke)(void *user, const char *tool_name, const char *arguments_json,
                    char **out_result_json, char **out_error);
    void *user;
} ca_tool_bridge_t;

/* ===========================================================================
 * AIOptions
 * =========================================================================== */

typedef struct {
    /* Identity / prompt. */
    char *model_id;               /* owned, or NULL */
    char *system_prompt;          /* owned; default "You are B!, ..." */
    char *persona_user_id;        /* owned; default "default" */

    /* Generator. */
    int   context_size;           /* <=0 => 4096 default */
    int   thread_count;           /* informational */
    bool  warm_on_start;

    /* Agentic. */
    int   agentic_max_iterations; /* <=0 => 1 */

    /* Default generation options applied when a call passes none. */
    ca_generation_options_t default_generation_options;

    /* Seams (all borrowed; may be NULL). */
    ca_ai_observer_v2_t     *observer;
    ca_tool_bridge_t        *tool_bridge;
    ca_memory_pressure_source_t *pressure_source;
} ca_ai_options_t2;

/* Fills opts with defaults (system_prompt/persona_user_id malloc'd). Returns
 * false on OOM. Free with ca_ai_options_free. */
bool ca_ai_options_init(ca_ai_options_t2 *opts);
void ca_ai_options_free(ca_ai_options_t2 *opts);

/* ===========================================================================
 * IAIService — the vtable seam
 * ===========================================================================
 *
 * Every impl exposes a ca_ai_service_t whose `vt` routes the call and whose
 * `self` is the concrete handle. Async is collapsed to synchronous returns.
 * String returns are freshly-allocated (caller frees). Streaming is a callback.
 */

typedef struct ca_ai_service ca_ai_service_t;

/* Streaming piece callback: text is borrowed for the call. Return false to
 * request early stop (mirrors cancellation). */
typedef bool (*ca_ai_stream_piece_fn)(void *user, const char *piece);

typedef struct {
    bool   (*is_ready)(void *self);
    bool   (*start)(void *self);
    bool   (*stop)(void *self);
    /* AskAsync / ChatAsync / AgenticChatAsync — return malloc'd text or NULL. */
    char  *(*ask)(void *self, const char *question);
    char  *(*chat)(void *self, const ca_chat_msg_t *messages, size_t count,
                   const ca_generation_options_t *opts);
    char  *(*agentic_chat)(void *self, const char *prompt,
                           const ca_generation_options_t *opts);
    /* StreamAsync — drive on_piece per chunk; returns total pieces (or -1). */
    long   (*stream)(void *self, const ca_chat_msg_t *messages, size_t count,
                     const ca_generation_options_t *opts,
                     ca_ai_stream_piece_fn on_piece, void *piece_user);
    /* InvokeToolAsync — fill *out (result_json / error malloc'd); return
     * success. */
    bool   (*invoke_tool)(void *self, const char *tool_name,
                          const char *arguments_json,
                          char **out_result_json, char **out_error);
    /* SubmitFeedbackAsync. */
    void   (*submit_feedback)(void *self, const ca_feedback_signal_rec_t *signal);
    /* PrewarmAsync. */
    void   (*prewarm)(void *self);
} ca_ai_service_vtable_t;

struct ca_ai_service {
    const ca_ai_service_vtable_t *vt;
    void                         *self;
};

/* Thin dispatchers over the vtable (used by ScheduledAIService, endpoints,
 * warmup, ProactiveReasoningService, FallbackAIService). */
bool  ca_ai_service_is_ready(ca_ai_service_t *svc);
bool  ca_ai_service_start(ca_ai_service_t *svc);
bool  ca_ai_service_stop(ca_ai_service_t *svc);
char *ca_ai_service_ask(ca_ai_service_t *svc, const char *question);
char *ca_ai_service_chat(ca_ai_service_t *svc, const ca_chat_msg_t *messages,
                         size_t count, const ca_generation_options_t *opts);
char *ca_ai_service_agentic_chat(ca_ai_service_t *svc, const char *prompt,
                                 const ca_generation_options_t *opts);
long  ca_ai_service_stream(ca_ai_service_t *svc, const ca_chat_msg_t *messages,
                           size_t count, const ca_generation_options_t *opts,
                           ca_ai_stream_piece_fn on_piece, void *piece_user);
bool  ca_ai_service_invoke_tool(ca_ai_service_t *svc, const char *tool_name,
                                const char *arguments_json,
                                char **out_result_json, char **out_error);
void  ca_ai_service_submit_feedback(ca_ai_service_t *svc, const ca_feedback_signal_rec_t *signal);
void  ca_ai_service_prewarm(ca_ai_service_t *svc);

/* ===========================================================================
 * ParseToolCall (AIService.ParseToolCall) — exposed for tests
 * ===========================================================================
 *
 * Extract a Qwen3 <tool_call>{json}</tool_call> block. On success writes
 * *out_tool_name (malloc'd) and *out_arguments_json (malloc'd; the raw
 * "arguments" object text, or "{}" when absent) and returns true. Returns
 * false (and leaves outs untouched) when no valid tool call is present.
 */
bool ca_ai_parse_tool_call(const char *response, char **out_tool_name,
                           char **out_arguments_json);

/* ===========================================================================
 * AIService — deterministic default impl
 * ===========================================================================
 *
 * Wraps a ca_local_chat_generator_t. Prepends the enriched system prompt (the
 * configured system_prompt; a caller-supplied system message is honoured
 * as-is). Runs the agentic <tool_call> loop against the injected tool bridge.
 * Fires the observer. Subscribes to the pressure source and brownout-swaps to
 * a fallback model on Critical.
 *
 * The options are borrowed (the caller owns them for the service lifetime).
 * The generator is created internally from options.model_id / context_size,
 * OR the caller can inject one via ca_ai_service_impl_create_with_generator
 * (the service then owns it).
 *
 * Brownout: when a fallback model id is set, a Critical pressure event swaps
 * the resolved model id from primary->fallback and fires on_brownout.
 */

typedef struct ca_ai_service_impl ca_ai_service_impl_t;

ca_ai_service_impl_t *ca_ai_service_impl_create(ca_ai_options_t2 *options);
/* Inject a generator (service takes ownership and destroys it). */
ca_ai_service_impl_t *ca_ai_service_impl_create_with_generator(
    ca_ai_options_t2 *options, ca_local_chat_generator_t *generator);
void ca_ai_service_impl_destroy(ca_ai_service_impl_t *s);

/* The IAIService view (borrowed; valid while the impl lives). */
ca_ai_service_t *ca_ai_service_impl_as_service(ca_ai_service_impl_t *s);

/* Configure the brownout fallback model id (BrownoutAsync target). Deep-copied.
 */
void ca_ai_service_impl_set_fallback_model(ca_ai_service_impl_t *s,
                                          const char *fallback_model_id);
/* The currently-resolved model id (borrowed), or NULL. */
const char *ca_ai_service_impl_resolved_model(const ca_ai_service_impl_t *s);
/* BrownoutAsync(reason): swap to the fallback model; returns true when a swap
 * happened. No-op (false) when not started, no fallback set, or already there.
 */
bool ca_ai_service_impl_brownout(ca_ai_service_impl_t *s, ca_brownout_reason_t reason);
/* Persona feedback tallies (exposed for tests; mirror PersonaState counters). */
int ca_ai_service_impl_positive_signals(const ca_ai_service_impl_t *s);
int ca_ai_service_impl_negative_signals(const ca_ai_service_impl_t *s);
int ca_ai_service_impl_total_interactions(const ca_ai_service_impl_t *s);

/* ===========================================================================
 * AIApiClient — remote ButlerAPI proxy over an injected transport seam
 * ===========================================================================
 *
 * The transport seam performs a request against a route + JSON body and writes
 * the response body. It also exposes an SSE stream seam. There are no sockets;
 * a deterministic in-memory transport (ca_http_loopback_transport) routes to a
 * bound IAIService so the client<->endpoint round-trip is fully testable.
 *
 * Routes (paths) mirror the C#: api/butler/{health,ask,chat,stream,agentic,tool,
 * feedback}.
 */

/* HTTP transport seam. */
typedef struct {
    /* GET/POST returning a body. method is "GET"/"POST". body_json may be NULL.
     * On success writes *out_body (malloc'd) and returns true; on transport /
     * status failure returns false. */
    bool (*request)(void *user, const char *method, const char *path,
                    const char *body_json, char **out_body);
    /* SSE POST: parse "data: <token>" lines from the server, calling on_piece
     * per token until "[DONE]"; returns pieces yielded, or -1 on failure. */
    long (*stream)(void *user, const char *path, const char *body_json,
                   ca_ai_stream_piece_fn on_piece, void *piece_user);
    void *user;
} ca_http_transport_t;

typedef struct ca_ai_api_client ca_ai_api_client_t;

/* transport is borrowed. Returns NULL on NULL transport. */
ca_ai_api_client_t *ca_ai_api_client_create(const ca_http_transport_t *transport);
void ca_ai_api_client_destroy(ca_ai_api_client_t *c);
ca_ai_service_t *ca_ai_api_client_as_service(ca_ai_api_client_t *c);

/* ===========================================================================
 * IAIEndpoint + InProcessEndpoint + HttpLoopbackEndpoint
 * =========================================================================== */

typedef struct ca_ai_endpoint ca_ai_endpoint_t;

/* --- InProcessEndpoint: just holds the bound service. --- */
ca_ai_endpoint_t *ca_inprocess_endpoint_create(void);
/* Start binds the service; idempotent. Returns false if disposed / NULL svc. */
bool ca_ai_endpoint_start(ca_ai_endpoint_t *e, ca_ai_service_t *service);
bool ca_ai_endpoint_stop(ca_ai_endpoint_t *e);
void ca_ai_endpoint_destroy(ca_ai_endpoint_t *e);
/* InProcess accessor (borrowed), or NULL when unbound. */
ca_ai_service_t *ca_inprocess_endpoint_service(ca_ai_endpoint_t *e);

/* --- HttpLoopbackEndpoint: token-guarded route dispatch. ---
 *
 * There is no OS socket. The endpoint exposes a `dispatch` entry that a
 * transport (e.g. ca_http_loopback_transport) calls with (token, method,
 * path, body). The token check (constant-time), method (POST), and route
 * table (/butler/{ask,chat,stream,tool}) are real; the bodies are the same
 * JSON shapes as the C#. */
ca_ai_endpoint_t *ca_http_loopback_endpoint_create(const char *token /*may be NULL -> random*/,
                                                   int bound_port /*<=0 -> 0*/);
/* Effective token (borrowed) after start, or NULL. */
const char *ca_http_loopback_endpoint_token(const ca_ai_endpoint_t *e);
int         ca_http_loopback_endpoint_port(const ca_ai_endpoint_t *e);

/* Non-streaming dispatch. Writes *out_body (malloc'd) + *out_status and returns
 * true when a response was produced (even a 4xx/5xx JSON/plain body). Returns
 * false only on NULL args. */
bool ca_http_loopback_endpoint_dispatch(ca_ai_endpoint_t *e,
                                        const char *token, const char *method,
                                        const char *path, const char *body_json,
                                        int *out_status, char **out_body);
/* Streaming dispatch for /butler/stream: token-checked, drives on_piece per
 * streamed token. Returns pieces (>=0) on success, or -1 on auth/route/arg
 * failure (writes *out_status). */
long ca_http_loopback_endpoint_dispatch_stream(ca_ai_endpoint_t *e,
                                              const char *token, const char *method,
                                              const char *path, const char *body_json,
                                              ca_ai_stream_piece_fn on_piece,
                                              void *piece_user, int *out_status);

/* Build an HTTP transport backed by a loopback endpoint (client<->endpoint
 * round-trip with no sockets). The transport sends the endpoint's token
 * automatically. Fill *out (borrowed function pointers + user = internal
 * adapter owned by the endpoint). Returns false on NULL args. */
bool ca_http_loopback_transport(ca_ai_endpoint_t *endpoint, ca_http_transport_t *out);

/* ===========================================================================
 * FallbackAIService — local-first, cloud-fallback
 * ===========================================================================
 *
 * Reads available RAM via an injected probe; when >= threshold it starts local
 * and (on local-start failure) falls back to cloud; below threshold it uses
 * cloud directly. After Start, all calls delegate to the active backend.
 */

typedef int64_t (*ca_ram_probe_fn)(void *user); /* available RAM bytes */

typedef struct ca_fallback_ai_service ca_fallback_ai_service_t;

/* local + cloud are borrowed IAIService views. ram_threshold_bytes default
 * (pass <=0) is 2 GiB. ram_probe may be NULL (treated as "0 bytes available" ->
 * always cloud). */
ca_fallback_ai_service_t *ca_fallback_ai_service_create(
    ca_ai_service_t *local, ca_ai_service_t *cloud,
    int64_t ram_threshold_bytes, ca_ram_probe_fn ram_probe, void *ram_probe_user);
void ca_fallback_ai_service_destroy(ca_fallback_ai_service_t *f);
ca_ai_service_t *ca_fallback_ai_service_as_service(ca_fallback_ai_service_t *f);
/* True when the last Start selected the cloud backend. */
bool ca_fallback_ai_service_using_cloud(const ca_fallback_ai_service_t *f);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_HOST_AI_H */
