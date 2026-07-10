// Networking.kt
//
// Kotlin port of the CircleAI.Networking transport-agnostic core abstraction —
// the C# reference under src/CircleAI.Networking/ is the EXACT spec.
//
// This is the transport ABSTRACTION that the 10 concrete transports (HTTP,
// WebSocket, gRPC, MQTT, TCP, WiFi Direct, BLE GATT, NearLink, Aether mesh,
// DTN) implement. The core itself is pure contracts + a permissive policy;
// the real sockets live behind INetworkTransport and are injected. To keep the
// abstraction fully exercisable with NO stubs, this file also ships the
// deterministic in-memory implementations (loopback transport, in-process mesh,
// in-process message channel, in-memory connectivity monitor, default transport
// selector) — mirroring the codebase convention (InProcess*/InMemory* stand-ins
// for the not-yet-wired transport, as in aethernet + memory.sync).
//
// Covers (C# → Kotlin):
//   NetworkTypes.cs        → TransportKind, ConnectivityState, MessagePriority,
//                            PeerRole enums (SyncDeliveryMode is REUSED from
//                            com.bhengubv.circleai.sync — not re-declared here)
//   NetworkPayload.cs      → NetworkPayload (record → data class, value bytes)
//   NetworkContext.cs      → NetworkContext (record → data class)
//   PeerInfo.cs            → PeerInfo (record → data class)
//   INetworkTransport.cs   → INetworkTransport
//   IMeshNetwork.cs        → IMeshNetwork
//   IMessageChannel.cs     → IMessageChannel
//   IConnectivityMonitor.cs→ IConnectivityMonitor
//   ITransportSelector.cs  → ITransportSelector
//   INetworkPolicy.cs      → INetworkPolicy
//   DefaultNetworkPolicy.cs→ DefaultNetworkPolicy (object)
//   NetworkPolicyBuilder.cs→ NetworkPolicyBuilder (fluent builder)
//
// C# → Kotlin conventions:
//   record                          → data class
//   IReadOnlyList / IReadOnlyDict   → List / Map
//   ReadOnlyMemory<byte>            → ByteArray (value equals/hashCode)
//   TimeSpan? / DateTimeOffset      → Duration? / Instant
//   Task / IAsyncEnumerable<T>      → suspend fun / Flow<T>
//   CancellationToken               → coroutine cancellation (structured)
//   static readonly Instance/Offline→ object / companion const

// NOTE: SyncDeliveryMode is intentionally NOT re-declared here — the networking
// core abstraction does not reference it, and the shared enum already lives in
// com.bhengubv.circleai.sync (SyncDelta / ISyncChannel use it). Per the work
// unit: "SyncDeliveryMode already exists in sync — reuse."
package com.bhengubv.circleai.networking

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.time.Duration
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

// ===========================================================================
// Enums  (NetworkTypes.cs)
// ===========================================================================

/**
 * Every transport family the abstraction can route over. Ordering matches the
 * C# declaration and doubles as the default selection cascade priority
 * (see [ITransportSelector]).
 */
enum class TransportKind {
    Http,
    WebSocket,
    Grpc,
    Mqtt,
    Tcp,
    Udp,

    /** WiFi Direct / mDNS / LAN — no Aether required. */
    WiFi,

    /** Raw BLE GATT — no Aether required. */
    Bluetooth,

    /** Huawei SLE / HarmonyOS — no Aether required. */
    NearLink,

    /** Full Aether mesh (Signal E2E + AODV + SOS). */
    Aether,

    /** 72hr store-and-forward over any transport. */
    Dtn,

    /** Offline queue — no live path at all. */
    LocalStore,
}

/** Coarse connectivity posture of the local node. */
enum class ConnectivityState { Online, LocalOnly, MeshOnly, Offline }

/** Relative urgency of a payload; drives queue ordering and transport choice. */
enum class MessagePriority { Low, Normal, High, Urgent, Emergency }

