/*
 * host_bridge.c — CircleAI.Hosting.InferenceBridge (C11 port). See host_bridge.h.
 *
 * LocalProcessInferenceBridge wraps a local IChatGenerator; CompleteAsync's
 * status classification (StoppedByToken/StoppedByLength/Completed), token-count
 * estimate (len/4), and reasoning split mirror the C#. MockInferenceBridge is
 * the deterministic canned bridge. Device capabilities come from an injected
 * probe with a deterministic default.
 *
 * Pure C11 + libc.
 */

#include "circle_ai/host_bridge.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

/* ── helpers ──────────────────────────────────────────────────────────── */

static char *b_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static void make_id(uint64_t counter, char out[33]) {
    uint64_t x = counter * 0x9E3779B97F4A7C15ull + 0x1234567890ABCDEFull;
    uint64_t y = (counter ^ 0xD1B54A32D192ED03ull) * 0xBF58476D1CE4E5B9ull;
    snprintf(out, 33, "%08x%08x%08x%08x",
             (unsigned)(x >> 32), (unsigned)(x & 0xFFFFFFFFu),
             (unsigned)(y >> 32), (unsigned)(y & 0xFFFFFFFFu));
}
static uint64_t g_id_counter = 1;
static int64_t g_clock_ms = 2000;
static int64_t bridge_now(void) { return (g_clock_ms += 3); }

/* EstimateTokenCount: max(1, len/4) for non-empty; 0 for empty. */
static int estimate_tokens(const char *text) {
    if (!text || !text[0]) return 0;
    int n = (int)(strlen(text) / 4);
    return n < 1 ? 1 : n;
}

const char *ca_hb_status_name(ca_hb_status_t s) {
    switch (s) {
        case CA_HB_COMPLETED:         return "completed";
        case CA_HB_STOPPED_BY_TOKEN:  return "stoppedbytoken";
        case CA_HB_STOPPED_BY_LENGTH: return "stoppedbylength";
        case CA_HB_FAILED:            return "failed";
        case CA_HB_CANCELLED:         return "cancelled";
    }
    return "unknown";
}

/* ===========================================================================
 * ModelDescriptor
 * =========================================================================== */

void ca_bridge_model_descriptor_free(ca_bridge_model_descriptor_t *d) {
    if (!d) return;
    free(d->model_id); free(d->version); free(d->quantisation_label);
    d->model_id = d->version = d->quantisation_label = NULL;
}
void ca_bridge_model_descriptor_free_array(ca_bridge_model_descriptor_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_bridge_model_descriptor_free(&arr[i]);
    free(arr);
}
ca_bridge_model_descriptor_t *ca_bridge_model_descriptor_copy(
    ca_bridge_model_descriptor_t *dst, const ca_bridge_model_descriptor_t *src) {
    if (!dst || !src) return dst;
    dst->model_id              = b_strdup(src->model_id);
    dst->version               = b_strdup(src->version);
    dst->format                = src->format;
    dst->context_window_tokens = src->context_window_tokens;
    dst->vocab_size            = src->vocab_size;
    dst->parameter_count       = src->parameter_count;
    dst->quantisation_label    = b_strdup(src->quantisation_label);
    dst->approximate_memory_bytes = src->approximate_memory_bytes;
    return dst;
}

/* ===========================================================================
 * DeviceCapabilities
 * =========================================================================== */

void ca_device_capabilities_free(ca_device_capabilities_t *d) {
    if (!d) return;
    free(d->os_name); free(d->os_version); free(d->gpu_name); free(d->npu_name);
    d->os_name = d->os_version = d->gpu_name = d->npu_name = NULL;
}

/* ===========================================================================
 * InferenceRequest
 * =========================================================================== */

void ca_hb_request_free(ca_hb_request_t *r) {
    if (!r) return;
    free(r->id); free(r->model_id); free(r->prompt);
    for (size_t i = 0; i < r->stop_sequence_count; ++i) free(r->stop_sequences[i]);
    free(r->stop_sequences);
    r->id = r->model_id = r->prompt = NULL; r->stop_sequences = NULL; r->stop_sequence_count = 0;
}
bool ca_hb_request_create(const char *model_id, const char *prompt,
                                 int max_output_tokens, float temperature, float top_p,
                                 ca_hb_request_t *out) {
    if (!model_id || !model_id[0] || !prompt || !out) return false;
    memset(out, 0, sizeof(*out));
    char id[33]; make_id(g_id_counter++, id);
    out->id = b_strdup(id);
    out->model_id = b_strdup(model_id);
    out->prompt = b_strdup(prompt);
    out->max_output_tokens = max_output_tokens;
    out->temperature = temperature;
    out->top_p = top_p;
    out->stop_sequences = NULL; out->stop_sequence_count = 0;
    out->requested_at_ms = bridge_now();
    return true;
}

