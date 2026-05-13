#ifndef CIRCLE_AI_MODELS_H
#define CIRCLE_AI_MODELS_H

#include <stdint.h>

typedef enum {
    CA_ROLE_USER = 0,
    CA_ROLE_ASSISTANT,
    CA_ROLE_SYSTEM
} ca_role_t;

typedef struct {
    ca_role_t role;
    const char* content;
    int64_t     created_at; /* unix ms */
} ca_chat_message_t;

typedef struct {
    int64_t bytes_received;
    int64_t bytes_total;    /* 0 = unknown */
    float   progress;       /* 0.0-1.0 */
} ca_download_progress_t;

#endif /* CIRCLE_AI_MODELS_H */