/** A discovered peer's role in the mesh topology. */
enum class PeerRole { Peer, Relay, Bridge, Sink }

// ===========================================================================
// NetworkPayload  (NetworkPayload.cs)
// ===========================================================================

/**
 * Immutable envelope for a single message or data unit traversing any
 * transport. Transports must not mutate it — create a new payload instead.
 *
 * [data] is opaque bytes; [equals]/[hashCode] use value semantics over the byte
 * content so two payloads carrying identical fields + bytes compare equal
 * (C# records give this for free; Kotlin needs the explicit override because
 * [ByteArray] is reference-equal by default).
 */
data class NetworkPayload(
    val id: String,
    val sourceId: String?,
    val destinationId: String?,
    val data: ByteArray,
    val priority: MessagePriority,
    val ttl: Duration?,
    val contentType: String,
    val metadata: Map<String, String>,
    val createdAt: Instant,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is NetworkPayload) return false
        return id == other.id &&
            sourceId == other.sourceId &&
            destinationId == other.destinationId &&
            data.contentEquals(other.data) &&
            priority == other.priority &&
            ttl == other.ttl &&
            contentType == other.contentType &&
            metadata == other.metadata &&
            createdAt == other.createdAt
    }

    override fun hashCode(): Int {
        var result = id.hashCode()
        result = 31 * result + (sourceId?.hashCode() ?: 0)
        result = 31 * result + (destinationId?.hashCode() ?: 0)
        result = 31 * result + data.contentHashCode()
        result = 31 * result + priority.hashCode()
        result = 31 * result + (ttl?.hashCode() ?: 0)
        result = 31 * result + contentType.hashCode()
        result = 31 * result + metadata.hashCode()
        result = 31 * result + createdAt.hashCode()
        return result
    }

    companion object {
        /**
         * Factory mirroring `NetworkPayload.Create` — assigns a fresh 32-char
         * hex id (Guid "N" format), no source, empty metadata, and a UTC
         * creation timestamp. [now] is injectable for deterministic tests.
         */
        fun create(
            data: ByteArray,
            destinationId: String? = null,
            priority: MessagePriority = MessagePriority.Normal,
            contentType: String = "application/octet-stream",
            ttl: Duration? = null,
            now: () -> Instant = { Instant.now() },
        ): NetworkPayload = NetworkPayload(
            id = UUID.randomUUID().toString().replace("-", ""),
            sourceId = null,
            destinationId = destinationId,
            data = data,
            priority = priority,
            ttl = ttl,
            contentType = contentType,
            metadata = emptyMap(),
            createdAt = now(),
        )
    }
}

// ===========================================================================
// NetworkContext  (NetworkContext.cs)
// ===========================================================================

/** Snapshot of current connectivity state. */
data class NetworkContext(
    val state: ConnectivityState,
    val preferredTransport: TransportKind,
    val availableTransports: List<TransportKind>,
    val signalStrengthDbm: Int?,
    val estimatedBandwidthBps: Long?,
    val latencyMs: Long?,
    val nearbyPeerCount: Int,
    val snapshotAt: Instant,
) {
    companion object {
        /**
         * The canonical "no connectivity" snapshot. Timestamped at call time so
         * a freshly-observed offline state is not mistaken for a stale one.
         */
        fun offline(now: () -> Instant = { Instant.now() }): NetworkContext = NetworkContext(
            state = ConnectivityState.Offline,
            preferredTransport = TransportKind.LocalStore,
            availableTransports = emptyList(),
            signalStrengthDbm = null,
            estimatedBandwidthBps = null,
            latencyMs = null,
            nearbyPeerCount = 0,
            snapshotAt = now(),
        )
    }
}

// ===========================================================================
// PeerInfo  (PeerInfo.cs)
// ===========================================================================

/** Describes a discovered peer on any transport. */
data class PeerInfo(
    val nodeId: String,
    val displayName: String?,
    val supportedTransports: List<TransportKind>,
    val role: PeerRole,
    val signalStrengthDbm: Int?,
    val lastSeen: Instant,
)

