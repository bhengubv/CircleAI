#ifndef CIRCLE_AI_HOST_BRIDGE_H
#define CIRCLE_AI_HOST_BRIDGE_H

/*
 * host_bridge.h — CircleAI.Hosting.InferenceBridge (C11 port).
 *
 * Ports (from src/CircleAI.Hosting.InferenceBridge):
 *   InferenceStatus / ModelFormat
 *   InferenceRequest (+ Create) / InferenceResponse
 *   ModelDescriptor / DeviceCapabilities
 *   InferenceFragmentKind / InferenceFragment
 *   IInferenceBridge (vtable seam)
 *   LocalProcessInferenceBridge — wraps a local IChatGenerator; computes status
 *                                 (StoppedByToken / StoppedByLength / Completed),
 *                                 token estimates (len/4), and reasoning split;
 *                                 device capabilities via an injected probe
 *   MockInferenceBridge         — deterministic canned bridge
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup owning fields with
 * matching *_free, deep-copied returned arrays the caller frees.
 */

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#include "inference_rt.h"   /* ca_local_chat_generator_t, ca_chat_msg_t */
#include "inference.h"      /* ca_generation_options_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Enums
 * =========================================================================== */

typedef enum {
    CA_HB_COMPLETED         = 0,
    CA_HB_STOPPED_BY_TOKEN  = 1,
    CA_HB_STOPPED_BY_LENGTH = 2,
    CA_HB_FAILED            = 3,
    CA_HB_CANCELLED         = 4
} ca_hb_status_t;

const char *ca_hb_status_name(ca_hb_status_t s); /* lower-case */

typedef enum {
    CA_MODEL_FORMAT_GGUF    = 0,
    CA_MODEL_FORMAT_ONNX    = 1,
    CA_MODEL_FORMAT_COREML  = 2,
    CA_MODEL_FORMAT_TFLITE  = 3,
    CA_MODEL_FORMAT_UNKNOWN = 4
} ca_model_format_t;

typedef enum {
    CA_HB_FRAGMENT_CONTENT   = 0,
    CA_HB_FRAGMENT_REASONING = 1
} ca_hb_fragment_kind_t;

/* ===========================================================================
 * ModelDescriptor
 * =========================================================================== */

typedef struct {
    char             *model_id;             /* owned */
    char             *version;              /* owned */
    ca_model_format_t format;
    int               context_window_tokens;
    int               vocab_size;
    int64_t           parameter_count;
    char             *quantisation_label;   /* owned, or NULL */
    int64_t           approximate_memory_bytes;
} ca_bridge_model_descriptor_t;

void ca_bridge_model_descriptor_free(ca_bridge_model_descriptor_t *d);
void ca_bridge_model_descriptor_free_array(ca_bridge_model_descriptor_t *arr, size_t count);
ca_bridge_model_descriptor_t *ca_bridge_model_descriptor_copy(
    ca_bridge_model_descriptor_t *dst, const ca_bridge_model_descriptor_t *src);

/* ===========================================================================
 * DeviceCapabilities
 * =========================================================================== */

typedef struct {
    char   *os_name;              /* owned */
    char   *os_version;           /* owned */
    int64_t physical_memory_bytes;
    int     cpu_core_count;
    bool    has_gpu;
    char   *gpu_name;             /* owned, or NULL */
    bool    has_gpu_memory;
    int64_t gpu_memory_bytes;
    bool    has_npu;
    char   *npu_name;             /* owned, or NULL */
    bool    has_transport_layer_encryption;
} ca_device_capabilities_t;

void ca_device_capabilities_free(ca_device_capabilities_t *d);

/* ===========================================================================
 * InferenceRequest / InferenceResponse
 * =========================================================================== */

typedef struct {
    char    *id;                  /* owned; 32-hex */
    char    *model_id;            /* owned */
    char    *prompt;              /* owned */
    int      max_output_tokens;
    float    temperature;
    float    top_p;
    char   **stop_sequences;      /* owned array; may be NULL */
    size_t   stop_sequence_count;
    int64_t  requested_at_ms;
} ca_hb_request_t;

void ca_hb_request_free(ca_hb_request_t *r);
/* Create — stamps a fresh id + requested-at, defaults topP=0.95, temp=0.7,
 * max=256 (mirrors InferenceRequest.Create). Fills *out. Returns false on
 * blank model_id / NULL prompt / NULL out. */
bool ca_hb_request_create(const char *model_id, const char *prompt,
                                 int max_output_tokens, float temperature, float top_p,
                                 ca_hb_request_t *out);

