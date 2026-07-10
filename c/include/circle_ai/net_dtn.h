#ifndef CIRCLE_AI_NET_DTN_H
#define CIRCLE_AI_NET_DTN_H

/*
 * net_dtn.h — CircleAI.Networking.Dtn (C11 port).
 *
 * Delay-tolerant-networking store-and-forward. Ports CircleAI.Networking.Dtn 1:1:
 *
 *   Enum      : DtnPriority
 *   Records   : DtnBundle, DtnCustodyRecord
 *   Store     : InMemoryDtnBundleStore (bundles + custody + expiry/purge/in-flight)
 *   SyncChannel : DtnSyncChannel — ISyncChannel over DTN. PushDeltaAsync builds a
 *               DtnBundle (72h default TTL; CustodyRequired iff DeliveryMode==
 *               Guaranteed), then sends over the FIRST available injected
 *               INetworkTransport as a NetworkPayload tagged "application/dtn-bundle"
 *               (Urgent priority iff DeliveryMode==Urgent, else Normal). When no
 *               transport is available the bundle is queued locally. Tracks last
 *               sequence per (ownerId, domainKey). Delivered deltas drain an
 *               UNBOUNDED channel.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no hashtable, no pthreads. Timestamps are Unix ms UTC, passed in.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "networking.h"      /* ca_network_transport_t, ca_network_payload_t */
#include "net_sync_delta.h"  /* ca_net_sync_delta_t */

