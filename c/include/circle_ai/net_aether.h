#ifndef CIRCLE_AI_NET_AETHER_H
#define CIRCLE_AI_NET_AETHER_H

/*
 * net_aether.h — CircleAI.Networking.AetherNet (C11 port).
 *
 * The AetherNet mesh transport binding. Ports CircleAI.Networking.AetherNet 1:1:
 *
 *   Enum      : AetherPeerKind
 *   Records   : AetherPeer, AetherHopTelemetry, AetherPacketSummary
 *   Registry  : InMemoryAetherNetRegistry (peers + hop telemetry + packet log)
 *   IAetherContext : the injected presence/capability seam (AetherInstallLevel +
 *                    availability flags) that AetherNetworkTransport queries.
 *   Transport : AetherNetworkTransport  — INetworkTransport over the Aether mesh.
 *               Backed by an IAetherContext (IsAvailable) + an UNBOUNDED inbound
 *               FIFO drained by ReceiveAsync. SendAsync is the bridge to the
 *               aether-protocol engine (a completed no-op in this in-memory port,
 *               exactly like the C# which delegates routing to aether-protocol).
 *   Discovery : AetherPeerDiscovery — IPeerDiscovery over Aether presence beacons.
 *               DiscoverAsync yields nothing until wired to IAetherTelemetry;
 *               AnnounceAsync is a completed no-op (mirrors the C# shell).
 *   SyncChannel : AetherSyncChannel — ISyncChannel over Aether DTN store-and-forward.
 *               Tracks last sequence per (ownerId, domainKey); PushDelta / receive
 *               are the completed bridges the C# defines.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Linear
 * arrays, no hashtable, no pthreads. Timestamps are Unix ms UTC, passed in.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "networking.h"   /* ca_network_transport_t, ca_network_payload_t, ... */
#include "net_sync_delta.h" /* ca_net_sync_delta_t (Networking.SyncDelta) */
#include "aether.h"       /* ca_aether_context_t (IAetherContext) — reused */