// ===========================================================================
// INetworkPolicy  (INetworkPolicy.cs)
// ===========================================================================

/**
 * Policy rules applied before choosing a transport.
 * Examples: "WiFi-only", "mesh-first", "no cloud when roaming".
 */
interface INetworkPolicy {
    /** Whether [transport] may carry [payload] under this policy. */
    fun permits(transport: TransportKind, payload: NetworkPayload): Boolean

    /** If non-null, forces every send onto this transport, bypassing selection. */
    val forceTransport: TransportKind?

    /** Prefer mesh transports (WiFi/Bluetooth/NearLink/Aether) ahead of cloud. */
    val meshFirst: Boolean

    /** Whether payloads may be queued for later delivery when offline. */
    val offlineQueueEnabled: Boolean

    /** Whether cloud transports (HTTP/WebSocket/gRPC/MQTT) are permitted at all. */
    val allowCloudTransports: Boolean
}

// ===========================================================================
// DefaultNetworkPolicy  (DefaultNetworkPolicy.cs)
// ===========================================================================

/** Permissive default: all transports allowed, offline queue on. */
object DefaultNetworkPolicy : INetworkPolicy {
    override fun permits(transport: TransportKind, payload: NetworkPayload): Boolean = true
    override val forceTransport: TransportKind? = null
    override val meshFirst: Boolean = false
    override val offlineQueueEnabled: Boolean = true
    override val allowCloudTransports: Boolean = true
}

// ===========================================================================
// NetworkPolicyBuilder  (NetworkPolicyBuilder.cs)
// ===========================================================================

/** Fluent builder for [INetworkPolicy]. */
class NetworkPolicyBuilder {
    private val allowed = LinkedHashSet<TransportKind>()
    private var meshFirst = false
    private var noCloud = false
    private var queueEnabled = true
    private var force: TransportKind? = null

    fun meshFirst(): NetworkPolicyBuilder = apply { meshFirst = true }
    fun noCloud(): NetworkPolicyBuilder = apply { noCloud = true }
    fun disableQueue(): NetworkPolicyBuilder = apply { queueEnabled = false }
    fun force(t: TransportKind): NetworkPolicyBuilder = apply { force = t }

    fun allow(vararg kinds: TransportKind): NetworkPolicyBuilder = apply {
        for (k in kinds) allowed.add(k)
    }

    fun build(): INetworkPolicy = Policy(
        allowed = if (allowed.isNotEmpty()) LinkedHashSet(allowed) else null,
        meshFirst = meshFirst,
        noCloud = noCloud,
        queueEnabled = queueEnabled,
        force = force,
    )

    private class Policy(
        private val allowed: Set<TransportKind>?,
        meshFirst: Boolean,
        private val noCloud: Boolean,
        queueEnabled: Boolean,
        force: TransportKind?,
    ) : INetworkPolicy {
        override val meshFirst: Boolean = meshFirst
        override val offlineQueueEnabled: Boolean = queueEnabled
        override val forceTransport: TransportKind? = force
        override val allowCloudTransports: Boolean = !noCloud

        override fun permits(transport: TransportKind, payload: NetworkPayload): Boolean {
            if (noCloud && transport in CLOUD_TRANSPORTS) return false
            return allowed == null || transport in allowed
        }
    }

    companion object {
        /** The four cloud transports gated by [noCloud] / [INetworkPolicy.allowCloudTransports]. */
        internal val CLOUD_TRANSPORTS = setOf(
            TransportKind.Http,
            TransportKind.WebSocket,
            TransportKind.Grpc,
            TransportKind.Mqtt,
        )
    }
}

// ===========================================================================
// INetworkTransport  (INetworkTransport.cs)
// ===========================================================================

/** Unified send/receive abstraction for a single transport kind. */
interface INetworkTransport {
    /** Which transport family this instance speaks. */
    val kind: TransportKind