/* ===========================================================================
 * InferenceResponse
 * =========================================================================== */

void ca_hb_response_free(ca_hb_response_t *r) {
    if (!r) return;
    free(r->request_id); free(r->model_id); free(r->output_text);
    free(r->failure_message); free(r->reasoning_text);
    memset(r, 0, sizeof(*r));
}

/* ===========================================================================
 * IInferenceBridge dispatchers
 * =========================================================================== */

ca_bridge_model_descriptor_t *ca_hb_bridge_list_loaded_models(ca_hb_bridge_t *b, size_t *out_count) {
    if (out_count) *out_count = 0;
    return (b && b->vt->list_loaded_models) ? b->vt->list_loaded_models(b->self, out_count) : NULL;
}
bool ca_hb_bridge_is_model_loaded(ca_hb_bridge_t *b, const char *model_id) {
    return (b && b->vt->is_model_loaded) ? b->vt->is_model_loaded(b->self, model_id) : false;
}
bool ca_hb_bridge_complete(ca_hb_bridge_t *b, const ca_hb_request_t *req,
                                  ca_hb_response_t *out) {
    return (b && b->vt->complete) ? b->vt->complete(b->self, req, out) : false;
}
long ca_hb_bridge_stream_completion(ca_hb_bridge_t *b, const ca_hb_request_t *req,
                                           ca_bridge_stream_fn on_chunk, void *chunk_user) {
    return (b && b->vt->stream_completion) ? b->vt->stream_completion(b->self, req, on_chunk, chunk_user) : -1;
}
long ca_hb_bridge_stream_fragments(ca_hb_bridge_t *b, const ca_hb_request_t *req,
                                          ca_bridge_fragment_fn on_fragment, void *frag_user) {
    return (b && b->vt->stream_fragments) ? b->vt->stream_fragments(b->self, req, on_fragment, frag_user) : -1;
}
bool ca_hb_bridge_get_device_capabilities(ca_hb_bridge_t *b, ca_device_capabilities_t *out) {
    return (b && b->vt->get_device_capabilities) ? b->vt->get_device_capabilities(b->self, out) : false;
}

/* ===========================================================================
 * LocalProcessInferenceBridge
 * =========================================================================== */

struct ca_local_process_bridge {
    ca_local_chat_generator_t   *generator;   /* borrowed */
    ca_bridge_model_descriptor_t descriptor;  /* owned */
    ca_capability_probe_fn       probe;
    void                        *probe_user;
    ca_hb_bridge_t        view;
};

static ca_bridge_model_descriptor_t *lpb_list(void *self, size_t *out_count) {
    ca_local_process_bridge_t *b = (ca_local_process_bridge_t *)self;
    ca_bridge_model_descriptor_t *arr = (ca_bridge_model_descriptor_t *)calloc(1, sizeof(*arr));
    if (!arr) { if (out_count) *out_count = 0; return NULL; }
    ca_bridge_model_descriptor_copy(&arr[0], &b->descriptor);
    if (out_count) *out_count = 1;
    return arr;
}
static bool lpb_is_loaded(void *self, const char *model_id) {
    ca_local_process_bridge_t *b = (ca_local_process_bridge_t *)self;
    return model_id && b->descriptor.model_id && strcmp(b->descriptor.model_id, model_id) == 0;
}

/* DetermineStatus (LocalProcessInferenceBridge.DetermineStatus). */
static ca_hb_status_t determine_status(const char *output, const ca_hb_request_t *req) {
    for (size_t i = 0; i < req->stop_sequence_count; ++i) {
        const char *s = req->stop_sequences[i];
        if (s && s[0] && output && strstr(output, s)) return CA_HB_STOPPED_BY_TOKEN;
    }
    int produced = estimate_tokens(output);
    return produced >= req->max_output_tokens ? CA_HB_STOPPED_BY_LENGTH : CA_HB_COMPLETED;
}

