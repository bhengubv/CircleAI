#ifndef CIRCLE_AI_SYNC_H
#define CIRCLE_AI_SYNC_H

/*
 * sync.h — SyncDelta, ISyncChannel, and delivery-mode constants.
 * Pure C11, no OS-specific headers.
 */

#include <stdint.h>

/* ---------------------------------------------------------------------------
 * SyncDeliveryMode
 * --------------------------------------------------------------------------- */

typedef enum {
    CA_SYNC_IMMEDIATE   = 0,
    CA_SYNC_BATCHED     = 1,
    CA_SYNC_BEST_EFFORT = 2
} ca_sync_delivery_mode_t;

/* ---------------------------------------------------------------------------
 * SyncDelta — a single change record sent over the sync channel
 * --------------------------------------------------------------------------- */

typedef struct {
    char                    delta_id[37];   /* UUID string */
    const char             *domain;         /* e.g. CA_DOMAIN_AFFECT  */
    const char             *entity_id;      /* identity_id or session_id */
    int64_t                 timestamp_ms;   /* Unix ms UTC */
    const char             *payload_json;   /* caller owns */
    ca_sync_delivery_mode_t delivery_mode;
    int                     sequence;       /* monotonically increasing per entity */
} ca_sync_delta_t;

/* ---------------------------------------------------------------------------
 * Well-known domain constants
 * --------------------------------------------------------------------------- */

#define CA_DOMAIN_AFFECT    "affect"
#define CA_DOMAIN_GOALS     "goals"
#define CA_DOMAIN_PERSONA   "persona"
#define CA_DOMAIN_MEMORY    "memory"
#define CA_DOMAIN_IDENTITY  "identity"
#define CA_DOMAIN_COMPANION "companion"

/* ---------------------------------------------------------------------------
 * ISyncChannel — callback-based interface
 *
 * Implementations call on_delta for each incoming delta and on_flush when
 * a batch has been fully delivered.
 * --------------------------------------------------------------------------- */

typedef void (*ca_sync_delta_fn)(const ca_sync_delta_t *delta, void *userdata);
typedef void (*ca_sync_flush_fn)(void *userdata);

typedef struct {
    ca_sync_delta_fn on_delta;
    ca_sync_flush_fn on_flush;  /* may be NULL */
    void            *userdata;
} ca_sync_channel_t;

#endif /* CIRCLE_AI_SYNC_H */
