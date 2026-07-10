#ifndef CIRCLE_AI_NETWORKING_H
#define CIRCLE_AI_NETWORKING_H

/*
 * networking.h — CircleAI.Networking core abstraction (C11 port).
 *
 * The unified transport abstraction the 10 concrete transports (HTTP, WebSocket,
 * gRPC, MQTT, TCP, UDP, WiFi, Bluetooth, NearLink, Aether, DTN, LocalStore)
 * implement. Ports the public enums, immutable records, and interfaces of
 * CircleAI.Networking 1:1:
 *
 *   Enums     : TransportKind, ConnectivityState, MessagePriority, PeerRole
 *               (SyncDeliveryMode already lives in sync.h — reused, not redefined)
 *   Records   : NetworkPayload, NetworkContext, PeerInfo
 *   Policy    : INetworkPolicy (vtable), DefaultNetworkPolicy, NetworkPolicyBuilder
 *   Interfaces: INetworkTransport, IMeshNetwork, IMessageChannel,
 *               IConnectivityMonitor, ITransportSelector (vtable structs)
 *
 * Interfaces are modelled as vtable structs (self + function pointers). Because
 * nothing may be a stub, the module ships working, deterministic, in-memory
 * implementations of every stateful interface — the seam behind which a real
 * socket is injected:
 *
 *   - ca_loopback_transport : INetworkTransport whose SendAsync enqueues into an
 *                             unbounded FIFO that ReceiveAsync drains (a socket
 *                             stand-in; the wire is injected by swapping this).
 *   - ca_mem_mesh           : IMeshNetwork over a fixed local id + peer/health snapshot.
 *   - ca_mem_channel        : IMessageChannel pub/sub with UNBOUNDED fan-out
 *                             buffering (messages published before a subscriber
 *                             attaches are retained until read, mirroring an
 *                             unbounded System.Threading.Channels channel).
 *   - ca_mem_connectivity   : IConnectivityMonitor; WatchAsync drains an unbounded
 *                             queue of pushed NetworkContext snapshots.
 *   - ca_default_selector   : ITransportSelector implementing the documented
 *                             cascade gRPC->WebSocket->HTTP->MQTT->TCP->UDP->WiFi->
 *                             Bluetooth->NearLink->Aether->DTN->LocalStore, filtered
 *                             by context availability + policy, honouring
 *                             ForceTransport and MeshFirst.
 *
 * "Async" streams become drainable cursors: a *_receive_next(out) call returns
 * true and fills *out with the next item, false when currently drained. This is
 * the deterministic, single-threaded analogue of IAsyncEnumerable — no pthreads.
 *
 * Conventions: ca_ prefix, _t types, opaque create/destroy handles, strdup-owning
 * fields with matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX.
 * Linear arrays, no hashtable, no pthreads. Timestamps are Unix ms UTC.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "sync.h"   /* ca_sync_delivery_mode_t (SyncDeliveryMode) reused */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * Enums
 * =========================================================================== */

/* TransportKind — ordered exactly as the C# enum (ordinal values are load-bearing
 * for the default selector cascade). */
typedef enum {
    CA_TRANSPORT_HTTP       = 0,
    CA_TRANSPORT_WEBSOCKET  = 1,
    CA_TRANSPORT_GRPC       = 2,
    CA_TRANSPORT_MQTT       = 3,
    CA_TRANSPORT_TCP        = 4,
    CA_TRANSPORT_UDP        = 5,
    CA_TRANSPORT_WIFI       = 6,   /* WiFi Direct / mDNS / LAN — no Aether */
    CA_TRANSPORT_BLUETOOTH  = 7,   /* raw BLE GATT — no Aether */
    CA_TRANSPORT_NEARLINK   = 8,   /* Huawei SLE / HarmonyOS — no Aether */
    CA_TRANSPORT_AETHER     = 9,   /* full Aether mesh (Signal E2E + AODV + SOS) */
    CA_TRANSPORT_DTN        = 10,  /* 72hr store-and-forward over any transport */
    CA_TRANSPORT_LOCAL_STORE = 11  /* offline queue — no live path at all */
} ca_transport_kind_t;

/* ConnectivityState */
typedef enum {
    CA_CONNECTIVITY_ONLINE     = 0,
    CA_CONNECTIVITY_LOCAL_ONLY = 1,
    CA_CONNECTIVITY_MESH_ONLY  = 2,
    CA_CONNECTIVITY_OFFLINE    = 3
} ca_connectivity_state_t;

