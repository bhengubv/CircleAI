#ifndef CIRCLE_AI_SYNC_H
#define CIRCLE_AI_SYNC_H

#include <stdint.h>

typedef enum {
    CA_SYNC_REALTIME = 0, CA_SYNC_BEST_EFFORT, CA_SYNC_BATCH
} ca_sync_delivery_mode_t;

typedef struct {
    char                  delta_id[37];
    const char*           domain;
    const char*           entity_id;
    int64_t               timestamp;     /* unix ms */
    const char*           payload_json;
    ca_sync_delivery_mode_t delivery_mode;
    int                   sequence;
} ca_sync_delta_t;

/* Domain key constants */
#define CA_DOMAIN_AFFECT    "affect"
#define CA_DOMAIN_GOALS     "goals"
#define CA_DOMAIN_PERSONA   "persona"
#define CA_DOMAIN_MEMORY    "memory"
#define CA_DOMAIN_IDENTITY  "identity"

#endif /* CIRCLE_AI_SYNC_H */