    /** Whether the transport is currently usable (started + link up). */
    val isAvailable: Boolean

    /** Bring the transport up (bind sockets, join mesh, …). Idempotent. */
    suspend fun start()

    /** Tear the transport down and release resources. Idempotent. */
    suspend fun stop()

    /** Enqueue [payload] for delivery. Returns when accepted by the transport. */
    suspend fun send(payload: NetworkPayload)

    /**
     * Emits every inbound payload as a cold [Flow]. The flow completes when the
     * transport is stopped or the collector cancels.
     */
    fun receive(): Flow<NetworkPayload>
}

// ===========================================================================
// IMeshNetwork  (IMeshNetwork.cs)
// ===========================================================================

/** Mesh-specific: topology, node identity, mesh health. */
interface IMeshNetwork {
    /** Stable identity of this node in the mesh. */
    val localNodeId: String

    /** Currently-reachable peer node ids. */
    suspend fun getPeerIds(): List<String>

    /** A fresh [NetworkContext] describing mesh health. */
    suspend fun getMeshHealth(): NetworkContext
}

// ===========================================================================
// IMessageChannel  (IMessageChannel.cs)
// ===========================================================================

/**
 * Typed message delivery over any transport. The C# generic
 * `SendAsync<T>` / `ReceiveAsync<T>` (where `T : class`) maps to reified type
 * parameters here; a runtime type filter routes each inbound message to the
 * collector that asked for its type.
 */
interface IMessageChannel {
    /** Send [message] to [destinationId]. */
    suspend fun <T : Any> send(destinationId: String, message: T)

    /** Emits inbound messages of type [T] as a cold [Flow]. */
    fun <T : Any> receive(type: Class<T>): Flow<T>
}

/** Reified convenience for [IMessageChannel.receive], mirroring the C# generic call site. */
inline fun <reified T : Any> IMessageChannel.receive(): Flow<T> = receive(T::class.java)

// ===========================================================================
// IConnectivityMonitor  (IConnectivityMonitor.cs)
// ===========================================================================

/** Observes connectivity state and emits changes. */
interface IConnectivityMonitor {
    /** The most recently observed [ConnectivityState]. */
    val currentState: ConnectivityState

    /** A point-in-time [NetworkContext] snapshot. */
    fun getSnapshot(): NetworkContext

    /**
     * Emits a fresh [NetworkContext] on every connectivity change as a cold
     * [Flow]. The current snapshot is emitted first so late subscribers are not
     * left blind until the next change.
     */
    fun watch(): Flow<NetworkContext>
}

// ===========================================================================
// ITransportSelector  (ITransportSelector.cs)
// ===========================================================================

/**
 * Selects the best transport for a payload+context.
 *
 * Default cascade: gRPC → WebSocket → HTTP → MQTT → TCP → WiFi → Bluetooth →
 * NearLink → Aether → DTN → LocalStore.
 */
interface ITransportSelector {
    /** The single best transport for [payload] given [context]. */
    fun selectBest(payload: NetworkPayload, context: NetworkContext): TransportKind

    /**
     * The ordered fall-back cascade for [payload] given [context] — index 0 is
     * the first choice, the last entry is the guaranteed-terminal fallback.
     */
    fun getCascade(payload: NetworkPayload, context: NetworkContext): List<TransportKind>
}

// ===========================================================================
// DefaultTransportSelector  — deterministic policy-aware selector.
//
// No concrete selector ships in the C# core (it is a pure interface there),
// but the RULES require a working implementation for every contract. This one
// realises the documented cascade exactly and folds in the two obvious
// signals: an [INetworkPolicy] (force / mesh-first / cloud-gating / per-kind
// permits) and the live [NetworkContext] (only offer transports the context
// reports available, always keeping the terminal LocalStore fallback).
// ===========================================================================

