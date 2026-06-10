/*
 * selector.c — capability parsing + tier ceiling.
 */

#include "circle_ai/selector.h"
#include <string.h>
#include <ctype.h>

static int ieq(const char *a, const char *b) {
    while (*a && *b) {
        if (tolower((unsigned char)*a) != tolower((unsigned char)*b)) return 0;
        a++; b++;
    }
    return *a == 0 && *b == 0;
}

uint32_t ca_parse_capabilities(const char *raw) {
    if (!raw) return 0;
    uint32_t out = 0;
    const char *p = raw;
    char buf[32];
    while (*p) {
        /* skip separators */
        while (*p == ',' || *p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
        if (!*p) break;
        size_t i = 0;
        while (*p && *p != ',' && *p != ' ' && *p != '\t' && *p != '\n' && *p != '\r' && i + 1 < sizeof(buf)) {
            buf[i++] = (char)tolower((unsigned char)*p++);
        }
        buf[i] = 0;
        /* skip rest of long token */
        while (*p && *p != ',' && *p != ' ' && *p != '\t' && *p != '\n' && *p != '\r') p++;

        if      (ieq(buf, "text"))      out |= CA_CHAT_CAP_TEXT;
        else if (ieq(buf, "tools"))     out |= CA_CHAT_CAP_TOOLS;
        else if (ieq(buf, "vision"))    out |= CA_CHAT_CAP_VISION;
        else if (ieq(buf, "audio"))     out |= CA_CHAT_CAP_AUDIO;
        else if (ieq(buf, "longctx"))   out |= CA_CHAT_CAP_LONG_CTX;
        else if (ieq(buf, "long_ctx"))  out |= CA_CHAT_CAP_LONG_CTX;
        else if (ieq(buf, "reasoning")) out |= CA_CHAT_CAP_REASONING;
        else if (ieq(buf, "streaming")) out |= CA_CHAT_CAP_STREAMING;
    }
    return out;
}

int64_t ca_selector_max_bytes_for_tier(ca_device_tier_t tier) {
    switch (tier) {
        case CA_TIER_WEARABLE:    return 200LL  * 1024 * 1024;
        case CA_TIER_EMBEDDED:    return 500LL  * 1024 * 1024;
        case CA_TIER_PHONE:       return 2500000000LL;
        case CA_TIER_TABLET:      return 6000000000LL;
        case CA_TIER_LAPTOP:      return 20000000000LL;
        case CA_TIER_WORKSTATION: return 60000000000LL;
    }
    return 2500000000LL;
}
