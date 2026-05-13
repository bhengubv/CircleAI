#ifndef CIRCLE_AI_COMPANION_H
#define CIRCLE_AI_COMPANION_H

#include <stdint.h>

typedef enum {
    CA_IFACE_VOICE = 0,
    CA_IFACE_TEXT,
    CA_IFACE_VISUAL,
    CA_IFACE_AMBIENT
} ca_interface_kind_t;

typedef struct {
    char               session_id[37];
    char               identity_id[37];
    ca_interface_kind_t interface_kind;
    const char*        locale;
    int64_t            started_at; /* unix ms */
} ca_companion_context_t;

typedef struct {
    char        turn_id[37];
    char        session_id[37];
    const char* user_input;
    const char* assistant_response;
    int64_t     created_at;  /* unix ms */
    int         turn_index;
} ca_companion_turn_t;

typedef enum {
    CA_PROACTIVE_IDLE_TOO_LONG = 0,
    CA_PROACTIVE_TOPIC_SHIFT,
    CA_PROACTIVE_GOAL_COMPLETED,
    CA_PROACTIVE_GOAL_SUGGESTED,
    CA_PROACTIVE_MEMORY_RECALLED
} ca_proactive_event_kind_t;

typedef struct {
    ca_proactive_event_kind_t kind;
    const char* payload;
} ca_proactive_event_t;

#endif /* CIRCLE_AI_COMPANION_H */