class DefaultTransportSelector(
    private val policy: INetworkPolicy = DefaultNetworkPolicy,
) : ITransportSelector {

    override fun selectBest(payload: NetworkPayload, context: NetworkContext): TransportKind =
        // Cascade is never empty (LocalStore is always the terminal fallback).
        getCascade(payload, context).first()

    override fun getCascade(payload: NetworkPayload, context: NetworkContext): List<TransportKind> {
        // 1. Forced transport short-circuits everything (still honoured even if
        //    the context does not list it — a force is an explicit override).
        policy.forceTransport?.let { forced ->
            return if (forced == TransportKind.LocalStore) listOf(forced)
            else listOf(forced, TransportKind.LocalStore)
        }

        // 2. Base priority order (the documented cascade).
        var order = BASE_CASCADE

        // 3. mesh-first: bubble mesh transports ahead of cloud (stable within groups).
        if (policy.meshFirst) {
            order = order.sortedBy { if (it in MESH_TRANSPORTS) 0 else 1 }
        }

        // 4. Filter by policy.permits + cloud gating + context availability.
        //    LocalStore is exempt from the availability filter — it is the
        //    offline queue and is always a legal terminal, provided the policy
        //    permits it.
        val available = context.availableTransports.toHashSet()
        val filtered = order.filter { kind ->
            if (!policy.permits(kind, payload)) return@filter false
            if (!policy.allowCloudTransports && kind in NetworkPolicyBuilder.CLOUD_TRANSPORTS) {
                return@filter false
            }
            kind == TransportKind.LocalStore || kind in available
        }

        // 5. Guarantee a terminal fallback. If the offline queue is enabled and
        //    LocalStore is permitted, ensure it is present at the tail; if the
        //    filter produced nothing at all, fall back to LocalStore regardless
        //    so a send never has an empty cascade.
        val result = filtered.toMutableList()
        val localStorePermitted = policy.permits(TransportKind.LocalStore, payload)
        if (policy.offlineQueueEnabled && localStorePermitted &&
            TransportKind.LocalStore !in result
        ) {
            result.add(TransportKind.LocalStore)
        }
        if (result.isEmpty()) result.add(TransportKind.LocalStore)
        return result
    }

    companion object {
        /**
         * The documented default cascade, in priority order:
         * gRPC → WebSocket → HTTP → MQTT → TCP → WiFi → Bluetooth → NearLink →
         * Aether → DTN → LocalStore. (Udp is not part of the documented cascade
         * and is only ever selected via an explicit policy force.)
         */
        val BASE_CASCADE: List<TransportKind> = listOf(
            TransportKind.Grpc,
            TransportKind.WebSocket,
            TransportKind.Http,
            TransportKind.Mqtt,
            TransportKind.Tcp,
            TransportKind.WiFi,
            TransportKind.Bluetooth,
            TransportKind.NearLink,
            TransportKind.Aether,
            TransportKind.Dtn,
            TransportKind.LocalStore,
        )

        /** Local-first mesh transports bubbled up by [INetworkPolicy.meshFirst]. */
        val MESH_TRANSPORTS: Set<TransportKind> = setOf(
            TransportKind.WiFi,
            TransportKind.Bluetooth,
            TransportKind.NearLink,
            TransportKind.Aether,
        )
    }
}

// ===========================================================================
// In-memory deterministic implementations of the transport-side contracts.
//
// These are the "real socket injected behind INetworkTransport" stand-ins: a
// loopback bus that lets any number of transports/channels talk in-process,
// exactly as the codebase does for CompanionStateChannel (InProcessSyncHub) and
// mesh capability (InMemoryMeshCapabilityRegistry). No stubs — every method has
// working behaviour.
//
// CONCURRENCY: fan-out uses UNBOUNDED channels so a publish never blocks; the
// subscriber list is snapshotted under a lock and the lock is RELEASED before
// any delivery (no continuation completes while a lock its cleanup path also
// takes is held). Subscription is registered SYNCHRONOUSLY before the consumer
// starts collecting, so a message sent immediately after start is not lost.
// ===========================================================================

