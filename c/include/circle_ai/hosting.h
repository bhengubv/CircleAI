#ifndef CIRCLE_AI_HOSTING_H
#define CIRCLE_AI_HOSTING_H

/*
 * hosting.h — IAIObserver + AIOptions for the hosting layer.
 *
 * In C, "interface" is a struct of function pointers; pass NULL for callbacks
 * you don't care about.
 */

#include <stdint.h>
#include <stdbool.h>
#include "models_v15.h"
#include "selector.h"

typedef struct {
    void (*on_started)(void *user);
    void (*on_stopped)(void *user);
    void (*on_chat_completed)(void *user, const ca_chat_response_t *response);
    void (*on_stream_started)(void *user, const char *model_id);
    void (*on_stream_completed)(void *user, const char *model_id, uint32_t token_count);
    void (*on_tool_invoked)(void *user, const char *tool_name, bool success);
    void (*on_model_fetching)(void *user, const char *model_id, bool auto_selected);
    void (*on_upgrade_available)(void *user, const ca_upgrade_info_t *upgrade);
    void *user;
} ca_ai_observer_t;

typedef struct {
    const char       *model_id;                   /* may be NULL */
    const char       *model_path;                 /* may be NULL */
    const char       *system_prompt;
    uint32_t          context_size;
    uint32_t          thread_count;
    bool              warm_on_start;
    uint32_t          required_capabilities;      /* OR of ca_chat_capability_t */
    uint32_t          agentic_max_iterations;
    bool              check_for_upgrades_on_start;
    const char       *model_storage_directory;    /* may be NULL */
    ca_ai_observer_t *observer;                   /* may be NULL */
} ca_ai_options_t;

/* Fills opts with default values. */
void ca_ai_options_defaults(ca_ai_options_t *opts);

#endif /* CIRCLE_AI_HOSTING_H */