static bool lpb_complete(void *self, const ca_hb_request_t *req, ca_hb_response_t *out) {
    ca_local_process_bridge_t *b = (ca_local_process_bridge_t *)self;
    if (!req || !out) return false;
    memset(out, 0, sizeof(*out));

    if (!b->descriptor.model_id || strcmp(b->descriptor.model_id, req->model_id) != 0) {
        out->request_id = b_strdup(req->id);
        out->model_id = b_strdup(req->model_id);
        out->output_text = b_strdup("");
        out->status = CA_HB_FAILED;
        size_t n = strlen(req->model_id) + strlen(b->descriptor.model_id ? b->descriptor.model_id : "") + 80;
        char *msg = (char *)malloc(n);
        if (msg) snprintf(msg, n, "Model '%s' is not loaded by this bridge (have '%s').",
                          req->model_id, b->descriptor.model_id ? b->descriptor.model_id : "");
        out->failure_message = msg;
        out->completed_at_ms = bridge_now();
        return true;
    }

    ca_chat_msg_t msg = { "user", req->prompt, NULL, 0 };
    ca_generation_options_t opts; ca_generation_options_init(&opts);
    opts.max_tokens = req->max_output_tokens;
    opts.temperature = req->temperature;
    opts.top_p = req->top_p;

    int64_t t0 = bridge_now();
    ca_chat_gen_response_t r;
    bool ok = ca_local_chat_generator_generate_response(b->generator, &msg, 1, &opts, &r);
    int64_t t1 = bridge_now();

    if (!ok) {
        out->request_id = b_strdup(req->id);
        out->model_id = b_strdup(req->model_id);
        out->output_text = b_strdup("");
        out->prompt_token_count = estimate_tokens(req->prompt);
        out->status = CA_HB_FAILED;
        out->inference_millis = (double)(t1 - t0);
        out->failure_message = b_strdup("generation failed");
        out->completed_at_ms = bridge_now();
        return true;
    }

    const char *output = r.text ? r.text : "";
    ca_hb_status_t status = determine_status(output, req);

    out->request_id = b_strdup(req->id);
    out->model_id = b_strdup(req->model_id);
    out->output_text = b_strdup(output);
    out->output_token_count = estimate_tokens(output);
    out->prompt_token_count = estimate_tokens(req->prompt);
    out->status = status;
    out->inference_millis = (double)(t1 - t0);
    out->failure_message = NULL;
    out->completed_at_ms = bridge_now();
    out->reasoning_text = r.reasoning_content ? b_strdup(r.reasoning_content) : NULL;

    ca_chat_gen_response_free(&r);
    return true;
}

/* Streaming helper state to bridge the generator's fragment callback. */
typedef struct { ca_bridge_stream_fn cb; void *user; long count; bool any; bool stopped; } lpb_stream_ctx;
static void lpb_frag_to_content(const ca_chat_fragment_t *frag, void *user) {
    lpb_stream_ctx *ctx = (lpb_stream_ctx *)user;
    if (ctx->stopped) return;
    /* content-only stream: skip reasoning fragments */
    if (frag->kind == CA_CHAT_FRAGMENT_REASONING) return;
    ctx->any = true; ctx->count++;
    if (ctx->cb) { if (!ctx->cb(ctx->user, frag->text ? frag->text : "")) ctx->stopped = true; }
}

static long lpb_stream_completion(void *self, const ca_hb_request_t *req,
                                  ca_bridge_stream_fn on_chunk, void *chunk_user) {
    ca_local_process_bridge_t *b = (ca_local_process_bridge_t *)self;
    if (!req) return -1;
    if (!b->descriptor.model_id || strcmp(b->descriptor.model_id, req->model_id) != 0) return 0;

    ca_chat_msg_t msg = { "user", req->prompt, NULL, 0 };
    ca_generation_options_t opts; ca_generation_options_init(&opts);
    opts.max_tokens = req->max_output_tokens;
    opts.temperature = req->temperature;
    opts.top_p = req->top_p;
    opts.include_reasoning = 0; /* content-only stream */

    lpb_stream_ctx ctx = { on_chunk, chunk_user, 0, false, false };
    ca_local_chat_generator_stream_fragments(b->generator, &msg, 1, &opts, lpb_frag_to_content, &ctx);
    if (!ctx.any) {
        /* fallback: full completion in one chunk */
        char *full = ca_local_chat_generator_generate(b->generator, &msg, 1, &opts);
        if (!full) return ctx.count;
        if (full[0] != '\0') { if (on_chunk) on_chunk(chunk_user, full); ctx.count++; }
        free(full);
    }
    return ctx.count;
}