typedef struct {
    char                 *request_id;        /* owned */
    char                 *model_id;          /* owned */
    char                 *output_text;       /* owned */
    int                   output_token_count;
    int                   prompt_token_count;
    ca_hb_status_t status;
    double                inference_millis;
    char                 *failure_message;   /* owned, or NULL */
    int64_t               completed_at_ms;
    char                 *reasoning_text;    /* owned, or NULL */
} ca_hb_response_t;

void ca_hb_response_free(ca_hb_response_t *r);

/* One streamed fragment. text borrowed for the callback. */
typedef struct {
    ca_hb_fragment_kind_t kind;
    const char                  *text;
} ca_hb_fragment_t;

/* ===========================================================================
 * IInferenceBridge — vtable seam
 * =========================================================================== */

typedef bool (*ca_bridge_stream_fn)(void *user, const char *chunk);
typedef bool (*ca_bridge_fragment_fn)(void *user, const ca_hb_fragment_t *fragment);

typedef struct ca_hb_bridge ca_hb_bridge_t;

typedef struct {
    /* ListLoadedModelsAsync — fresh descriptor array (caller frees). */
    ca_bridge_model_descriptor_t *(*list_loaded_models)(void *self, size_t *out_count);
    bool (*is_model_loaded)(void *self, const char *model_id);
    /* CompleteAsync — fill *out. Returns true when produced (even a Failed
     * response). */
    bool (*complete)(void *self, const ca_hb_request_t *req, ca_hb_response_t *out);
    /* StreamCompletionAsync — drive on_chunk; return chunks (>=0) or -1. */
    long (*stream_completion)(void *self, const ca_hb_request_t *req,
                              ca_bridge_stream_fn on_chunk, void *chunk_user);
    /* StreamFragmentsAsync — drive on_fragment; return fragments (>=0) or -1. */
    long (*stream_fragments)(void *self, const ca_hb_request_t *req,
                             ca_bridge_fragment_fn on_fragment, void *frag_user);
    /* GetDeviceCapabilitiesAsync — fill *out. Returns true. */
    bool (*get_device_capabilities)(void *self, ca_device_capabilities_t *out);
    void (*destroy)(void *self);
} ca_hb_bridge_vtable_t;

struct ca_hb_bridge {
    const ca_hb_bridge_vtable_t *vt;
    void                               *self;
};

/* Dispatchers. */
ca_bridge_model_descriptor_t *ca_hb_bridge_list_loaded_models(ca_hb_bridge_t *b, size_t *out_count);
bool ca_hb_bridge_is_model_loaded(ca_hb_bridge_t *b, const char *model_id);
bool ca_hb_bridge_complete(ca_hb_bridge_t *b, const ca_hb_request_t *req,
                                  ca_hb_response_t *out);
long ca_hb_bridge_stream_completion(ca_hb_bridge_t *b, const ca_hb_request_t *req,
                                           ca_bridge_stream_fn on_chunk, void *chunk_user);
long ca_hb_bridge_stream_fragments(ca_hb_bridge_t *b, const ca_hb_request_t *req,
                                          ca_bridge_fragment_fn on_fragment, void *frag_user);
bool ca_hb_bridge_get_device_capabilities(ca_hb_bridge_t *b, ca_device_capabilities_t *out);

/* ===========================================================================
 * LocalProcessInferenceBridge
 * =========================================================================== */

/* Capability probe seam — fills a DeviceCapabilities. Default (NULL) reports a
 * deterministic "portable host" profile. */
typedef void (*ca_capability_probe_fn)(void *user, ca_device_capabilities_t *out);

typedef struct ca_local_process_bridge ca_local_process_bridge_t;

/* Wrap a generator for the model described by `descriptor` (deep-copied). The
 * generator is borrowed (caller owns it). capability_probe may be NULL. */
ca_local_process_bridge_t *ca_local_process_bridge_create(
    ca_local_chat_generator_t *generator, const ca_bridge_model_descriptor_t *descriptor,
    ca_capability_probe_fn capability_probe, void *probe_user);
void ca_local_process_bridge_destroy(ca_local_process_bridge_t *b);
ca_hb_bridge_t *ca_local_process_bridge_as_bridge(ca_local_process_bridge_t *b);

/* ===========================================================================
 * MockInferenceBridge
 * =========================================================================== */

typedef struct ca_mock_bridge ca_mock_bridge_t;

/* canned_output required; model_id NULL => "mock-model". */
ca_mock_bridge_t *ca_mock_bridge_create(const char *canned_output, const char *model_id);
void ca_mock_bridge_destroy(ca_mock_bridge_t *b);
ca_hb_bridge_t *ca_mock_bridge_as_bridge(ca_mock_bridge_t *b);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_HOST_BRIDGE_H */