#ifdef __cplusplus
extern "C" {
#endif

/* 72 hours in milliseconds — the default bundle TTL. */
#define CA_DTN_DEFAULT_TTL_MS (72LL * 60LL * 60LL * 1000LL)

/* ===========================================================================
 * DtnPriority
 * =========================================================================== */

typedef enum {
    CA_DTN_PRIORITY_BULK      = 0,
    CA_DTN_PRIORITY_NORMAL    = 1,
    CA_DTN_PRIORITY_EXPEDITED = 2
} ca_dtn_priority_t;

/* ===========================================================================
 * DtnBundle — a self-contained delivery unit with TTL + custody semantics.
 * =========================================================================== */

typedef struct {
    char   *bundle_id;           /* owned, non-null */
    char   *source_node_id;      /* owned, non-null */
    char   *destination_node_id; /* owned, non-null */
    uint8_t *payload;            /* owned */
    size_t  payload_len;
    int64_t expires_at_unix_ms;  /* default: CreatedAt + 72h */
    bool    custody_required;
    int     hop_count;
    int64_t created_at_unix_ms;
} ca_dtn_bundle_t;

ca_dtn_bundle_t *ca_dtn_bundle_new(
    const char *bundle_id, const char *source_node_id,
    const char *destination_node_id, const uint8_t *payload,
    size_t payload_len, int64_t expires_at_unix_ms, bool custody_required,
    int hop_count, int64_t created_at_unix_ms);
void ca_dtn_bundle_destroy(ca_dtn_bundle_t *b);
ca_dtn_bundle_t *ca_dtn_bundle_copy(const ca_dtn_bundle_t *b);
void ca_dtn_bundle_free_array(ca_dtn_bundle_t **arr, size_t count);

/* ===========================================================================
 * DtnCustodyRecord
 * =========================================================================== */

typedef struct {
    char   *bundle_id;         /* owned */
    char   *custodian_node;    /* owned */
    int64_t accepted_at_unix_ms;
} ca_dtn_custody_record_t;

ca_dtn_custody_record_t *ca_dtn_custody_record_new(
    const char *bundle_id, const char *custodian_node,
    int64_t accepted_at_unix_ms);
void ca_dtn_custody_record_destroy(ca_dtn_custody_record_t *r);
ca_dtn_custody_record_t *ca_dtn_custody_record_copy(
    const ca_dtn_custody_record_t *r);

/* ===========================================================================
 * InMemoryDtnBundleStore
 *
 * Store: LWW by BundleId. Get: fresh copy or NULL. All: every bundle (copies).
 * AcceptCustody: LWW by BundleId. GetCustody: fresh copy or NULL. IsExpired:
 * true when absent OR now > ExpiresAt. Purge: remove all expired (bundle +
 * custody), return count removed. InFlightTo: bundles for a destination (copies).
 * =========================================================================== */

typedef struct ca_dtn_bundle_store ca_dtn_bundle_store_t;

ca_dtn_bundle_store_t *ca_dtn_bundle_store_create(void);
void ca_dtn_bundle_store_destroy(ca_dtn_bundle_store_t *s);

int ca_dtn_bundle_store_store(ca_dtn_bundle_store_t *s,
                              const ca_dtn_bundle_t *b);
ca_dtn_bundle_t *ca_dtn_bundle_store_get(const ca_dtn_bundle_store_t *s,
                                         const char *bundle_id);
/* All — owned array of owned copies. On error *out=NULL,*count=SIZE_MAX. */
int ca_dtn_bundle_store_all(const ca_dtn_bundle_store_t *s,
                            ca_dtn_bundle_t ***out, size_t *count);
int ca_dtn_bundle_store_accept_custody(ca_dtn_bundle_store_t *s,
                                       const ca_dtn_custody_record_t *r);
ca_dtn_custody_record_t *ca_dtn_bundle_store_get_custody(
    const ca_dtn_bundle_store_t *s, const char *bundle_id);
bool ca_dtn_bundle_store_is_expired(const ca_dtn_bundle_store_t *s,
                                    const char *bundle_id, int64_t now_unix_ms);
int ca_dtn_bundle_store_purge(ca_dtn_bundle_store_t *s, int64_t now_unix_ms);
/* InFlightTo — bundles addressed to a destination (copies). */
int ca_dtn_bundle_store_in_flight_to(const ca_dtn_bundle_store_t *s,
                                     const char *destination_node_id,
                                     ca_dtn_bundle_t ***out, size_t *count);

/* ===========================================================================
 * DtnSyncChannel — ISyncChannel over DTN store-and-forward.
 *
 * Constructed with a snapshot of injected INetworkTransport vtables (borrowed;
 * they must outlive the channel). PushDeltaAsync creates a bundle and, if any
 * transport IsAvailable, sends over the first available one; the bundle_id used
 * for that bundle is supplied by the caller (Guid "N" in the C#). To keep the
 * port deterministic and free of hidden global entropy, the caller passes the
 * bundle_id and the two timestamps (now for CreatedAt; ExpiresAt is derived).
 * =========================================================================== */

typedef struct ca_dtn_sync_channel ca_dtn_sync_channel_t;

/* Create over `count` transport vtables (copied by value into an owned array;
 * the underlying transport objects are borrowed). NULL on OOM. */
ca_dtn_sync_channel_t *ca_dtn_sync_channel_create(
    const ca_network_transport_t *transports, size_t count);
void ca_dtn_sync_channel_destroy(ca_dtn_sync_channel_t *c);

/* PushDeltaAsync(delta): build a bundle (id=bundle_id_n, CreatedAt=now_unix_ms,
 * ExpiresAt = now + (delta.Ttl ?? 72h), CustodyRequired = delivery==Guaranteed,
 * HopCount=0) and, if a transport is available, send the payload over the first
 * available transport (priority Urgent iff delivery==Urgent else Normal;
 * content-type "application/dtn-bundle"; destination = delta.TargetDeviceId).
 * Returns 0 on success (sent or queued), -1 on error (NULL/OOM/send failure).
 * When no transport is available the bundle is retained in the local queue. */
int ca_dtn_sync_channel_push_delta(ca_dtn_sync_channel_t *c,
                                   const ca_net_sync_delta_t *delta,
                                   const char *bundle_id_n, int64_t now_unix_ms);

/* Deliver a delta into the delivered channel (the DTN-received seam). Deep
 * copied; drained FIFO by receive_next. Returns 0 / -1. */
int ca_dtn_sync_channel_deliver(ca_dtn_sync_channel_t *c,
                                const ca_net_sync_delta_t *delta);
/* ReceiveDeltasAsync(ownerId): drain one delivered delta. true if produced. */
bool ca_dtn_sync_channel_receive_next(ca_dtn_sync_channel_t *c,
                                      const char *owner_id,
                                      ca_net_sync_delta_t **out);
/* GetLastSequenceAsync(ownerId, domainKey): last seen sequence (0 if unseen). */
int64_t ca_dtn_sync_channel_last_sequence(const ca_dtn_sync_channel_t *c,
                                          const char *owner_id,
                                          const char *domain_key);
/* Seed / advance the sequence for (ownerId, domainKey). Returns 0 / -1. */
int ca_dtn_sync_channel_set_sequence(ca_dtn_sync_channel_t *c,
                                     const char *owner_id,
                                     const char *domain_key, int64_t seq);
/* Number of bundles currently queued locally (no transport was available). */
size_t ca_dtn_sync_channel_queued(const ca_dtn_sync_channel_t *c);
/* Number of delivered deltas still pending (undrained). */
size_t ca_dtn_sync_channel_pending(const ca_dtn_sync_channel_t *c);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_NET_DTN_H */