typedef struct { ca_bridge_fragment_fn cb; void *user; long count; bool stopped; } lpb_frag_ctx;
static void lpb_frag_to_fragment(const ca_chat_fragment_t *frag, void *user) {
    lpb_frag_ctx *ctx = (lpb_frag_ctx *)user;
    if (ctx->stopped) return;
    ca_hb_fragment_t f;
    f.kind = frag->kind == CA_CHAT_FRAGMENT_REASONING ? CA_HB_FRAGMENT_REASONING : CA_HB_FRAGMENT_CONTENT;
    f.text = frag->text ? frag->text : "";
    ctx->count++;
    if (ctx->cb) { if (!ctx->cb(ctx->user, &f)) ctx->stopped = true; }
}
static long lpb_stream_fragments(void *self, const ca_hb_request_t *req,
                                 ca_bridge_fragment_fn on_fragment, void *frag_user) {
    ca_local_process_bridge_t *b = (ca_local_process_bridge_t *)self;
    if (!req) return -1;
    if (!b->descriptor.model_id || strcmp(b->descriptor.model_id, req->model_id) != 0) return 0;
    ca_chat_msg_t msg = { "user", req->prompt, NULL, 0 };
    ca_generation_options_t opts; ca_generation_options_init(&opts);
    opts.max_tokens = req->max_output_tokens;
    opts.temperature = req->temperature;
    opts.top_p = req->top_p;
    opts.include_reasoning = 1;
    lpb_frag_ctx ctx = { on_fragment, frag_user, 0, false };
    ca_local_chat_generator_stream_fragments(b->generator, &msg, 1, &opts, lpb_frag_to_fragment, &ctx);
    return ctx.count;
}

static bool lpb_capabilities(void *self, ca_device_capabilities_t *out) {
    ca_local_process_bridge_t *b = (ca_local_process_bridge_t *)self;
    if (!out) return false;
    memset(out, 0, sizeof(*out));
    if (b->probe) { b->probe(b->probe_user, out); return true; }
    /* deterministic portable-host default */
    out->os_name = b_strdup("Portable");
    out->os_version = b_strdup("1.0");
    out->physical_memory_bytes = 8LL * 1024 * 1024 * 1024;
    out->cpu_core_count = 8;
    out->has_gpu = false;
    out->has_npu = false;
    out->has_transport_layer_encryption = true;
    return true;
}

static void lpb_destroy_self(void *self) { (void)self; /* handle owns nothing extra */ }

static const ca_hb_bridge_vtable_t LPB_VT = {
    lpb_list, lpb_is_loaded, lpb_complete, lpb_stream_completion,
    lpb_stream_fragments, lpb_capabilities, lpb_destroy_self,
};

ca_local_process_bridge_t *ca_local_process_bridge_create(
    ca_local_chat_generator_t *generator, const ca_bridge_model_descriptor_t *descriptor,
    ca_capability_probe_fn capability_probe, void *probe_user) {
    if (!generator || !descriptor) return NULL;
    ca_local_process_bridge_t *b = (ca_local_process_bridge_t *)calloc(1, sizeof(*b));
    if (!b) return NULL;
    b->generator = generator;
    ca_bridge_model_descriptor_copy(&b->descriptor, descriptor);
    b->probe = capability_probe; b->probe_user = probe_user;
    b->view.vt = &LPB_VT; b->view.self = b;
    return b;
}
void ca_local_process_bridge_destroy(ca_local_process_bridge_t *b) {
    if (!b) return;
    ca_bridge_model_descriptor_free(&b->descriptor);
    free(b);
}
ca_hb_bridge_t *ca_local_process_bridge_as_bridge(ca_local_process_bridge_t *b) {
    return b ? &b->view : NULL;
}

/* ===========================================================================
 * MockInferenceBridge
 * =========================================================================== */

struct ca_mock_bridge {
    char                        *canned;
    ca_bridge_model_descriptor_t descriptor;
    ca_hb_bridge_t        view;
};

