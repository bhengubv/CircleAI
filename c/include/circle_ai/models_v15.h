#ifndef CIRCLE_AI_MODELS_V15_H
#define CIRCLE_AI_MODELS_V15_H

/*
 * models_v15.h — 1.5.0 portable surface extensions.
 *
 * Kept separate from models.h so the 1.0–1.4 ABI stays byte-stable.
 * Pure C11, no OS-specific headers, no JSON deps.
 */

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

/* ---------------------------------------------------------------------------
 * Finish reason
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_FINISH_STOP          = 0,
    CA_FINISH_MAX_TOKENS    = 1,
    CA_FINISH_STOP_SEQUENCE = 2,
    CA_FINISH_CANCELLED     = 3,
    CA_FINISH_ERROR         = 4,
    CA_FINISH_UNKNOWN       = 5
} ca_finish_reason_t;

/* ---------------------------------------------------------------------------
 * Structured chat response
 *
 * reasoning_content is the chain-of-thought emitted by reasoning models
 * (Qwen3 / DeepSeek-R1 / o1) inside <think>...</think>. NULL when the model
 * emitted no reasoning or ca_generation_options.include_reasoning was 0.
 * Tags themselves are stripped — only the text content.
 * --------------------------------------------------------------------------- */

typedef struct {
    const char        *text;              /* UTF-8, caller owns */
    ca_finish_reason_t finish_reason;
    int32_t            tokens_generated;  /* -1 = unknown */
    const char        *reasoning_content; /* UTF-8, caller owns; NULL when absent */
} ca_chat_response_t;

/* ---------------------------------------------------------------------------
 * Chat fragment (streaming router output)
 * --------------------------------------------------------------------------- */

typedef enum {
    /* Part of the user-facing answer (goes into "content"). */
    CA_CHAT_FRAGMENT_CONTENT   = 0,
    /* Part of the model's reasoning trace (goes into "reasoning_content"). */
    CA_CHAT_FRAGMENT_REASONING = 1
} ca_chat_fragment_kind_t;

/*
 * One fragment yielded by a streaming generator. Tagged so callers can
 * route the model's <think> block into a separate reasoning_content field
 * (o1 / DeepSeek style). text is UTF-8 and lives for the duration of the
 * callback only — copy it if you need to retain it.
 */
typedef struct {
    ca_chat_fragment_kind_t kind;
    const char             *text;
} ca_chat_fragment_t;

/* ---------------------------------------------------------------------------
 * Bundle file (one file inside a model bundle)
 * --------------------------------------------------------------------------- */

typedef struct {
    const char *name;       /* relative path, caller owns */
    const char *sha256;     /* lowercase hex, caller owns */
    int64_t     size_bytes;
} ca_bundle_file_t;

/* ---------------------------------------------------------------------------
 * Installed manifest (lives at <storage>/<modelId>/installed.json)
 * --------------------------------------------------------------------------- */

typedef struct {
    const char       *model_id;
    const char       *version;
    const char       *repo;            /* may be NULL */
    int64_t           total_bytes;
    ca_bundle_file_t *files;           /* caller owns array + strings */
    size_t            files_count;
    int64_t           installed_at_unix_ms;  /* Unix ms UTC */
} ca_installed_manifest_t;

/* ---------------------------------------------------------------------------
 * Upgrade reason / info
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_UPGRADE_UNKNOWN         = 0,
    CA_UPGRADE_VERSION_CHANGED = 1,
    CA_UPGRADE_SHA_CHANGED     = 2,
    CA_UPGRADE_BOTH            = 3
} ca_upgrade_reason_t;

typedef struct {
    const char         *model_id;            /* caller owns */
    const char         *installed_version;   /* may be NULL */
    const char         *available_version;
    ca_upgrade_reason_t reason;
    int64_t             estimated_download_bytes;
    int64_t             detected_at_unix_ms;
} ca_upgrade_info_t;

/* ---------------------------------------------------------------------------
 * Vision chat message (multimodal extension to ca_chat_message_t)
 * --------------------------------------------------------------------------- */

typedef struct {
    const char    *role;
    const char    *content;
    const uint8_t *image_bytes;   /* may be NULL */
    size_t         image_len;
} ca_vision_chat_message_t;

#endif /* CIRCLE_AI_MODELS_V15_H */
