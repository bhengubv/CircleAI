// AetherNet.kt
//
// Kotlin port of CircleAI.Networking.AetherNet (src/CircleAI.Networking.AetherNet/*.cs
// is the EXACT spec). The AetherNet mesh transport bridges the transport-agnostic
// CircleAI.Networking contracts to the Aether mesh protocol engine (BLE + WiFi
// Direct + NearLink + NFC + LoRa + HTTP Relay physical transports, Signal E2E,
// AODV routing, DTN 72hr store-and-forward, SOS flood).
//
// The real mesh engine is injected behind [IAetherContext] (the presence/capability
// contract from com.bhengubv.circleai.aether) — no real sockets here. This layer is
// the deterministic in-memory bridge plus the shared descriptor/telemetry registry
// from AetherNetTransportCommons.cs.
//
// Covers (C# → Kotlin):
//   AetherNetTransportCommons.cs → AetherPeerKind (enum), AetherPeer,
//                                  AetherHopTelemetry, AetherPacketSummary (records
//                                  → data classes), InMemoryAetherNetRegistry
//   AetherNetworkTransport.cs    → AetherNetworkTransport (INetworkTransport)
//   AetherPeerDiscovery.cs       → AetherPeerDiscovery (IPeerDiscovery) + the
//                                  IPeerDiscovery core contract (IPeerDiscovery.cs,
//                                  not previously ported to Kotlin)
//   AetherSyncChannel.cs         → AetherSyncChannel (ISyncChannel)
//
// C# → Kotlin conventions:
//   record                       → data class
//   IReadOnlyList                 → List
//   ReadOnlyMemory<byte>          → ByteArray
//   ConcurrentDictionary + lock   → ConcurrentHashMap + synchronized
//   Task / IAsyncEnumerable<T>    → suspend fun / Flow<T>
//   Channel.CreateUnbounded       → kotlinx.coroutines Channel(UNLIMITED)
//   [Flags] enum                  → n/a here
//
// CONCURRENCY: the inbound bridge channel is UNBOUNDED so a publish never blocks;
// stop() completes the channel which ends the receive() flow. The registry mutates
// its telemetry/packet lists only under a short lock and never holds it while
// emitting to a channel.
package com.bhengubv.circleai.networking.aethernet

import com.bhengubv.circleai.aether.IAetherContext
import com.bhengubv.circleai.networking.INetworkTransport
import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.PeerInfo
import com.bhengubv.circleai.networking.TransportKind
import com.bhengubv.circleai.sync.ISyncChannel
import com.bhengubv.circleai.sync.SyncDelta
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.flow
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// ===========================================================================
// AetherPeerKind  (AetherNetTransportCommons.cs)
// ===========================================================================

/** Device class of a peer discovered on the Aether mesh. */
enum class AetherPeerKind { Phone, Tablet, Laptop, Desktop, Edge, Vehicle, Iot }

// ===========================================================================
// Records  (AetherNetTransportCommons.cs)
// ===========================================================================

/** A peer node on the Aether mesh, with its advertised capabilities. */
data class AetherPeer(
    val peerId: String,
    val kind: AetherPeerKind,
    val friendlyName: String?,
    val advertisedCapabilities: List<String>,
)

/** One hop-telemetry sample: round-trip latency + hop count to a peer. */
data class AetherHopTelemetry(
    val peerId: String,
    val hopCount: Int,
    val roundTripMs: Double,
    val atUtc: Instant,
)

/** Summary of a single packet that traversed the mesh. */
data class AetherPacketSummary(
    val packetId: String,
    val fromPeer: String,
    val toPeer: String,
    val bytes: Int,
    val packetKind: String,
    val atUtc: Instant,
)

// ===========================================================================
// InMemoryAetherNetRegistry  (AetherNetTransportCommons.cs)
// ===========================================================================

/**
 * Deterministic in-memory store of mesh peers + hop telemetry + packet summaries.
 * Mirrors the C# [ConcurrentDictionary] + `lock`ed lists: [register]/[getPeer]/
 * [peers] are lock-free over the peer map, while telemetry + packet aggregation
 * take a short lock. [peers] is ordered by peer id (Ordinal), [recentPackets]
 * newest-first, exactly as the C# `OrderBy`/`OrderByDescending` do.
 */
class InMemoryAetherNetRegistry {
    private val peersMap = ConcurrentHashMap<String, AetherPeer>()
    private val telemetry = ArrayList<AetherHopTelemetry>()
    private val packets = ArrayList<AetherPacketSummary>()
    private val lock = Any()

    /** Register (or replace) a peer by its [AetherPeer.peerId]. */
    fun register(p: AetherPeer) {
        peersMap[p.peerId] = p
    }

    /** The peer with [id], or null if unknown. */
    fun getPeer(id: String): AetherPeer? = peersMap[id]

    /** All registered peers, ordered by peer id (ordinal). */
    val peers: List<AetherPeer>
        get() = peersMap.values.sortedBy { it.peerId }

    /** Record a hop-telemetry sample. */
    fun recordHop(t: AetherHopTelemetry) {
        synchronized(lock) { telemetry.add(t) }
    }

    /** Record a packet summary. */
    fun recordPacket(p: AetherPacketSummary) {
        synchronized(lock) { packets.add(p) }
    }

    /** The [limit] most recent packet summaries, newest first. */
    fun recentPackets(limit: Int = 100): List<AetherPacketSummary> =
        synchronized(lock) {
            packets.sortedByDescending { it.atUtc }.take(limit)
        }

