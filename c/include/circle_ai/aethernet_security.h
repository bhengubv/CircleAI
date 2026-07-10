#ifndef CIRCLE_AI_AETHERNET_SECURITY_H
#define CIRCLE_AI_AETHERNET_SECURITY_H

/*
 * aethernet_security.h — CircleAI.Security.AetherNet bindings (C11 port).
 *
 * The AetherNet-specific security bindings that glue the Aether contracts
 * (aether.h) to the transport-agnostic peer security layer (peer_security.h):
 *
 *   AetherMapper                  — enum translation Aether <-> Peer
 *                                   (ToPeerEventKind / ToPeer/AetherThreatLevel /
 *                                    ToSecurityDirectiveKind)   [AetherMapper.cs]
 *   MeshDirectiveStore            — ISecurityDirectiveConsumer sink + query
 *                                   surface (IsBlocked / GetActiveDirectives),
 *                                   lazy expiry, Release lifts all [MeshDirectiveStore.cs]
 *   MeshSecurityGate              — read-only "is this id blocked?" fast path
 *                                   + Enforce() guard                [MeshSecurityGate.cs]
 *   AetherSecurityBridge          — IAISecurityLayer over SecurityLayerService:
 *                                   subscribes an Aether telemetry feed,
 *                                   translates AetherSecurityEvent -> PeerSecurityEvent,
 *                                   adapts directive consumers + posture
 *                                                               [AetherSecurityBridge.cs]
 *   AetherIntelligenceAdapter     — IAetherIntelligence over PeerIntelligenceService,
 *                                   mapping every peer result to its Aether form
 *                                                               [AetherIntelligenceAdapter.cs]
 *
 * Conventions: ca_ prefix, _t types, opaque create/destroy handles,
 * strdup-owning fields, deep-copy getters, errors via NULL / negative rc.
 * In-memory + deterministic; no pthreads; linear arrays.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "aether.h"
#include "peer_security.h"

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * AetherMapper — static enum translation
 * =========================================================================== */

/* AetherSecurityEventKind -> PeerSecurityEventKind. */
ca_peer_security_event_kind_t ca_aether_mapper_to_peer_event_kind(
    ca_aether_security_event_kind_t kind);
/* AetherThreatLevel <-> PeerThreatLevel. */
ca_peer_threat_level_t ca_aether_mapper_to_peer_threat_level(
    ca_aether_threat_level_t level);
ca_aether_threat_level_t ca_aether_mapper_to_aether_threat_level(
    ca_peer_threat_level_t level);
/* PeerDirectiveKind -> SecurityDirectiveKind. */
ca_aether_security_directive_kind_t ca_aether_mapper_to_security_directive_kind(
    ca_peer_directive_kind_t kind);

/* ===========================================================================
 * MeshDirectiveStore — ISecurityDirectiveConsumer + query surface
 * =========================================================================== */

typedef struct ca_mesh_directive_store ca_mesh_directive_store_t;

/* clock_fn supplies "now" (Unix ms) for lazy expiry — pass an explicit clock
 * for deterministic tests. NULL on OOM. */
typedef int64_t (*ca_mesh_clock_fn)(void *user);
ca_mesh_directive_store_t *ca_mesh_directive_store_create(
    ca_mesh_clock_fn clock_fn, void *clock_user);
void ca_mesh_directive_store_destroy(ca_mesh_directive_store_t *store);

/* OnDirective — the sink. Ignores untargeted directives; a ReleaseNode lifts
 * every Avoid/Quarantine tracked for the node. The store deep-copies. */
void ca_mesh_directive_store_on_directive(
    ca_mesh_directive_store_t *store,
    const ca_aether_security_directive_t *directive);

/* Borrowed vtable view as an ISecurityDirectiveConsumer (for wiring into a
 * layer's SubscribeToDirectives). */
ca_aether_security_directive_consumer_t
ca_mesh_directive_store_as_consumer(ca_mesh_directive_store_t *store);

/*
 * IsBlocked — true when an unexpired Avoid or Quarantine directive is active
 * for node_id. On a true result *out_reason is set to a freshly-allocated copy
 * of the most-recent block's reason (caller frees). On false *out_reason is set
 * to NULL. Sweeps expired entries as it walks.
 */
bool ca_mesh_directive_store_is_blocked(ca_mesh_directive_store_t *store,
                                        const char *node_id,
                                        char **out_reason);