/**
 * A shared in-process delivery bus. Every [LoopbackNetworkTransport] attached to
 * the same bus can send to it and receive what others send. Routing:
 *   - a payload with a null/blank [NetworkPayload.destinationId] is broadcast to
 *     every OTHER attached transport,
 *   - a payload addressed to a specific node is delivered only to the transport
 *     whose [LoopbackNetworkTransport.nodeId] matches.
 *
 * The sender never receives its own payload (loopback, not echo).
 */
class LoopbackNetworkBus {
    private val subscribers = ConcurrentHashMap<String, Channel<NetworkPayload>>()
    private val lock = Any()
    // Ordered registration list so broadcast is deterministic.
    private val order = ArrayList<String>()

    internal fun attach(nodeId: String): Channel<NetworkPayload> {
        val ch = Channel<NetworkPayload>(Channel.UNLIMITED)
        synchronized(lock) {
            subscribers[nodeId]?.close()
            subscribers[nodeId] = ch
            if (nodeId !in order) order.add(nodeId)
        }
        return ch
    }

    internal fun detach(nodeId: String) {
        val ch = synchronized(lock) {
            order.remove(nodeId)
            subscribers.remove(nodeId)
        }
        ch?.close()
    }

    /** Node ids currently attached to this bus, in attach order. */
    val attachedNodeIds: List<String>
        get() = synchronized(lock) { order.toList() }

    /**
     * Route [payload] originating from [senderNodeId]. Snapshot the target set
     * under the lock, then release the lock before delivering so a subscriber's
     * close()/detach path (which also takes the lock) can never self-deadlock.
     */
    internal fun route(payload: NetworkPayload, senderNodeId: String) {
        val targets: List<Channel<NetworkPayload>> = synchronized(lock) {
            val dest = payload.destinationId
            if (dest.isNullOrBlank()) {
                // Broadcast to everyone except the sender, in registration order.
                order.asSequence()
                    .filter { it != senderNodeId }
                    .mapNotNull { subscribers[it] }
                    .toList()
            } else {
                // Unicast to the addressed node (if attached and not the sender).
                if (dest != senderNodeId) listOfNotNull(subscribers[dest]) else emptyList()
            }
        }
        for (ch in targets) {
            // UNBOUNDED channel: trySend always succeeds unless closed.
            ch.trySend(payload)
        }
    }
}

/**
 * In-process [INetworkTransport] over a [LoopbackNetworkBus]. Deterministic
 * stand-in for a real socket-backed transport. [kind] defaults to
 * [TransportKind.LocalStore] but can be any kind so tests can simulate a
 * specific transport family.
 *
 * @param nodeId identity used for routing on the bus.
 * @param bus the shared delivery bus.
 * @param kind the transport family this instance reports as.
 */
class LoopbackNetworkTransport(
    val nodeId: String,
    private val bus: LoopbackNetworkBus,
    override val kind: TransportKind = TransportKind.LocalStore,
) : INetworkTransport {

    @Volatile private var started = false
    @Volatile private var inbox: Channel<NetworkPayload>? = null
    private val lifecycleLock = Any()

    init {
        require(nodeId.isNotBlank()) { "nodeId is required." }
    }

    override val isAvailable: Boolean
        get() = started

    override suspend fun start() {
        synchronized(lifecycleLock) {
            if (started) return
            // Attach SYNCHRONOUSLY before returning so a payload sent right after
            // start() is captured, never raced/lost.
            inbox = bus.attach(nodeId)
            started = true
        }
    }

    override suspend fun stop() = closeNow()

    /**
     * Synchronous teardown — detaches from the bus (which closes the inbox
     * channel, completing any active [receive] flow). Exposed so non-suspending
     * `AutoCloseable`-style callers can tear the transport down too. Idempotent.
     */
    fun closeNow() {
        synchronized(lifecycleLock) {
            if (!started) return
            started = false
            bus.detach(nodeId) // closes the inbox channel → receive() flow completes
            inbox = null
        }
    }

    override suspend fun send(payload: NetworkPayload) {
        check(started) { "Transport '$nodeId' ($kind) is not started." }
        // Stamp the source if the caller left it null (a transport knows its own
        // node id). NetworkPayload is immutable → produce a new instance.
        val stamped = if (payload.sourceId == null) payload.copy(sourceId = nodeId) else payload
        bus.route(stamped, nodeId)
    }

    override fun receive(): Flow<NetworkPayload> {
        val ch = inbox ?: throw IllegalStateException(
            "Transport '$nodeId' ($kind) is not started; call start() before receive()."
        )
        return flow {
            for (p in ch) emit(p)
        }
    }
}

