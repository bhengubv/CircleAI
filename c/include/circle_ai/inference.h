#ifndef CIRCLE_AI_INFERENCE_H
#define CIRCLE_AI_INFERENCE_H

/*
 * inference.h — Generation options and IChatGenerator callback interface.
 * Pure C11, no OS-specific headers.
 */

#include <stdint.h>
#include "models_v15.h"  /* ca_chat_fragment_t, ca_chat_fragment_kind_t */

/* ---------------------------------------------------------------------------
 * GenerationOptions
 * --------------------------------------------------------------------------- */

typedef struct {
    const char *model;              /* NULL = provider default      */
    int         max_tokens;         /* 0    = provider default      */
    float       temperature;        /* < 0  = provider default      */
    float       top_p;              /* < 0  = provider default      */
    int         stream;             /* 0 = false (complete response) */
    char        system_prompt[1024]; /* empty string = none          */

    /*
     * Whether to surface the model's reasoning trace (Qwen3
     * <think>...</think>) on the call.
     *
     * 1 (default) = generator separates reasoning from the final answer:
     *               ca_chat_response_t.reasoning_content gets the reasoning,
     *               ca_chat_response_t.text gets the answer. Streaming
     *               callers see fragments tagged with
     *               CA_CHAT_FRAGMENT_REASONING.
     * 0           = generator still RUNS reasoning (per-call output gating,
     *               NOT a thinking disable) but the reasoning text is
     *               dropped — only the final answer reaches the caller.
     *               Use for JSON-strict consumers.
     */
    int         include_reasoning;
} ca_generation_options_t;

/*
 * Initialise a GenerationOptions struct with sensible defaults.
 *   model = NULL, max_tokens = 0, temperature = -1.0f, top_p = -1.0f,
 *   stream = 0, system_prompt = "", include_reasoning = 1
 */
void ca_generation_options_init(ca_generation_options_t *opts);

/* ---------------------------------------------------------------------------
 * IChatGenerator — callback-based async interface
 *
 * Implementations invoke on_complete exactly once when a response is ready,
 * passing the UTF-8 response string and the caller-supplied userdata pointer.
 * The response string lifetime is only guaranteed for the duration of the
 * callback; copy it if you need it beyond that.
 * --------------------------------------------------------------------------- */

typedef void (*ca_generate_callback)(const char *response, void *userdata);

/*
 * Fragment-aware streaming callback. Invoked once per emitted fragment with
 * its kind (content vs reasoning) and UTF-8 text. The fragment.text pointer
 * is valid only for the duration of the callback. Pass NULL on the
 * ca_chat_generator_t to opt out of streaming.
 *
 * Pulls in <ca_chat_fragment_t> from models_v15.h — host code that wires up
 * the generator must include both headers.
 */
typedef void (*ca_stream_fragment_callback)(
    const ca_chat_fragment_t *fragment,
    void                     *userdata);

typedef struct {
    ca_generate_callback        on_complete;
    /*
     * Optional. When non-NULL the generator yields fragments here AS WELL AS
     * accumulating the full text for on_complete. Implementations that don't
     * surface reasoning should still drive this callback with kind ==
     * CA_CHAT_FRAGMENT_CONTENT for byte-for-byte parity with on_complete.
     */
    ca_stream_fragment_callback on_fragment;
    void                       *userdata;
} ca_chat_generator_t;

/* ---------------------------------------------------------------------------
 * Model capability flags (bitmask)
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_CAP_NONE          = 0,
    CA_CAP_VISION        = 1 << 0,
    CA_CAP_TOOL_USE      = 1 << 1,
    CA_CAP_STREAMING     = 1 << 2,
    CA_CAP_SYSTEM_PROMPT = 1 << 3
} ca_model_capability_t;

typedef struct {
    const char            *model_id;
    const char            *display_name;
    int                    max_context_tokens;
    ca_model_capability_t  capabilities; /* bitmask of ca_model_capability_t */
} ca_model_descriptor_t;

#endif /* CIRCLE_AI_INFERENCE_H */
