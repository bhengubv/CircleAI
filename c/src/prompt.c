/*
 * prompt.c — Fallback ChatML renderer.
 */

#include "circle_ai/prompt.h"
#include <string.h>
#include <ctype.h>
#include <stdio.h>

const char CA_FALLBACK_CHAT_TEMPLATE[] =
    "{%- for message in messages -%}\n"
    "<|im_start|>{{ message.role }}\n"
    "{{ message.content }}<|im_end|>\n"
    "{% endfor -%}\n"
    "{%- if add_generation_prompt -%}\n"
    "<|im_start|>assistant\n"
    "{%- endif -%}";

static const char *role_str(ca_role_t r) {
    switch (r) {
        case CA_ROLE_USER:      return "user";
        case CA_ROLE_ASSISTANT: return "assistant";
        case CA_ROLE_SYSTEM:    return "system";
    }
    return "user";
}

size_t ca_render_chatml(
    const ca_chat_message_t *messages,
    size_t                   count,
    bool                     add_generation_prompt,
    char                    *out,
    size_t                   out_cap)
{
    size_t written = 0;
    char tmp[256];
    #define EMIT(s) do { \
        size_t _n = strlen(s); \
        if (out && written + 1 < out_cap) { \
            size_t copy = (written + _n < out_cap) ? _n : (out_cap - 1 - written); \
            memcpy(out + written, s, copy); \
        } \
        written += _n; \
    } while (0)

    for (size_t i = 0; i < count; i++) {
        const char *role = role_str(messages[i].role);
        EMIT("<|im_start|>");
        EMIT(role);
        EMIT("\n");
        EMIT(messages[i].content ? messages[i].content : "");
        EMIT("<|im_end|>\n");
        (void)tmp;
    }
    if (add_generation_prompt) {
        EMIT("<|im_start|>assistant\n");
    }
    if (out && out_cap > 0) {
        out[written < out_cap ? written : out_cap - 1] = 0;
    }
    return written;
    #undef EMIT
}
