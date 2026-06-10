#ifndef CIRCLE_AI_SELECTOR_H
#define CIRCLE_AI_SELECTOR_H

/*
 * selector.h — ChatCapability bitmask + model selection.
 */

#include <stdint.h>
#include <stdbool.h>
#include "device.h"

/* Capability flags. Bitwise-composable. Prefixed CA_CHAT_CAP_* to avoid
 * collision with the legacy ca_model_capability_t in inference.h. */
typedef enum {
    CA_CHAT_CAP_NONE      = 0,
    CA_CHAT_CAP_TEXT      = 1u << 0,
    CA_CHAT_CAP_TOOLS     = 1u << 1,
    CA_CHAT_CAP_VISION    = 1u << 2,
    CA_CHAT_CAP_AUDIO     = 1u << 3,
    CA_CHAT_CAP_LONG_CTX  = 1u << 4,
    CA_CHAT_CAP_REASONING = 1u << 5,
    CA_CHAT_CAP_STREAMING = 1u << 6,
    CA_CHAT_CAP_DEFAULT   = (1u << 0) | (1u << 6)  /* TEXT | STREAMING */
} ca_chat_capability_t;

/* Parse a comma- or space-separated capability string. */
uint32_t ca_parse_capabilities(const char *raw);

/* Tier ceiling for selector "fits on device" check. */
int64_t ca_selector_max_bytes_for_tier(ca_device_tier_t tier);

#endif /* CIRCLE_AI_SELECTOR_H */
