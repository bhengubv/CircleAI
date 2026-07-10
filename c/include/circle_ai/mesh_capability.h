#ifndef CIRCLE_AI_MESH_CAPABILITY_H
#define CIRCLE_AI_MESH_CAPABILITY_H

/*
 * mesh_capability.h — CircleAI.AetherNet mesh capability discovery (C11 port).
 *
 * Ports MeshCapabilityRegistry.cs (RT-12 v1):
 *   MeshCapabilityAdvertisement           — one peer's "what I can serve now"
 *   IMeshCapabilityRegistry               — latest-per-peer + filtered query
 *   InMemoryMeshCapabilityRegistry        — thread-safe (C: single-thread) impl
 *   IMeshCapabilityBroadcaster            — publish OUR advertisement
 *   NullMeshCapabilityBroadcaster         — no-op default (no transport bound)
 *
 * The device tier reuses ca_device_tier_t from device.h — the C# DeviceTier
 * enum from CircleAI.Core maps onto the same tiers already ported there.
 *
 * Conventions: ca_ prefix, _t types, opaque create/destroy handles,
 * strdup-owning fields with matching *_free, deep-copy getters, errors via
 * NULL / count SIZE_MAX. In-memory + deterministic; no pthreads; linear arrays.
 *
 * List / Find return a freshly-allocated array of DEEP-COPIED advertisements
 * (mirrors the C# .ToArray() snapshot). The caller frees each element with
 * ca_mesh_capability_advertisement_destroy and then frees the array.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "device.h" /* ca_device_tier_t */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * MeshCapabilityAdvertisement
 * =========================================================================== */

/* One peer's advertisement of what it can serve right now. Pure data. */
typedef struct {
    char            *peer_id;              /* owned — stable opaque peer id */
    char            *model_id;             /* owned — e.g. "Qwen3-1.7B-MNN" */
    int              free_kv_tokens;       /* spare KV-cache budget */
    ca_device_tier_t tier;                 /* device tier */
    int              context_window_tokens;/* model's configured context window */
    int64_t          advertised_at_ms;     /* last publish, Unix ms UTC */
    bool             has_latency_hint;     /* LatencyHintMs != null */
    int              latency_hint_ms;      /* valid iff has_latency_hint */
} ca_mesh_capability_advertisement_t;

/* Build a deep-owning advertisement (heap). peer_id / model_id are copied.
 * NULL on OOM. */
ca_mesh_capability_advertisement_t *ca_mesh_capability_advertisement_create(
    const char *peer_id, const char *model_id, int free_kv_tokens,
    ca_device_tier_t tier, int context_window_tokens, int64_t advertised_at_ms,
    bool has_latency_hint, int latency_hint_ms);
void ca_mesh_capability_advertisement_destroy(
    ca_mesh_capability_advertisement_t *ad);
ca_mesh_capability_advertisement_t *ca_mesh_capability_advertisement_copy(
    const ca_mesh_capability_advertisement_t *ad);

/* ===========================================================================
 * InMemoryMeshCapabilityRegistry — IMeshCapabilityRegistry
 * =========================================================================== */

typedef struct ca_mesh_capability_registry ca_mesh_capability_registry_t;

/* Create an empty registry. now_fn supplies the clock for stale filtering
 * (NULL -> a wall-clock is not available in the pure-C port; callers pass an
 * explicit clock). NULL on OOM. */
typedef int64_t (*ca_mesh_now_fn)(void *user);
ca_mesh_capability_registry_t *ca_mesh_capability_registry_create(
    ca_mesh_now_fn now_fn, void *now_user);
void ca_mesh_capability_registry_destroy(ca_mesh_capability_registry_t *reg);

/* UpsertAsync — publish or replace the advertisement for ad->peer_id. The
 * registry deep-copies the advertisement. Returns 0 on success, -1 on bad args
 * (NULL ad / null-or-whitespace peer id) or OOM. */
int ca_mesh_capability_registry_upsert(
    ca_mesh_capability_registry_t *reg,
    const ca_mesh_capability_advertisement_t *ad);

/* RemoveAsync — remove a peer. Idempotent. Returns true if a peer was removed.
 * false on null-or-whitespace peer id or when absent. */
bool ca_mesh_capability_registry_remove(ca_mesh_capability_registry_t *reg,
                                        const char *peer_id);

/*
 * List — every known advertisement. When has_stale_after, entries older than
 * stale_after_ms (advertised_at < now - stale_after) are filtered out. Writes
 * a freshly-allocated array of deep-copied advertisement pointers into
 * *out_list; returns the count. On OOM / NULL reg writes NULL/SIZE_MAX.
 * An empty result writes NULL and returns 0.
 */
size_t ca_mesh_capability_registry_list(
    const ca_mesh_capability_registry_t *reg,
    bool has_stale_after, int64_t stale_after_ms,
    ca_mesh_capability_advertisement_t ***out_list);

/*
 * Find — every peer that has loaded model_id (OrdinalIgnoreCase) with at least
 * min_free_kv_tokens spare, optionally filtered by staleness, SORTED by spare
 * budget descending (most-capable first; stable for ties). Same array
 * ownership as List. Returns SIZE_MAX on NULL reg / null-or-whitespace
 * model_id / OOM.
 */
size_t ca_mesh_capability_registry_find(
    const ca_mesh_capability_registry_t *reg, const char *model_id,
    int min_free_kv_tokens, bool has_stale_after, int64_t stale_after_ms,
    ca_mesh_capability_advertisement_t ***out_list);

/* Number of peers currently tracked (diagnostic; not in the C# surface but
 * handy for tests). */
size_t ca_mesh_capability_registry_count(
    const ca_mesh_capability_registry_t *reg);

/* Free a result array from List / Find (destroys each element + the array). */
void ca_mesh_capability_advertisement_list_free(
    ca_mesh_capability_advertisement_t **list, size_t count);

/* ===========================================================================
 * IMeshCapabilityBroadcaster
 * =========================================================================== */

/* Vtable. BroadcastAsync publishes our advertisement to the mesh. Returns 0 on
 * success, -1 on error. */
typedef struct {
    void *self;
    int (*broadcast)(void *self,
                     const ca_mesh_capability_advertisement_t *ad);
} ca_mesh_capability_broadcaster_t;

/* NullMeshCapabilityBroadcaster — borrowed singleton vtable; broadcast is a
 * no-op that always succeeds. Used when no AetherNet transport is bound. */
ca_mesh_capability_broadcaster_t ca_null_mesh_capability_broadcaster(void);

/* --- Capturing broadcaster (in-memory) ---
 * A working broadcaster that records the last advertisement it was asked to
 * broadcast and a broadcast count. Lets a transportless deployment observe
 * what WOULD go on the wire (and drives the test). */
typedef struct ca_capturing_broadcaster ca_capturing_broadcaster_t;

ca_capturing_broadcaster_t *ca_capturing_broadcaster_create(void);
void ca_capturing_broadcaster_destroy(ca_capturing_broadcaster_t *b);
ca_mesh_capability_broadcaster_t ca_capturing_broadcaster_as_broadcaster(
    ca_capturing_broadcaster_t *b);
int ca_capturing_broadcaster_count(const ca_capturing_broadcaster_t *b);
/* Borrowed pointer to the last captured advertisement (NULL if none). */
const ca_mesh_capability_advertisement_t *ca_capturing_broadcaster_last(
    const ca_capturing_broadcaster_t *b);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_MESH_CAPABILITY_H */
