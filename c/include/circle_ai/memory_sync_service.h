#ifndef CIRCLE_AI_MEMORY_SYNC_SERVICE_H
#define CIRCLE_AI_MEMORY_SYNC_SERVICE_H

/*
 * memory_sync_service.h — CircleAI.Sync (C11 port).
 *
 * Ports the CircleAI.Sync project:
 *   - IMemorySyncService + MemorySyncService  (IMemorySyncService.cs / MemorySyncService.cs)
 *   - SyncDomainKeys                          (SyncDomainKeys.cs)
 *   - VersionVector + SyncReconciliation      (SyncPrimitives.cs)
 *   - the SyncDelta / SyncDeliveryMode / ISyncChannel seam it rides
 *     (CircleAI.Networking SyncDelta.cs + NetworkTypes.cs + ISyncChannel.cs)
 *
 * NB: distinct from sync.h's fixture ca_sync_delta_t / ca_sync_delivery_mode_t
 * (IMMEDIATE/BATCHED/BEST_EFFORT). This ports the CircleAI.Networking SyncDelta
 * record used by MemorySyncService — OwnerId, SourceDeviceId, TargetDeviceId,
 * DomainKey, Payload bytes, Sequence, DeliveryMode {BestEffort,Guaranteed,Urgent},
 * Ttl?, CreatedAt.
 *
 * The transport (ISyncChannel) and episodic store are INJECTED seams so the app
 * code is identical whatever the transport. The C# ReceiveLoopAsync skips own
 * echoes then dispatches on DomainKey; the episodic branch applies the delta to
 * the local store via the injected apply callback (the C# left this as a
 * "// Full wire" comment — the C port wires a deterministic apply seam so the
 * contract is real, with the exact skip-own-echo + domain-dispatch structure).
 *
 * Conventions: ca_ prefix, _t types, opaque handle, deep-copied owning fields,
 * NULL/false errors. Pure C11 + libc (no threads: the receive loop is driven
 * explicitly by pumping the channel's delta callback).
 */

#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * SyncDeliveryMode — NetworkTypes.cs { BestEffort, Guaranteed, Urgent }
 * =========================================================================== */

typedef enum {
    CA_SYNC_DELIVERY_BEST_EFFORT = 0,
    CA_SYNC_DELIVERY_GUARANTEED  = 1,
    CA_SYNC_DELIVERY_URGENT      = 2
} ca_mss_delivery_mode_t;

/* ===========================================================================
 * SyncDelta — CircleAI.Networking SyncDelta.cs
 *
 * SchedulingHint is an optional AI-layer advisory; represented as an opaque
 * borrowed pointer (NULL when absent) since it is not consumed here.
 * =========================================================================== */

typedef struct {
    char                  *owner_id;         /* owned */
    char                  *source_device_id; /* owned */
    char                  *target_device_id; /* owned; "" = broadcast */
    char                  *domain_key;       /* owned */
    uint8_t               *payload;          /* owned copy */
    size_t                 payload_len;
    int64_t                sequence;         /* monotonic per owner+domain */
    ca_mss_delivery_mode_t delivery_mode;
    bool                   has_ttl;
    int64_t                ttl_ms;           /* valid iff has_ttl */
    int64_t                created_at_ms;    /* Unix ms UTC */
    const void            *scheduling_hint;  /* borrowed, or NULL */
} ca_sync_delta_full_t;

/* Free owned fields of a delta (not the struct). */
void ca_sync_delta_full_free(ca_sync_delta_full_t *d);
/* Deep-copy src into dst. Returns dst. */
ca_sync_delta_full_t *ca_sync_delta_full_copy(ca_sync_delta_full_t *dst,
                                              const ca_sync_delta_full_t *src);

/* ===========================================================================
 * SyncDomainKeys — SyncDomainKeys.cs
 * =========================================================================== */

#define CA_SYNC_DOMAIN_EPISODIC_MEMORY "memory.episodic"
#define CA_SYNC_DOMAIN_AFFECT_STATE    "affect.state"
#define CA_SYNC_DOMAIN_PERSONA         "persona"
#define CA_SYNC_DOMAIN_GOALS           "goals"
#define CA_SYNC_DOMAIN_SKILLS          "skills"
#define CA_SYNC_DOMAIN_PREFERENCES     "preferences"

/* ===========================================================================
 * VersionVector + SyncReconciliation — SyncPrimitives.cs
 *
 * VersionVector wraps a (nodeId → clock) map (linear array here). Merge takes
 * the per-key max; ADominatesB is "a >= b everywhere AND strictly greater
 * somewhere" (treating absent keys as 0); LastWriterWins picks the later
 * timestamp (ties → a).
 * =========================================================================== */

typedef struct {
    char   **keys;    /* owned array of owned strings */
    int64_t *clocks;  /* parallel array */
    size_t   count;
} ca_version_vector_t;