/* MessagePriority */
typedef enum {
    CA_MSG_PRIORITY_LOW       = 0,
    CA_MSG_PRIORITY_NORMAL    = 1,
    CA_MSG_PRIORITY_HIGH      = 2,
    CA_MSG_PRIORITY_URGENT    = 3,
    CA_MSG_PRIORITY_EMERGENCY = 4
} ca_message_priority_t;

/* PeerRole */
typedef enum {
    CA_PEER_ROLE_PEER   = 0,
    CA_PEER_ROLE_RELAY  = 1,
    CA_PEER_ROLE_BRIDGE = 2,
    CA_PEER_ROLE_SINK   = 3
} ca_peer_role_t;

/* ===========================================================================
 * NetworkPayload — immutable envelope for one message/data unit.
 *
 * Mirrors the C# record: transports must not mutate it; create a new one instead.
 * Ttl is optional (has_ttl gate; TimeSpan stored as milliseconds). Metadata is an
 * owned array of owned key/value pairs (IReadOnlyDictionary<string,string>).
 * =========================================================================== */

typedef struct {
    char *key;   /* owned */
    char *value; /* owned */
} ca_net_metadata_pair_t;

typedef struct {
    char                   *id;             /* owned, non-null */
    char                   *source_id;      /* owned, may be NULL */
    char                   *destination_id; /* owned, may be NULL */
    uint8_t                *data;           /* owned copy of the byte payload */
    size_t                  data_len;
    ca_message_priority_t   priority;
    bool                    has_ttl;
    int64_t                 ttl_ms;         /* TimeSpan Ttl in ms (valid iff has_ttl) */
    char                   *content_type;   /* owned, non-null */
    ca_net_metadata_pair_t *metadata;       /* owned array */
    size_t                  metadata_count;
    int64_t                 created_at_ms;  /* DateTimeOffset CreatedAt (Unix ms) */
} ca_network_payload_t;

/* Full constructor (deep-copies every field). source_id/destination_id may be
 * NULL; content_type NULL is normalised to "application/octet-stream" only via
 * ca_network_payload_create() — this raw constructor keeps content_type as
 * given (NULL -> empty). NULL on OOM. */
ca_network_payload_t *ca_network_payload_new(
    const char *id, const char *source_id, const char *destination_id,
    const uint8_t *data, size_t data_len, ca_message_priority_t priority,
    bool has_ttl, int64_t ttl_ms, const char *content_type,
    const ca_net_metadata_pair_t *metadata, size_t metadata_count,
    int64_t created_at_ms);

/* NetworkPayload.Create(...) — new Guid("N") id (32 lowercase hex, no dashes),
 * SourceId null, empty metadata, CreatedAt = now_ms. content_type NULL defaults
 * to "application/octet-stream"; has_ttl gates ttl. gen32hex writes a 32-char id
 * (caller may pass NULL to use an internal counter-free deterministic scheme is
 * NOT used — the id must be supplied by ca_net_new_guid_n for parity). */
ca_network_payload_t *ca_network_payload_create(
    const uint8_t *data, size_t data_len, const char *destination_id,
    ca_message_priority_t priority, const char *content_type,
    bool has_ttl, int64_t ttl_ms, int64_t created_at_ms, const char *id);

void ca_network_payload_destroy(ca_network_payload_t *p);
ca_network_payload_t *ca_network_payload_copy(const ca_network_payload_t *p);
/* Lookup a metadata value (ordinal). Borrowed pointer or NULL. */
const char *ca_network_payload_metadata(const ca_network_payload_t *p,
                                        const char *key);

/* Generate a Guid "N" format string (32 lowercase hex chars + NUL) into out[33].
 * Deterministic PRNG is avoided; uses the platform CSPRNG when available (matches
 * Guid.NewGuid entropy intent). Returns out. */
char *ca_net_new_guid_n(char out[33]);

/* ===========================================================================
 * NetworkContext — snapshot of current connectivity.
 *
 * SignalStrengthDbm / EstimatedBandwidthBps / LatencyMs are optional (has_* gate).
 * AvailableTransports is an owned array. Mirrors the C# record + the static
 * NetworkContext.Offline value (via ca_network_context_offline).
 * =========================================================================== */