/**
 * In-memory [IMeshNetwork] whose peer set + health are driven by an injected
 * [LoopbackNetworkBus] (peers = everyone else attached) and a mutable health
 * snapshot. Deterministic; no real topology.
 */
class InMemoryMeshNetwork(
    override val localNodeId: String,
    private val bus: LoopbackNetworkBus,
    private val now: () -> Instant = { Instant.now() },
) : IMeshNetwork {

    @Volatile private var health: NetworkContext? = null

    init {
        require(localNodeId.isNotBlank()) { "localNodeId is required." }
    }

    /** Override the reported mesh health snapshot (e.g. from a simulator). */
    fun setHealth(context: NetworkContext) {
        health = context
    }

    override suspend fun getPeerIds(): List<String> =
        bus.attachedNodeIds.filter { it != localNodeId }

    override suspend fun getMeshHealth(): NetworkContext {
        health?.let { return it }
        // Derive a sensible default snapshot from the live peer count.
        val peers = getPeerIds()
        val online = peers.isNotEmpty()
        return NetworkContext(
            state = if (online) ConnectivityState.MeshOnly else ConnectivityState.Offline,
            preferredTransport = if (online) TransportKind.Aether else TransportKind.LocalStore,
            availableTransports = if (online) listOf(TransportKind.Aether) else emptyList(),
            signalStrengthDbm = null,
            estimatedBandwidthBps = null,
            latencyMs = null,
            nearbyPeerCount = peers.size,
            snapshotAt = now(),
        )
    }
}

/**
 * Routes typed messages between every [InMemoryMessageChannel] that has joined
 * the hub — one hub per simulated "mesh", mirroring the codebase's
 * `InProcessSyncHub`. Delivery is direct into the destination channel's
 * per-type UNBOUNDED buffer (no intermediate pump coroutine), so a message sent
 * before the receiver begins collecting is retained and delivered on first read.
 */
class MessageHub {
    private val channels = ConcurrentHashMap<String, InMemoryMessageChannel>()

    internal fun join(channel: InMemoryMessageChannel) {
        channels[channel.nodeId] = channel
    }

    internal fun leave(nodeId: String) {
        channels.remove(nodeId)
    }

    /** Node ids currently on this hub. */
    val connectedNodeIds: Collection<String>
        get() = channels.keys.toList()

    /**
     * Deliver [message] of runtime type [typeName] to [destinationId]. No-op if
     * the destination has not joined (a real transport would queue/drop; the
     * deterministic hub simply does not deliver to an absent node).
     */
    internal fun <T : Any> deliver(destinationId: String, typeName: String, message: T) {
        channels[destinationId]?.enqueue(typeName, message)
    }
}

/**
 * In-process [IMessageChannel] over a [MessageHub]. Because the hub is
 * in-process the original message object is handed straight across (no invented
 * wire format — deterministic, lossless, no external serializer). [receive]
 * surfaces only messages whose runtime type matches the requested type,
 * mirroring the C# generic `ReceiveAsync<T>`.
 *
 * CONCURRENCY: per-type subscriber channels are UNBOUNDED, so [send] never
 * blocks and a message delivered before the subscriber attaches is buffered
 * until read. The type→channel map is read under a short lock that is RELEASED
 * before any [Channel.trySend]; [close] snapshots the channels under the lock
 * and closes them only after releasing it (no continuation completes while the
 * lock its cleanup path also takes is held).
 */