#ifdef __cplusplus
extern "C" {
#endif

/* ===========================================================================
 * AetherPeerKind
 * =========================================================================== */

typedef enum {
    CA_AETHER_PEER_PHONE   = 0,
    CA_AETHER_PEER_TABLET  = 1,
    CA_AETHER_PEER_LAPTOP  = 2,
    CA_AETHER_PEER_DESKTOP = 3,
    CA_AETHER_PEER_EDGE    = 4,
    CA_AETHER_PEER_VEHICLE = 5,
    CA_AETHER_PEER_IOT     = 6
} ca_aether_peer_kind_t;

/* ===========================================================================
 * AetherPeer — a mesh peer descriptor.
 * AdvertisedCapabilities is an owned array of owned strings.
 * =========================================================================== */

typedef struct {
    char                  *peer_id;        /* owned, non-null */
    ca_aether_peer_kind_t  kind;
    char                  *friendly_name;  /* owned, may be NULL */
    char                 **capabilities;   /* owned array of owned strings */
    size_t                 capability_count;
} ca_aether_peer_t;

ca_aether_peer_t *ca_aether_peer_new(
    const char *peer_id, ca_aether_peer_kind_t kind, const char *friendly_name,
    const char *const *capabilities, size_t capability_count);
void ca_aether_peer_destroy(ca_aether_peer_t *p);
ca_aether_peer_t *ca_aether_peer_copy(const ca_aether_peer_t *p);

/* ===========================================================================
 * AetherHopTelemetry — per-hop round-trip sample.
 * =========================================================================== */

typedef struct {
    char   *peer_id;         /* owned */
    int     hop_count;
    double  round_trip_ms;
    int64_t at_unix_ms;
} ca_aether_hop_telemetry_t;

/* ===========================================================================
 * AetherPacketSummary — a single relayed packet record.
 * =========================================================================== */

typedef struct {
    char   *packet_id;   /* owned */
    char   *from_peer;   /* owned */
    char   *to_peer;     /* owned */
    int     bytes;
    char   *packet_kind; /* owned */
    int64_t at_unix_ms;
} ca_aether_packet_summary_t;

void ca_aether_packet_summary_free(ca_aether_packet_summary_t *p);
/* Free an array of packet summaries returned by RecentPackets. */
void ca_aether_packet_summary_free_array(ca_aether_packet_summary_t *arr,
                                         size_t count);

/* ===========================================================================
 * InMemoryAetherNetRegistry — peers + hop telemetry + packet log.
 *
 * Register uses last-writer-wins keyed by PeerId (a ConcurrentDictionary). Peers
 * returns a snapshot ordered by PeerId (ordinal). RecordHop / RecordPacket append.
 * RecentPackets returns the newest `limit` ordered by AtUtc descending.
 * AvgRoundTripMs averages telemetry for a peer (0 when none). TotalBytesBetween
 * sums packet bytes for a (fromPeer, toPeer) pair.
 * =========================================================================== */

typedef struct ca_aethernet_registry ca_aethernet_registry_t;

ca_aethernet_registry_t *ca_aethernet_registry_create(void);
void ca_aethernet_registry_destroy(ca_aethernet_registry_t *r);

/* Register(peer) — deep copy, LWW by PeerId. Returns 0 / -1 (OOM or NULL). */
int ca_aethernet_registry_register(ca_aethernet_registry_t *r,
                                   const ca_aether_peer_t *peer);
/* GetPeer(id) — freshly-owned copy or NULL if absent. */
ca_aether_peer_t *ca_aethernet_registry_get_peer(
    const ca_aethernet_registry_t *r, const char *peer_id);
/* Peers — owned array of owned copies ordered by PeerId. On error *out=NULL,
 * *count=SIZE_MAX. Empty => *out=NULL,*count=0. Free each then the array. */
int ca_aethernet_registry_peers(const ca_aethernet_registry_t *r,
                                ca_aether_peer_t ***out, size_t *count);
/* RecordHop — append a telemetry sample (deep copy). Returns 0 / -1. */
int ca_aethernet_registry_record_hop(ca_aethernet_registry_t *r,
                                     const char *peer_id, int hop_count,
                                     double round_trip_ms, int64_t at_unix_ms);
/* RecordPacket — append a packet summary (deep copy). Returns 0 / -1. */
int ca_aethernet_registry_record_packet(ca_aethernet_registry_t *r,
                                        const char *packet_id,
                                        const char *from_peer,
                                        const char *to_peer, int bytes,
                                        const char *packet_kind,
                                        int64_t at_unix_ms);
/* RecentPackets(limit) — newest `limit` ordered by AtUtc descending. Returns an
 * owned array (free with ca_aether_packet_summary_free_array). On error *count=
 * SIZE_MAX. */
ca_aether_packet_summary_t *ca_aethernet_registry_recent_packets(
    const ca_aethernet_registry_t *r, int limit, size_t *count);
/* AvgRoundTripMs(peerId) — mean round trip for the peer (0.0 when none). */
double ca_aethernet_registry_avg_round_trip_ms(
    const ca_aethernet_registry_t *r, const char *peer_id);
/* TotalBytesBetween(from,to) — sum of packet bytes on that directed edge. */
int ca_aethernet_registry_total_bytes_between(
    const ca_aethernet_registry_t *r, const char *from_peer,
    const char *to_peer);

/* ===========================================================================
 * IAetherContext — presence + capability seam (injected).
 *
 * REUSED from aether.h: the ca_aether_context_t vtable (InstallLevel /
 * IsAvailable / RuntimeVersion / MinimumRequired / IsSufficient / RequiresAuth /
 * IsEnabled) and its in-memory implementation ca_aether_context_impl_* already
 * port CircleAI.Aether.IAetherContext 1:1. AetherNetworkTransport / Discovery /
 * SyncChannel below all take a ca_aether_context_t by value (borrowed vtable);
 * the transport uses is_available(). Construct one with
 * ca_aether_context_impl_create() + ca_aether_context_impl_as_context().
 * =========================================================================== */

/* ===========================================================================
 * AetherNetworkTransport — INetworkTransport over the Aether mesh.
 *
 * Kind == Aether. IsAvailable mirrors the injected context. StartAsync is a
 * no-op; StopAsync completes the inbound channel (subsequent receive drains
 * remaining then reports drained). SendAsync bridges to the aether-protocol
 * engine — a completed no-op here (matching the C# which delegates routing).
 * ReceiveAsync drains the UNBOUNDED inbound FIFO; a test/host injects inbound
 * traffic with ca_aether_transport_inject.
 * =========================================================================== */

typedef struct ca_aether_transport ca_aether_transport_t;

ca_aether_transport_t *ca_aether_transport_create(ca_aether_context_t context);
void ca_aether_transport_destroy(ca_aether_transport_t *t);
/* Borrowed INetworkTransport vtable view (valid for the transport's lifetime). */
ca_network_transport_t ca_aether_transport_as_transport(
    ca_aether_transport_t *t);
/* Inject an inbound payload (the mesh-received seam). Deep-copied; enqueued into
 * the unbounded inbound FIFO iff the channel is open. Returns 0 / -1 (closed,
 * OOM, or NULL). */
int ca_aether_transport_inject(ca_aether_transport_t *t,
                               const ca_network_payload_t *payload);
/* Number of inbound payloads currently queued. */
size_t ca_aether_transport_pending(const ca_aether_transport_t *t);

/* ===========================================================================
 * AetherPeerDiscovery — IPeerDiscovery over Aether presence beacons.
 *
 * DiscoverAsync yields nothing (until wired to IAetherTelemetry NodeJoined);
 * discover_next therefore always reports drained. AnnounceAsync is a completed
 * no-op. Announced peers are retained so a host can inspect what was announced.
 * =========================================================================== */

typedef struct ca_aether_discovery ca_aether_discovery_t;

ca_aether_discovery_t *ca_aether_discovery_create(ca_aether_context_t context);
void ca_aether_discovery_destroy(ca_aether_discovery_t *d);
/* DiscoverAsync — drain one discovered peer. Always false (empty stream). */
bool ca_aether_discovery_discover_next(ca_aether_discovery_t *d,
                                       ca_peer_info_t **out);
/* AnnounceAsync(localInfo) — completed no-op; retains a copy. Returns 0 / -1. */
int ca_aether_discovery_announce(ca_aether_discovery_t *d,
                                 const ca_peer_info_t *local_info);
/* The most recently announced PeerInfo (borrowed) or NULL if never announced. */
const ca_peer_info_t *ca_aether_discovery_last_announced(
    const ca_aether_discovery_t *d);

/* ===========================================================================
 * AetherSyncChannel — ISyncChannel over Aether DTN store-and-forward.
 *
 * PushDeltaAsync bridges to the aether-protocol DTN engine (completed no-op).
 * ReceiveDeltasAsync yields nothing until wired to the DTN delivery queue.
 * GetLastSequenceAsync returns the tracked last sequence for (ownerId,domainKey),
 * or 0 when unseen. SetSequence lets a host seed the map (the C# tracks it
 * internally as bundles are custody-transferred).
 * =========================================================================== */

typedef struct ca_aether_sync_channel ca_aether_sync_channel_t;

ca_aether_sync_channel_t *ca_aether_sync_channel_create(
    ca_aether_context_t context);
void ca_aether_sync_channel_destroy(ca_aether_sync_channel_t *s);
/* PushDeltaAsync(delta) — completed no-op bridge. Returns 0 / -1 (NULL). */
int ca_aether_sync_channel_push_delta(ca_aether_sync_channel_t *s,
                                      const ca_net_sync_delta_t *delta);
/* ReceiveDeltasAsync(ownerId) — drain one delivered delta. Always false. */
bool ca_aether_sync_channel_receive_next(ca_aether_sync_channel_t *s,
                                         const char *owner_id,
                                         ca_net_sync_delta_t **out);
/* GetLastSequenceAsync(ownerId, domainKey) — last known sequence (0 if unseen). */
int64_t ca_aether_sync_channel_last_sequence(
    const ca_aether_sync_channel_t *s, const char *owner_id,
    const char *domain_key);
/* Seed / advance the sequence for (ownerId, domainKey). Returns 0 / -1. */
int ca_aether_sync_channel_set_sequence(ca_aether_sync_channel_t *s,
                                        const char *owner_id,
                                        const char *domain_key, int64_t seq);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_NET_AETHER_H */
