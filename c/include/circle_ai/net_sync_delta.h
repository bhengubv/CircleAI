#ifndef CIRCLE_AI_NET_SYNC_DELTA_H
#define CIRCLE_AI_NET_SYNC_DELTA_H

/*
 * net_sync_delta.h — CircleAI.Networking.SyncDelta + SchedulingHint (C11 port).
 *
 * The cross-device continuity primitive consumed by ISyncChannel implementations
 * (AetherSyncChannel, DtnSyncChannel). This is the CircleAI.Networking SyncDelta
 * record — distinct from sync.h's fixture ca_sync_delta_t and the MemorySync
 * variant: it carries owner/source/target device ids, a domain key, an opaque
 * payload, a monotonic per-owner+domain sequence, a delivery mode, an optional
 * TTL, a creation timestamp, and an optional AI-layer SchedulingHint.
 *
 * SyncDeliveryMode here is the Networking enum (BestEffort/Guaranteed/Urgent),
 * NOT sync.h's (Immediate/Batched/BestEffort). Defined locally to avoid clashing.
 *
 * Conventions: ca_ prefix, _t types, strdup-owning fields with matching *_free,
 * deep-copy helper, Unix ms UTC timestamps.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* SyncDeliveryMode (CircleAI.Networking.SyncDeliveryMode). */
typedef enum {
    CA_NET_DELIVERY_BEST_EFFORT = 0,
    CA_NET_DELIVERY_GUARANTEED  = 1,
    CA_NET_DELIVERY_URGENT      = 2
} ca_net_delivery_mode_t;

/* SchedulingHint — optional AI-layer routing advisory.
 * PreferredPeerIds is an owned array of owned strings. SuggestedWindowUtc is
 * optional (has_window gate, Unix ms). */
typedef struct {
    char  **preferred_peer_ids; /* owned array of owned strings */
    size_t  preferred_count;
    bool    has_window;
    int64_t suggested_window_unix_ms;
    float   confidence_score;
} ca_net_scheduling_hint_t;

/* SyncDelta.
 * target_device_id "" == broadcast to all owned devices (kept as given).
 * payload is an owned byte copy. ttl optional (has_ttl gate, ms). scheduling_hint
 * optional (owned, may be NULL). */
typedef struct {
    char                    *owner_id;         /* owned, non-null */
    char                    *source_device_id; /* owned, non-null */
    char                    *target_device_id; /* owned, non-null ("" = bcast) */
    char                    *domain_key;       /* owned, non-null */
    uint8_t                 *payload;          /* owned */
    size_t                   payload_len;
    int64_t                  sequence;
    ca_net_delivery_mode_t   delivery_mode;
    bool                     has_ttl;
    int64_t                  ttl_ms;
    int64_t                  created_at_unix_ms;
    ca_net_scheduling_hint_t *scheduling_hint; /* owned, may be NULL */
} ca_net_sync_delta_t;

/* Construct a SyncDelta (deep-copies every field, incl. the optional hint). NULL
 * fields for the four required ids/domain are normalised to "" (non-null). NULL
 * on OOM. scheduling_hint may be NULL. */
ca_net_sync_delta_t *ca_net_sync_delta_new(
    const char *owner_id, const char *source_device_id,
    const char *target_device_id, const char *domain_key,
    const uint8_t *payload, size_t payload_len, int64_t sequence,
    ca_net_delivery_mode_t delivery_mode, bool has_ttl, int64_t ttl_ms,
    int64_t created_at_unix_ms, const ca_net_scheduling_hint_t *scheduling_hint);

void ca_net_sync_delta_destroy(ca_net_sync_delta_t *d);
ca_net_sync_delta_t *ca_net_sync_delta_copy(const ca_net_sync_delta_t *d);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_NET_SYNC_DELTA_H */