class InMemoryMessageChannel(
    val nodeId: String,
    private val hub: MessageHub,
) : IMessageChannel, AutoCloseable {

    // Per-type fan-out. Each subscribed type gets its own UNBOUNDED channel.
    private val byType = ConcurrentHashMap<String, Channel<Any>>()
    private val lock = Any()
    @Volatile private var closed = false

    init {
        require(nodeId.isNotBlank()) { "nodeId is required." }
        hub.join(this)
    }

    /** Detach from the hub + close all subscriber channels. Idempotent. */
    override fun close() {
        if (closed) return
        closed = true
        hub.leave(nodeId)
        val snapshot = synchronized(lock) { byType.values.toList() }
        for (ch in snapshot) ch.close()
    }

    override suspend fun <T : Any> send(destinationId: String, message: T) {
        require(destinationId.isNotBlank()) { "destinationId is required." }
        check(!closed) { "InMemoryMessageChannel '$nodeId' is closed." }
        hub.deliver(destinationId, message::class.java.name, message)
    }

    override fun <T : Any> receive(type: Class<T>): Flow<T> {
        // Register the per-type channel SYNCHRONOUSLY (before any collection or
        // send races it) so a message delivered the instant collection begins
        // lands in an existing buffer, not the void.
        val ch = obtainChannel(type.name)
        return flow {
            for (obj in ch) {
                @Suppress("UNCHECKED_CAST")
                emit(obj as T)
            }
        }
    }

    /** Deliver [message] into this channel's buffer for [typeName]. Called by the hub. */
    internal fun <T : Any> enqueue(typeName: String, message: T) {
        if (closed) return
        // UNBOUNDED channel: trySend only fails if the channel was closed.
        obtainChannel(typeName).trySend(message)
    }

    private fun obtainChannel(typeName: String): Channel<Any> =
        synchronized(lock) { byType.getOrPut(typeName) { Channel(Channel.UNLIMITED) } }
}

/**
 * In-memory [IConnectivityMonitor]. Holds a current [NetworkContext] and pushes
 * every change to subscribers over UNBOUNDED per-subscriber channels. The
 * current snapshot is replayed to each new subscriber first so late joiners are
 * never blind.
 */
class InMemoryConnectivityMonitor(
    initial: NetworkContext,
) : IConnectivityMonitor {

    @Volatile private var snapshot: NetworkContext = initial
    private val subscribers = ArrayList<Channel<NetworkContext>>()
    private val lock = Any()

    override val currentState: ConnectivityState
        get() = snapshot.state

    override fun getSnapshot(): NetworkContext = snapshot

    /**
     * Publish a new connectivity snapshot. Snapshot the subscriber list under
     * the lock, RELEASE the lock, then emit — so a subscriber that closes its
     * channel from a termination handler (which takes the lock) cannot deadlock.
     */
    fun push(context: NetworkContext) {
        val targets: List<Channel<NetworkContext>> = synchronized(lock) {
            snapshot = context
            subscribers.toList()
        }
        for (ch in targets) ch.trySend(context)
    }

    override fun watch(): Flow<NetworkContext> {
        // Register the subscriber channel and capture the current snapshot
        // atomically so no push is missed between snapshot-read and register.
        val ch = Channel<NetworkContext>(Channel.UNLIMITED)
        val current = synchronized(lock) {
            subscribers.add(ch)
            snapshot
        }
        // Seed with the current snapshot so a late subscriber sees state now.
        ch.trySend(current)
        return flow {
            try {
                for (ctx in ch) emit(ctx)
            } finally {
                // Deregister on cancellation/completion; take the lock only to
                // mutate the list, never while emitting.
                synchronized(lock) { subscribers.remove(ch) }
                ch.close()
            }
        }
    }
}
