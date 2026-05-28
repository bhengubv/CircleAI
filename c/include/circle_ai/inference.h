#ifndef CIRCLE_AI_INFERENCE_H
#define CIRCLE_AI_INFERENCE_H

/*
 * inference.h — Generation options and IChatGenerator callback interface.
 * Pure C11, no OS-specific headers.
 */

#include <stdint.h>

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
} ca_generation_options_t;

/*
 * Initialise a GenerationOptions struct with sensible defaults.
 *   model = NULL, max_tokens = 0, temperature = -1.0f, top_p = -1.0f,
 *   stream = 0, system_prompt = ""
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

typedef struct {
    ca_generate_callback on_complete;
    void                *userdata;
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
