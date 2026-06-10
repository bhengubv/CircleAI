/*
 * hosting.c — AIOptions defaults.
 */

#include "circle_ai/hosting.h"
#include <string.h>

void ca_ai_options_defaults(ca_ai_options_t *opts) {
    if (!opts) return;
    memset(opts, 0, sizeof(*opts));
    opts->system_prompt = "You are B!, a helpful on-device assistant.";
    opts->warm_on_start = true;
    opts->required_capabilities = CA_CHAT_CAP_DEFAULT;
}
