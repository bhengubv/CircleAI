/*
 * inference.c — ca_generation_options_init() and related helpers.
 * Pure C11, no OS-specific headers.
 */

#include "circle_ai/inference.h"
#include <string.h>

void ca_generation_options_init(ca_generation_options_t *opts) {
    memset(opts, 0, sizeof(*opts));
    opts->model             = NULL;
    opts->max_tokens        = 0;
    opts->temperature       = -1.0f;
    opts->top_p             = -1.0f;
    opts->stream            = 0;
    opts->system_prompt[0]  = '\0';
    opts->include_reasoning = 1;  /* default: surface reasoning_content */
    opts->budget            = (int)CA_POWER_BUDGET_NORMAL;
    opts->use_prefix_cache  = 0;
}