    /**
     * Mean round-trip latency (ms) across all recorded hops for [peerId].
     * Returns 0.0 when there are no samples, matching C# `DefaultIfEmpty(0).Average()`.
     */
    fun avgRoundTripMs(peerId: String): Double =
        synchronized(lock) {
            val vals = telemetry.filter { it.peerId == peerId }.map { it.roundTripMs }
            if (vals.isEmpty()) 0.0 else vals.average()
        }

    /** Total bytes carried in packets from [fromPeer] to [toPeer]. */
    fun totalBytesBetween(fromPeer: String, toPeer: String): Int =
        synchronized(lock) {
            packets.filter { it.fromPeer == fromPeer && it.toPeer == toPeer }.sumOf { it.bytes }
        }
}

// ===========================================================================
// IPeerDiscovery  (CircleAI.Networking/IPeerDiscovery.cs)
//
// The transport-agnostic peer-discovery contract from the networking core
// namespace (CircleAI.Networking). It was not previously ported to the Kotlin
// networking core; AetherPeerDiscovery is its first implementor, so it is
// declared here alongside the implementation.
// ===========================================================================

/**
 * Finds nearby devices via mDNS, BLE beacons, NearLink scan, Aether presence, etc.
 */
interface IPeerDiscovery {
    /** Emits every discovered peer as a cold [Flow]; completes when the caller cancels. */
    fun discover(): Flow<PeerInfo>

    /** Announce [localInfo] to nearby devices. */
    suspend fun announce(localInfo: PeerInfo)
}

// ===========================================================================
// AetherNetworkTransport  (AetherNetworkTransport.cs)
// ===========================================================================

/**
 * [INetworkTransport] backed by the Aether mesh protocol engine. Uses BLE + WiFi
 * Direct + NearLink + NFC + LoRa + HTTP Relay as physical transports. Signal
 * Protocol (X3DH + Double Ratchet) provides end-to-end encryption. AODV routing +
 * DTN 72hr store-and-forward for offline delivery. SOS flood is available for
 * emergency messages.
 *
 * Availability tracks the injected [IAetherContext] (`IsAvailable`). Routing is
 * handled by the aether-protocol engine; this layer bridges the CircleAI.Networking
 * contract to the Aether transport, so [send] is a no-op accept here (mirroring the
 * C# reference — the full wire wires into aether-protocol's RoutingService +
 * SignalCipher). Inbound payloads arrive over the UNBOUNDED [inbound] bridge; [stop]
 * completes it, ending the [receive] flow.
 */
class AetherNetworkTransport(
    private val context: IAetherContext,
) : INetworkTransport {

    private val inbound = Channel<NetworkPayload>(Channel.UNLIMITED)

    override val kind: TransportKind get() = TransportKind.Aether
    override val isAvailable: Boolean get() = context.isAvailable

    override suspend fun start() {
        // No-op: the Aether runtime is managed out-of-band (via IAetherContext).
    }

    override suspend fun stop() {
        // Complete the inbound bridge so any active receive() flow ends.
        inbound.close()
    }

    /**
     * Routes [payload] via the Aether mesh. Emergency payloads trigger SOS flood
     * mode in the full engine. Here the routing is delegated to aether-protocol;
     * this layer only bridges the contract (mirrors C# — reads priority, accepts).
     */
    override suspend fun send(payload: NetworkPayload) {
        // Routing is handled by the aether-protocol engine.
        @Suppress("UNUSED_EXPRESSION")
        payload.priority
    }

    override fun receive(): Flow<NetworkPayload> = flow {
        for (p in inbound) emit(p)
    }
}

// ===========================================================================
// AetherPeerDiscovery  (AetherPeerDiscovery.cs)
// ===========================================================================

/**
 * [IPeerDiscovery] using Aether presence beacons (Hello/HelloAck). No
 * infrastructure — discovery works over BLE / WiFi Direct / NearLink. The full
 * wire subscribes to Aether telemetry NodeJoined events; this bridge yields no
 * peers until the engine is wired (mirrors the C# `yield break`), and [announce]
 * is the presence-beacon broadcast hook (no-op accept here).
 */
class AetherPeerDiscovery(
    private val context: IAetherContext,
) : IPeerDiscovery {

    override fun discover(): Flow<PeerInfo> = emptyFlow()

    override suspend fun announce(localInfo: PeerInfo) {
        // Full wire: AetherPresenceBeacon broadcast. No-op bridge accept.
    }
}

// ===========================================================================
// AetherSyncChannel  (AetherSyncChannel.cs)
// ===========================================================================

/**
 * [ISyncChannel] backed by Aether DTN store-and-forward. Memory deltas are
 * delivered even when source and destination devices are never simultaneously
 * online — a DTN bundle relays through intermediate nodes. TTL = 72 hours by
 * default (matches the aether-protocol DTN spec).
 *
 * [pushDelta] serialises the delta and hands it to the aether-protocol DTN engine
 * for custody-transfer delivery (bridge accept here). [getLastSequence] tracks the
 * last observed sequence per (owner, domain) under a lock, mirroring the C#
 * `Dictionary` + `Lock`.
 */
class AetherSyncChannel(
    private val context: IAetherContext,
) : ISyncChannel {

    private val sequences = HashMap<Pair<String, String>, Long>()
    private val lock = Any()

    override suspend fun pushDelta(delta: SyncDelta) {
        // Serialise delta and hand to aether-protocol DTN engine for custody-transfer
        // delivery. Full wire: AetherDtnBundle { payload, ttl=72h, custodyRequired=true }.
    }

    override fun receiveDeltas(ownerId: String): Flow<SyncDelta> = emptyFlow()

    override suspend fun getLastSequence(ownerId: String, domainKey: String): Long =
        synchronized(lock) { sequences[ownerId to domainKey] ?: 0L }
}