typedef struct {
    ca_connectivity_state_t state;
    ca_transport_kind_t     preferred_transport;
    ca_transport_kind_t    *available_transports; /* owned array */
    size_t                  available_count;
    bool                    has_signal_strength;
    int                     signal_strength_dbm;
    bool                    has_bandwidth;
    int64_t                 estimated_bandwidth_bps;
    bool                    has_latency;
    int64_t                 latency_ms;
    int                     nearby_peer_count;
    int64_t                 snapshot_at_ms;
} ca_network_context_t;

ca_network_context_t *ca_network_context_new(
    ca_connectivity_state_t state, ca_transport_kind_t preferred_transport,
    const ca_transport_kind_t *available_transports, size_t available_count,
    bool has_signal_strength, int signal_strength_dbm,
    bool has_bandwidth, int64_t estimated_bandwidth_bps,
    bool has_latency, int64_t latency_ms,
    int nearby_peer_count, int64_t snapshot_at_ms);

void ca_network_context_destroy(ca_network_context_t *c);
ca_network_context_t *ca_network_context_copy(const ca_network_context_t *c);
/* NetworkContext.Offline — Offline state, LocalStore preferred, no transports,
 * all optionals null, 0 peers, snapshot = now_ms. */
ca_network_context_t *ca_network_context_offline(int64_t now_ms);
/* True if `t` appears in AvailableTransports. */
bool ca_network_context_supports(const ca_network_context_t *c,
                                 ca_transport_kind_t t);

/* ===========================================================================
 * PeerInfo — a discovered peer on any transport.
 *
 * DisplayName / SignalStrengthDbm optional; SupportedTransports owned array.
 * =========================================================================== */

typedef struct {
    char                *node_id;              /* owned, non-null */
    char                *display_name;         /* owned, may be NULL */
    ca_transport_kind_t *supported_transports; /* owned array */
    size_t               supported_count;
    ca_peer_role_t       role;
    bool                 has_signal_strength;
    int                  signal_strength_dbm;
    int64_t              last_seen_ms;
} ca_peer_info_t;

ca_peer_info_t *ca_peer_info_new(
    const char *node_id, const char *display_name,
    const ca_transport_kind_t *supported_transports, size_t supported_count,
    ca_peer_role_t role, bool has_signal_strength, int signal_strength_dbm,
    int64_t last_seen_ms);

void ca_peer_info_destroy(ca_peer_info_t *p);
ca_peer_info_t *ca_peer_info_copy(const ca_peer_info_t *p);

/* ===========================================================================
 * INetworkPolicy (vtable) + DefaultNetworkPolicy + NetworkPolicyBuilder
 * =========================================================================== */

/* INetworkPolicy — rules applied before choosing a transport.
 *   permits(t, payload)   : is transport t allowed for this payload?
 *   force_transport(&out) : ForceTransport — true + fills *out when forced,
 *                           false when null.
 *   mesh_first / offline_queue_enabled / allow_cloud_transports : the flags.
 * `payload` passed to permits is borrowed and may be NULL (some policies ignore
 * it, exactly like the C# `_` discard). */
typedef struct {
    void *self;
    bool (*permits)(void *self, ca_transport_kind_t t,
                    const ca_network_payload_t *payload);
    bool (*force_transport)(void *self, ca_transport_kind_t *out);
    bool (*mesh_first)(void *self);
    bool (*offline_queue_enabled)(void *self);
    bool (*allow_cloud_transports)(void *self);
} ca_network_policy_t;

/* DefaultNetworkPolicy.Instance — permissive: permits everything, no force,
 * MeshFirst=false, OfflineQueueEnabled=true, AllowCloudTransports=true.
 * Borrowed singleton vtable (stateless; valid for program lifetime). */
ca_network_policy_t ca_default_network_policy(void);

/* NetworkPolicyBuilder — fluent builder for INetworkPolicy. Chainable setters
 * return the same builder. Build() produces an owning policy handle whose vtable
 * view is obtained via ca_network_policy_impl_as_policy(); destroy with
 * ca_network_policy_impl_destroy() (outlive any vtable view taken from it). */
typedef struct ca_network_policy_builder ca_network_policy_builder_t;
typedef struct ca_network_policy_impl    ca_network_policy_impl_t;

