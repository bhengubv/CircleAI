#ifndef CIRCLE_AI_AGENTS_H
#define CIRCLE_AI_AGENTS_H

/*
 * agents.h — AgentMessage with auto-synthesised correlation ID.
 */

#include <stdint.h>
#include <stddef.h>

typedef enum {
    CA_AGENT_DISCOVER         = 0,
    CA_AGENT_GREET            = 1,
    CA_AGENT_CAPABILITY_QUERY = 2,
    CA_AGENT_INVOKE           = 3,
    CA_AGENT_RESPONSE         = 4,
    CA_AGENT_DECLINE          = 5,
    CA_AGENT_HEARTBEAT        = 6
} ca_agent_message_kind_t;

typedef struct {
    char                    id[37];                /* UUID v4 string */
    ca_agent_message_kind_t kind;
    const char             *from_uhid;
    const char             *to_uhid;
    const char             *content_type;
    const uint8_t          *payload;
    size_t                  payload_len;
    const uint8_t          *signature;
    size_t                  signature_len;
    int64_t                 sent_at_unix_ms;
    char                    correlation_id[33];    /* 32 hex chars + NUL */
} ca_agent_message_t;

/* Build a message. If correlation_id_in is non-NULL and non-empty, it is
 * copied (truncated to 32 chars). Otherwise a fresh 32-hex correlation ID
 * is synthesised. UUID id is also synthesised. */
void ca_agent_message_init(
    ca_agent_message_t      *m,
    ca_agent_message_kind_t  kind,
    const char              *from_uhid,
    const char              *to_uhid,
    const char              *content_type,
    const uint8_t           *payload,
    size_t                   payload_len,
    const uint8_t           *signature,
    size_t                   signature_len,
    const char              *correlation_id_in,    /* may be NULL */
    int64_t                  now_unix_ms);

#endif /* CIRCLE_AI_AGENTS_H */