/* Create a version vector from parallel arrays (deep-copied). keys/clocks may
 * be NULL when count == 0. */
ca_version_vector_t *ca_version_vector_create(const char *const *keys,
                                              const int64_t *clocks, size_t count);
void   ca_version_vector_destroy(ca_version_vector_t *v);
/* Clock for key (0 when absent). */
int64_t ca_version_vector_get(const ca_version_vector_t *v, const char *key);

/* Merge — fresh vector of the per-key maxima over the union of keys (caller
 * destroys). Returns NULL on a NULL argument. */
ca_version_vector_t *ca_sync_reconciliation_merge(const ca_version_vector_t *a,
                                                  const ca_version_vector_t *b);

/* ADominatesB — a dominates b (>= everywhere over the key union, strictly
 * greater somewhere). */
bool ca_sync_reconciliation_a_dominates_b(const ca_version_vector_t *a,
                                          const ca_version_vector_t *b);

/* LastWriterWins for an int64 value: returns the value whose timestamp is >=
 * the other (ties → a), and writes the winning timestamp to *out_at. */
int64_t ca_sync_reconciliation_last_writer_wins_i64(
    int64_t a_at, int64_t a_val, int64_t b_at, int64_t b_val, int64_t *out_at);

/* ===========================================================================
 * ISyncChannel seam — ISyncChannel.cs
 *
 * PushDeltaAsync + GetLastSequenceAsync are direct calls; ReceiveDeltasAsync is
 * an async stream in C#. With no threads, the channel instead invokes a
 * registered delta callback for each inbound delta (the service registers its
 * ReceiveLoop handler on StartReceiving and unregisters on StopReceiving). A
 * test/in-proc channel drives inbound delivery by calling the stored callback.
 * =========================================================================== */

/* Inbound delta callback (one per ReceiveDeltasAsync yield). */
typedef void (*ca_sync_channel_delta_cb)(void *user, const ca_sync_delta_full_t *delta);

typedef struct {
    void *self;
    /* PushDeltaAsync — returns true when accepted. */
    bool (*push_delta)(void *self, const ca_sync_delta_full_t *delta);
    /* Begin receiving for owner_id: store (cb,user) to invoke per inbound delta.
     * Returns an opaque subscription token (or NULL). */
    void *(*receive_start)(void *self, const char *owner_id,
                           ca_sync_channel_delta_cb cb, void *user);
    /* Stop receiving for a subscription token. */
    void  (*receive_stop)(void *self, void *subscription);
    /* GetLastSequenceAsync — highest sequence seen for owner+domain (0 default). */
    int64_t (*get_last_sequence)(void *self, const char *owner_id, const char *domain_key);
} ca_sync_channel_iface_t;

/* ===========================================================================
 * IEpisodicMemoryStore apply seam
 *
 * MemorySyncService's ReceiveLoop upserts an inbound episodic delta into the
 * local store. The store is injected as an apply callback taking the raw
 * payload bytes (the wire form the delta carried). Returns true on apply.
 * =========================================================================== */

typedef bool (*ca_episodic_apply_cb)(void *user, const char *owner_id,
                                     const uint8_t *payload, size_t len);

/* ===========================================================================
 * MemorySyncService — MemorySyncService.cs
 * =========================================================================== */

typedef struct ca_memory_sync_service ca_memory_sync_service_t;

/* Create over an injected channel + episodic-apply seam + local device id.
 * episodic_apply may be NULL (the episodic domain branch then just skips, like
 * the C# comment-only body). Returns NULL on NULL channel / blank device id. */
ca_memory_sync_service_t *ca_memory_sync_service_create(
    ca_sync_channel_iface_t channel,
    ca_episodic_apply_cb episodic_apply, void *episodic_apply_user,
    const char *local_device_id);
void ca_memory_sync_service_destroy(ca_memory_sync_service_t *svc);

/* PushMemoryDeltaAsync — wrap (owner, domain, delta) into a SyncDelta with
 * SourceDeviceId = local, TargetDeviceId = "" (broadcast), Sequence = now-ms,
 * CreatedAt = now, Ttl = none, and push it. Returns the channel's accept
 * result. Default mode is Guaranteed (pass CA_SYNC_DELIVERY_GUARANTEED). */
bool ca_memory_sync_service_push_delta(
    ca_memory_sync_service_t *svc,
    const char *owner_id, const char *domain_key,
    const uint8_t *delta, size_t delta_len,
    ca_mss_delivery_mode_t mode);

/* StartReceivingAsync — subscribe the receive loop for owner_id. Idempotent per
 * service (a second call replaces the prior subscription). */
bool ca_memory_sync_service_start_receiving(ca_memory_sync_service_t *svc,
                                            const char *owner_id);

/* StopReceivingAsync — cancel the receive subscription. */
void ca_memory_sync_service_stop_receiving(ca_memory_sync_service_t *svc);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_MEMORY_SYNC_SERVICE_H */