static ca_bridge_model_descriptor_t *mock_list(void *self, size_t *out_count) {
    ca_mock_bridge_t *b = (ca_mock_bridge_t *)self;
    ca_bridge_model_descriptor_t *arr = (ca_bridge_model_descriptor_t *)calloc(1, sizeof(*arr));
    if (!arr) { if (out_count) *out_count = 0; return NULL; }
    ca_bridge_model_descriptor_copy(&arr[0], &b->descriptor);
    if (out_count) *out_count = 1;
    return arr;
}
static bool mock_is_loaded(void *self, const char *model_id) {
    ca_mock_bridge_t *b = (ca_mock_bridge_t *)self;
    return model_id && strcmp(b->descriptor.model_id, model_id) == 0;
}
static bool mock_complete(void *self, const ca_hb_request_t *req, ca_hb_response_t *out) {
    ca_mock_bridge_t *b = (ca_mock_bridge_t *)self;
    if (!req || !out) return false;
    memset(out, 0, sizeof(*out));
    out->request_id = b_strdup(req->id);
    out->model_id = b_strdup(b->descriptor.model_id);
    out->output_text = b_strdup(b->canned);
    out->output_token_count = (int)(strlen(b->canned) / 4);
    out->prompt_token_count = (int)(strlen(req->prompt ? req->prompt : "") / 4);
    out->status = CA_HB_COMPLETED;
    out->inference_millis = 0.0;
    out->completed_at_ms = bridge_now();
    return true;
}
static long mock_stream_completion(void *self, const ca_hb_request_t *req,
                                   ca_bridge_stream_fn on_chunk, void *chunk_user) {
    ca_mock_bridge_t *b = (ca_mock_bridge_t *)self;
    (void)req;
    if (on_chunk) on_chunk(chunk_user, b->canned);
    return 1;
}
static long mock_stream_fragments(void *self, const ca_hb_request_t *req,
                                  ca_bridge_fragment_fn on_fragment, void *frag_user) {
    ca_mock_bridge_t *b = (ca_mock_bridge_t *)self;
    (void)req;
    ca_hb_fragment_t f = { CA_HB_FRAGMENT_CONTENT, b->canned };
    if (on_fragment) on_fragment(frag_user, &f);
    return 1;
}
static bool mock_capabilities(void *self, ca_device_capabilities_t *out) {
    (void)self;
    if (!out) return false;
    memset(out, 0, sizeof(*out));
    out->os_name = b_strdup("Mock");
    out->os_version = b_strdup("1.0");
    out->physical_memory_bytes = 4LL * 1024 * 1024 * 1024;
    out->cpu_core_count = 1;
    out->has_gpu = false;
    out->has_npu = false;
    out->has_transport_layer_encryption = true;
    return true;
}
static void mock_destroy_self(void *self) { (void)self; }

static const ca_hb_bridge_vtable_t MOCK_VT = {
    mock_list, mock_is_loaded, mock_complete, mock_stream_completion,
    mock_stream_fragments, mock_capabilities, mock_destroy_self,
};

ca_mock_bridge_t *ca_mock_bridge_create(const char *canned_output, const char *model_id) {
    if (!canned_output) return NULL;
    ca_mock_bridge_t *b = (ca_mock_bridge_t *)calloc(1, sizeof(*b));
    if (!b) return NULL;
    b->canned = b_strdup(canned_output);
    b->descriptor.model_id = b_strdup(model_id ? model_id : "mock-model");
    b->descriptor.version = b_strdup("mock-1.0.0");
    b->descriptor.format = CA_MODEL_FORMAT_UNKNOWN;
    b->descriptor.context_window_tokens = 4096;
    b->descriptor.vocab_size = 32000;
    b->descriptor.parameter_count = 0;
    b->descriptor.quantisation_label = NULL;
    b->descriptor.approximate_memory_bytes = 0;
    b->view.vt = &MOCK_VT; b->view.self = b;
    return b;
}
void ca_mock_bridge_destroy(ca_mock_bridge_t *b) {
    if (!b) return;
    free(b->canned);
    ca_bridge_model_descriptor_free(&b->descriptor);
    free(b);
}
ca_hb_bridge_t *ca_mock_bridge_as_bridge(ca_mock_bridge_t *b) { return b ? &b->view : NULL; }