ca_network_policy_builder_t *ca_network_policy_builder_create(void);
void ca_network_policy_builder_destroy(ca_network_policy_builder_t *b);

ca_network_policy_builder_t *ca_network_policy_builder_mesh_first(
    ca_network_policy_builder_t *b);
ca_network_policy_builder_t *ca_network_policy_builder_no_cloud(
    ca_network_policy_builder_t *b);
ca_network_policy_builder_t *ca_network_policy_builder_disable_queue(
    ca_network_policy_builder_t *b);
ca_network_policy_builder_t *ca_network_policy_builder_force(
    ca_network_policy_builder_t *b, ca_transport_kind_t t);
/* Allow(params ...) — add each kind to the allow-set (idempotent per kind). */
ca_network_policy_builder_t *ca_network_policy_builder_allow(
    ca_network_policy_builder_t *b, const ca_transport_kind_t *kinds,
    size_t count);

/* Build the policy. NULL on OOM. The builder may be destroyed afterwards; the
 * returned impl is independent. */
ca_network_policy_impl_t *ca_network_policy_builder_build(
    const ca_network_policy_builder_t *b);
void ca_network_policy_impl_destroy(ca_network_policy_impl_t *p);
/* Borrowed vtable view (valid for the impl's lifetime). */
ca_network_policy_t ca_network_policy_impl_as_policy(ca_network_policy_impl_t *p);

/* ===========================================================================
 * INetworkTransport (vtable) + in-memory loopback implementation
 * =========================================================================== */

/* INetworkTransport — unified send/receive for a single transport kind.
 *   kind()          : the TransportKind this transport serves.
 *   is_available()  : IsAvailable.
 *   start()/stop()  : StartAsync/StopAsync (return 0 ok, -1 error).
 *   send(payload)   : SendAsync — borrows payload (deep-copies what it retains);
 *                     returns 0 ok, -1 error.
 *   receive_next(&out): drain one payload from ReceiveAsync into a freshly-owned
 *                     *out (caller destroys). Returns true if one was produced,
 *                     false when currently drained. */
typedef struct {
    void *self;
    ca_transport_kind_t (*kind)(void *self);
    bool (*is_available)(void *self);
    int  (*start)(void *self);
    int  (*stop)(void *self);
    int  (*send)(void *self, const ca_network_payload_t *payload);
    bool (*receive_next)(void *self, ca_network_payload_t **out);
} ca_network_transport_t;

/* In-memory loopback transport: a socket stand-in. SendAsync enqueues a deep
 * copy into an UNBOUNDED FIFO; ReceiveAsync drains it in FIFO order. Available
 * only while started (Send/Receive before StartAsync fail / drain empty, matching
 * a transport that must be started). This is the seam a real socket replaces. */
typedef struct ca_loopback_transport ca_loopback_transport_t;

ca_loopback_transport_t *ca_loopback_transport_create(ca_transport_kind_t kind);
void ca_loopback_transport_destroy(ca_loopback_transport_t *t);
/* Borrowed vtable view (valid for the transport's lifetime). */
ca_network_transport_t ca_loopback_transport_as_transport(
    ca_loopback_transport_t *t);
/* Number of payloads currently queued (undelivered). */
size_t ca_loopback_transport_pending(const ca_loopback_transport_t *t);
bool ca_loopback_transport_is_started(const ca_loopback_transport_t *t);

/* ===========================================================================
 * IMeshNetwork (vtable) + in-memory implementation
 * =========================================================================== */

/* IMeshNetwork — topology, node identity, mesh health.
 *   local_node_id()          : borrowed LocalNodeId.
 *   peer_ids(&out,&count)     : GetPeerIdsAsync — allocates an owned array of
 *                              owned id strings into *out (count in *count); the
 *                              caller frees each string then the array. On error
 *                              *out=NULL, *count=SIZE_MAX.
 *   mesh_health(&out)         : GetMeshHealthAsync — writes a freshly-owned
 *                              NetworkContext* into *out (caller destroys);
 *                              returns 0 ok / -1 error. */
typedef struct {
    void *self;
    const char *(*local_node_id)(void *self);
    int  (*peer_ids)(void *self, char ***out, size_t *count);
    int  (*mesh_health)(void *self, ca_network_context_t **out);
} ca_mesh_network_t;

/* In-memory mesh: a fixed local id, a mutable peer-id set, and a settable health
 * snapshot (defaults to Offline until set). */
