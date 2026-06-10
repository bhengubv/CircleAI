#ifndef CIRCLE_AI_PROMPT_H
#define CIRCLE_AI_PROMPT_H

/*
 * prompt.h — Fallback ChatML prompt renderer. No Jinja2; host apps that
 * need custom templates wire in their own renderer.
 */

#include <stddef.h>
#include "models.h"

extern const char CA_FALLBACK_CHAT_TEMPLATE[];

/* Renders messages into ChatML. Writes up to out_cap bytes (including the
 * trailing NUL). Returns the number of bytes that WOULD be written (excluding
 * NUL), like snprintf. */
size_t ca_render_chatml(
    const ca_chat_message_t *messages,
    size_t                   count,
    bool                     add_generation_prompt,
    char                    *out,
    size_t                   out_cap);

#endif /* CIRCLE_AI_PROMPT_H */