/*
 * GetActiveDirectives — every unexpired directive for node_id, as a
 * freshly-allocated array of deep-copied directive pointers (caller destroys
 * each with ca_aether_security_directive_destroy + frees the array). Returns
 * count; writes NULL/0 when none. Returns SIZE_MAX on OOM.
 */
size_t ca_mesh_directive_store_get_active(
    const ca_mesh_directive_store_t *store, const char *node_id,
    ca_aether_security_directive_t ***out_directives);

/* TrackedNodeCount. */
int ca_mesh_directive_store_tracked_node_count(
    const ca_mesh_directive_store_t *store);

/* ===========================================================================
 * MeshSecurityGate — read-only query view over the store
 * =========================================================================== */

typedef struct ca_mesh_security_gate ca_mesh_security_gate_t;

/* Borrows the store (does not own it). NULL on OOM. */
ca_mesh_security_gate_t *ca_mesh_security_gate_create(
    ca_mesh_directive_store_t *store);
void ca_mesh_security_gate_destroy(ca_mesh_security_gate_t *gate);

/* GateDecision. reason is owned by the decision; free with
 * ca_mesh_gate_decision_free. */
typedef struct {
    bool  is_blocked;
    char *reason; /* owned; "" when allowed */
} ca_mesh_gate_decision_t;

void ca_mesh_gate_decision_free(ca_mesh_gate_decision_t *d);

/* Decide — single-shot decision for user_or_node_id. Writes into *out (owning).
 * Returns 0 on success, -1 on OOM. A null-or-whitespace id yields Allowed. */
int ca_mesh_security_gate_decide(ca_mesh_security_gate_t *gate,
                                 const char *user_or_node_id,
                                 ca_mesh_gate_decision_t *out);

/*
 * Enforce — the one-line guard. Returns true when the id is ALLOWED to proceed.
 * Returns false when BLOCKED (the C# throws MeshSecurityBlockedException;
 * without exceptions the C port signals via the return + fills the blocked
 * reason). On block, *out_reason (if non-NULL) receives a freshly-allocated
 * reason string (caller frees) and *out_message (if non-NULL) receives the
 * full "Mesh has blocked '<id>': <reason>" message (caller frees).
 */
bool ca_mesh_security_gate_enforce(ca_mesh_security_gate_t *gate,
                                   const char *user_or_node_id,
                                   char **out_reason, char **out_message);

/* ===========================================================================
 * AetherSecurityBridge — IAISecurityLayer over SecurityLayerService
 * =========================================================================== */

typedef struct ca_aether_security_bridge ca_aether_security_bridge_t;

/* Borrows the security layer service (does not own it). NULL on OOM / NULL
 * layer. */
ca_aether_security_bridge_t *ca_aether_security_bridge_create(
    ca_security_layer_service_t *layer);
void ca_aether_security_bridge_destroy(ca_aether_security_bridge_t *bridge);

/* Borrowed vtable view as an IAISecurityLayer. start() subscribes the given
 * Aether telemetry feed (translating AetherSecurityEvent -> PeerSecurityEvent
 * and node-exit -> HandlePeerLeft) and starts the layer; stop() unsubscribes
 * and stops it; subscribe_to_directives adapts a PeerDirective back to a
 * SecurityDirective before delivery; get_posture maps the peer posture. */
ca_aether_ai_security_layer_t ca_aether_security_bridge_as_layer(
    ca_aether_security_bridge_t *bridge);

/* ===========================================================================
 * AetherIntelligenceAdapter — IAetherIntelligence over PeerIntelligenceService
 * =========================================================================== */

typedef struct ca_aether_intelligence_adapter ca_aether_intelligence_adapter_t;

/* Borrows the peer intelligence service. now_fn supplies the clock passed to
 * AssessThreat (peer assess_threat takes now_ms). NULL on OOM / NULL inner. */
typedef int64_t (*ca_aether_intel_now_fn)(void *user);
ca_aether_intelligence_adapter_t *ca_aether_intelligence_adapter_create(
    ca_peer_intelligence_service_t *inner, ca_aether_intel_now_fn now_fn,
    void *now_user);
void ca_aether_intelligence_adapter_destroy(
    ca_aether_intelligence_adapter_t *adapter);

/* Borrowed vtable view as an IAetherIntelligence. */
ca_aether_intelligence_t ca_aether_intelligence_adapter_as_intelligence(
    ca_aether_intelligence_adapter_t *adapter);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_AETHERNET_SECURITY_H */