typedef struct ca_mem_mesh ca_mem_mesh_t;

ca_mem_mesh_t *ca_mem_mesh_create(const char *local_node_id);
void ca_mem_mesh_destroy(ca_mem_mesh_t *m);
/* Add a peer id (deep copy; duplicates ignored). Returns 0 ok / -1 error. */
int ca_mem_mesh_add_peer(ca_mem_mesh_t *m, const char *peer_id);
/* Remove a peer id (no-op if absent). */
void ca_mem_mesh_remove_peer(ca_mem_mesh_t *m, const char *peer_id);
/* Set the health snapshot (deep copy; replaces any prior). Returns 0 / -1. */
int ca_mem_mesh_set_health(ca_mem_mesh_t *m, const ca_network_context_t *health);
/* Borrowed vtable view. */
ca_mesh_network_t ca_mem_mesh_as_mesh(ca_mem_mesh_t *m);

/* ===========================================================================
 * IMessageChannel (vtable) + in-memory pub/sub implementation
 * =========================================================================== */

/* IMessageChannel — typed message delivery over any transport. The C# generic
 * <T> is erased to (payload bytes + content_type) here: send serialises the
 * caller's opaque message as bytes tagged with a content-type discriminator;
 * receive_next hands back the next queued (destination, bytes, content_type)
 * triple. This preserves the routing + typed-dispatch contract without a runtime
 * type system.
 *
 *   send(dest, data, len, content_type) : SendAsync<T>(destinationId, message).
 *                                         Fans the message out to every current
 *                                         subscriber's queue AND retains it in a
 *                                         backlog so a subscriber that attaches
 *                                         LATER still receives it (unbounded
 *                                         channel semantics). Returns 0 / -1.
 *   receive_next(sub, &item)           : drain one item for a subscription. */
typedef struct {
    void *self;
    int (*send)(void *self, const char *destination_id, const uint8_t *data,
                size_t len, const char *content_type);
} ca_message_channel_t;

/* One delivered message (owned; caller frees via ca_channel_message_destroy). */
typedef struct {
    char    *destination_id; /* owned, may be NULL */
    uint8_t *data;           /* owned */
    size_t   len;
    char    *content_type;   /* owned, non-null */
} ca_channel_message_t;

void ca_channel_message_destroy(ca_channel_message_t *m);

typedef struct ca_mem_channel      ca_mem_channel_t;
typedef struct ca_mem_channel_sub  ca_mem_channel_sub_t;

ca_mem_channel_t *ca_mem_channel_create(void);
void ca_mem_channel_destroy(ca_mem_channel_t *c);
/* Borrowed vtable view. */
ca_message_channel_t ca_mem_channel_as_channel(ca_mem_channel_t *c);

/* Subscribe a receiver (ReceiveAsync<T>). Returns an owned subscription cursor;
 * NULL on OOM. Any message SENT BEFORE this call is still delivered to the new
 * subscription (unbounded backlog), so subscribe-vs-first-publish never loses a
 * message. Destroy with ca_mem_channel_unsubscribe. */
ca_mem_channel_sub_t *ca_mem_channel_subscribe(ca_mem_channel_t *c);
void ca_mem_channel_unsubscribe(ca_mem_channel_t *c, ca_mem_channel_sub_t *sub);
/* Drain one message into *out (freshly owned). true if produced, false if drained. */
bool ca_mem_channel_receive_next(ca_mem_channel_sub_t *sub,
                                 ca_channel_message_t *out);
/* Messages still pending for this subscription. */
size_t ca_mem_channel_sub_pending(const ca_mem_channel_sub_t *sub);

/* ===========================================================================
 * IConnectivityMonitor (vtable) + in-memory implementation
 * =========================================================================== */

/* IConnectivityMonitor — observes connectivity and emits changes.
 *   current_state()  : CurrentState.
 *   snapshot(&out)    : GetSnapshot — freshly-owned NetworkContext* into *out
 *                      (caller destroys); 0 / -1.
 * WatchAsync is exposed via subscription cursors (see below): each pushed
 * snapshot is fanned out to every watcher's UNBOUNDED queue. */
typedef struct {
    void *self;
    ca_connectivity_state_t (*current_state)(void *self);
    int (*snapshot)(void *self, ca_network_context_t **out);
} ca_connectivity_monitor_t;

typedef struct ca_mem_connectivity     ca_mem_connectivity_t;
typedef struct ca_mem_connectivity_sub ca_mem_connectivity_sub_t;

/* Create with an initial snapshot (deep-copied; becomes the current snapshot and
 * seeds CurrentState). NULL on OOM / NULL initial. */
ca_mem_connectivity_t *ca_mem_connectivity_create(
    const ca_network_context_t *initial);
void ca_mem_connectivity_destroy(ca_mem_connectivity_t *m);
/* Borrowed vtable view. */
ca_connectivity_monitor_t ca_mem_connectivity_as_monitor(
    ca_mem_connectivity_t *m);
/* Push a new snapshot: updates current state/snapshot AND enqueues a copy to
 * every watcher (WatchAsync emission). Deep-copied. Returns 0 / -1. */
int ca_mem_connectivity_push(ca_mem_connectivity_t *m,
                             const ca_network_context_t *ctx);

/* Start watching (WatchAsync). Returns an owned watcher cursor (NULL on OOM).
 * Only snapshots pushed AFTER this subscribe are delivered to it. */
ca_mem_connectivity_sub_t *ca_mem_connectivity_watch(ca_mem_connectivity_t *m);
void ca_mem_connectivity_unwatch(ca_mem_connectivity_t *m,
                                 ca_mem_connectivity_sub_t *sub);
/* Drain one emitted snapshot into *out (freshly owned). true if produced. */
bool ca_mem_connectivity_watch_next(ca_mem_connectivity_sub_t *sub,
                                    ca_network_context_t **out);

/* ===========================================================================
 * ITransportSelector (vtable) + default cascade implementation
 * =========================================================================== */

/* ITransportSelector — chooses the best transport for a payload+context.
 *   select_best(payload, context)          : the single best TransportKind.
 *   get_cascade(payload, context, &count)  : the ordered fallback list; allocates
 *                                            an owned array (length in *count). On
 *                                            error returns NULL with *count=SIZE_MAX.
 * `payload` and `context` are borrowed. */
typedef struct {
    void *self;
    ca_transport_kind_t (*select_best)(void *self,
                                       const ca_network_payload_t *payload,
                                       const ca_network_context_t *context);
    ca_transport_kind_t *(*get_cascade)(void *self,
                                        const ca_network_payload_t *payload,
                                        const ca_network_context_t *context,
                                        size_t *count);
} ca_transport_selector_t;

/* Default selector. Cascade base order (matches the interface doc):
 *   gRPC -> WebSocket -> HTTP -> MQTT -> TCP -> UDP -> WiFi -> Bluetooth ->
 *   NearLink -> Aether -> DTN -> LocalStore
 * GetCascade rules (deterministic realisation of the DefaultNetworkPolicy seam):
 *   1. If policy.ForceTransport is set, the cascade is exactly [forced] (only if
 *      permitted) else [] then LocalStore appended as the terminal fallback.
 *   2. Otherwise walk the base order; keep a kind iff:
 *        policy.Permits(kind, payload) AND
 *        (context has no AvailableTransports listed, treat all as available; else
 *         the kind is in AvailableTransports) — LocalStore/DTN are ALWAYS eligible
 *        as terminal fallbacks regardless of availability, since they need no live
 *        path (offline queue / store-and-forward), subject to policy.Permits.
 *      LocalStore is only appended if OfflineQueueEnabled (else DTN is the floor;
 *      if neither is permitted, the last permitted live kind is the floor, and if
 *      nothing is permitted the cascade is empty).
 *   3. If MeshFirst, the mesh/local kinds (WiFi, Bluetooth, NearLink, Aether) are
 *      hoisted to the front (preserving their relative order) ahead of the cloud
 *      kinds.
 * SelectBest returns the first cascade entry, or LocalStore when the cascade is
 * empty (there is always a store-and-forward floor when the queue is enabled).
 *
 * Borrows the policy vtable (does not own it); the policy must outlive the
 * selector. Pass ca_default_network_policy() for the permissive default. */
typedef struct ca_default_selector ca_default_selector_t;

ca_default_selector_t *ca_default_selector_create(ca_network_policy_t policy);
void ca_default_selector_destroy(ca_default_selector_t *s);
/* Borrowed vtable view. */
ca_transport_selector_t ca_default_selector_as_selector(ca_default_selector_t *s);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_NETWORKING_H */
