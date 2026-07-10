// Mqtt.kt
//
// Kotlin port of CircleAI.Networking.Mqtt (src/CircleAI.Networking.Mqtt/*.cs is the
// EXACT spec). An [INetworkTransport] backed by an MQTT broker: publishes to
// `circle/payloads/{destinationId}` (or `circle/payloads/broadcast`) and subscribes
// to `circle/payloads/{localClientId}/#`.
//
// The C# reference uses MQTTnet's IMqttClient (a real broker connection). Per the
// work unit ("in-memory, deterministic, socket injected"), the Kotlin port injects
// [IMqttClient] — the same connect / subscribe / publish / disconnect surface, with
// an inbound-message callback standing in for MQTTnet's
// ApplicationMessageReceivedAsync event. The topic construction + the QoS mapping
// (ExactlyOnce when priority >= High, else AtLeastOnce) are preserved byte-for-byte.
// A deterministic [InMemoryMqttClient] backed by [InMemoryMqttBroker] ships so the
// transport is fully exercisable with no real network.
//
// Covers (C# → Kotlin):
//   MqttTransportCommons.cs  → MqttQos (enum, explicit 0/1/2 values),
//                              MqttTopicDescriptor, MqttRetainedMessage,
//                              MqttClientDescriptor (records → data classes),
//                              InMemoryMqttBroker (topic matcher + retained store +
//                              subscription registry)
//   MqttNetworkTransport.cs  → MqttNetworkTransport (INetworkTransport,
//                              AutoCloseable), IMqttClient (injected broker
//                              contract), MqttMessage + InMemoryMqttClient (the
//                              deterministic broker-backed stand-in)
//
// C# → Kotlin conventions:
//   record                          → data class
//   IReadOnlyList                    → List
//   ConcurrentDictionary + lock      → ConcurrentHashMap + synchronized
//   HashSet<string>(Ordinal)         → LinkedHashSet<String>
//   IAsyncDisposable                 → AutoCloseable
//   TimeSpan                         → java.time.Duration
//   Task / IAsyncEnumerable<T>       → suspend fun / Flow<T>
//   static class                     → object
//
// CONCURRENCY: the inbound channel is UNBOUNDED so an inbound broker message never
// blocks; the transport subscribes SYNCHRONOUSLY during start() (registering its
// message handler before the subscription is issued), so a message published right
// after start() is not raced/lost. stop() disconnects then completes the channel so
// the receive() flow ends. The broker snapshots its subscriber set under a lock and
// releases it before any delivery.
package com.bhengubv.circleai.networking.mqtt

import com.bhengubv.circleai.networking.INetworkTransport
import com.bhengubv.circleai.networking.MessagePriority
import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.TransportKind
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// ===========================================================================
// MqttQos  (MqttTransportCommons.cs)
// ===========================================================================

/** MQTT quality-of-service level. Wire values match the MQTT spec (0/1/2). */
enum class MqttQos(val value: Int) {
    AtMostOnce(0),
    AtLeastOnce(1),
    ExactlyOnce(2),
}

// ===========================================================================
// Records  (MqttTransportCommons.cs)
// ===========================================================================

/** A topic + the QoS it is published/subscribed at. */
data class MqttTopicDescriptor(val topic: String, val qos: MqttQos)

/** A retained message pinned to a topic, with the time it was retained. */
data class MqttRetainedMessage(
    val topic: String,
    val payload: ByteArray,
    val retainedAtUtc: Instant,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is MqttRetainedMessage) return false
        return topic == other.topic &&
            payload.contentEquals(other.payload) &&
            retainedAtUtc == other.retainedAtUtc
    }

    override fun hashCode(): Int {
        var result = topic.hashCode()
        result = 31 * result + payload.contentHashCode()
        result = 31 * result + retainedAtUtc.hashCode()
        return result
    }
}

/** Connection descriptor for an MQTT client: id, host, port, TLS, keep-alive. */
data class MqttClientDescriptor(
    val clientId: String,
    val host: String,
    val port: Int,
    val useTls: Boolean,
    val keepAlive: Duration,
)

// ===========================================================================
// InMemoryMqttBroker  (MqttTransportCommons.cs)
// ===========================================================================

/**
 * Deterministic in-memory MQTT broker: connected-client registry, per-client
 * subscription filters, retained-message store, and the MQTT topic matcher. Mirrors
 * the C# [ConcurrentDictionary] maps + `lock`ed subscription sets.
 *
 * The topic matcher implements MQTT wildcard semantics exactly as the C# reference:
 * `#` matches the remainder, `+` matches a single level, and an exact match requires
 * equal level counts.
 */
class InMemoryMqttBroker {
    private val retained = ConcurrentHashMap<String, MqttRetainedMessage>()
    private val clients = ConcurrentHashMap<String, MqttClientDescriptor>()
    private val subscriptions = ConcurrentHashMap<String, LinkedHashSet<String>>()
    private val lock = Any()

    /** Register a connected client by its client id. */
    fun connect(c: MqttClientDescriptor) {
        clients[c.clientId] = c
    }

    /** Remove a connected client (no-op if absent). */
    fun disconnect(clientId: String) {
        clients.remove(clientId)
    }

    /** All currently-connected clients (unordered, mirroring C# `.Values.ToArray()`). */
    val connectedClients: List<MqttClientDescriptor>
        get() = clients.values.toList()

    /**
     * Add a subscription [topicFilter] for [clientId]. Both must be non-blank
     * (mirrors C# `ArgumentException`).
     */
    fun subscribe(clientId: String, topicFilter: String) {
        require(clientId.isNotBlank()) { "clientId required" }
        require(topicFilter.isNotBlank()) { "topicFilter required" }
        synchronized(lock) {
            subscriptions.getOrPut(clientId) { LinkedHashSet() }.add(topicFilter)
        }
    }

    /**
     * MQTT topic match: does [topic] satisfy [topicFilter]? `#` matches the rest,
     * `+` matches one level, otherwise levels must be equal and counts must match.
     * Empty inputs never match (mirrors C#).
     */
    fun matches(topic: String, topicFilter: String): Boolean {
        if (topic.isEmpty() || topicFilter.isEmpty()) return false
        val t = topic.split('/')
        val f = topicFilter.split('/')
        for (i in f.indices) {
            if (f[i] == "#") return true
            if (i >= t.size) return false
            if (f[i] == "+") continue
            if (f[i] != t[i]) return false
        }
        return t.size == f.size
    }

    /** Pin a retained message to its topic. */
    fun publishRetained(m: MqttRetainedMessage) {
        retained[m.topic] = m
    }

    /** The retained message for [topic], or null if none. */
    fun getRetained(topic: String): MqttRetainedMessage? = retained[topic]

    /**
     * Client ids whose subscription set contains at least one filter matching
     * [topic]. Snapshot under the lock, matching outside is fine because the map is
     * copied first.
     */
    fun matchingSubscribers(topic: String): List<String> {
        val snapshot: List<Pair<String, List<String>>> = synchronized(lock) {
            subscriptions.map { (k, v) -> k to v.toList() }
        }
        return snapshot
            .filter { (_, filters) -> filters.any { matches(topic, it) } }
            .map { it.first }
    }
}

// ===========================================================================
// IMqttClient  (injected broker contract for MqttNetworkTransport)
// ===========================================================================

/** A single inbound MQTT application message: its topic and raw payload. */
data class MqttMessage(val topic: String, val payload: ByteArray) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is MqttMessage) return false
        return topic == other.topic && payload.contentEquals(other.payload)
    }

    override fun hashCode(): Int = 31 * topic.hashCode() + payload.contentHashCode()
}

/**
 * The injected stand-in for MQTTnet's IMqttClient. [onMessage] is the analogue of
 * the `ApplicationMessageReceivedAsync` event — the transport installs a handler and
 * the client invokes it for every inbound message. Implementations must preserve the
 * publish topic + QoS exactly as handed in.
 */
interface IMqttClient : AutoCloseable {
    /** Whether the client is connected to the broker. */
    val isConnected: Boolean

    /** Callback invoked for each inbound message. Installed by the transport. */
    var onMessage: (suspend (MqttMessage) -> Unit)?

    /** Connect to the broker using [descriptor]. */
    suspend fun connect(descriptor: MqttClientDescriptor)

    /** Subscribe to [topicFilter] at [qos]. */
    suspend fun subscribe(topicFilter: String, qos: MqttQos)

    /** Publish [payload] to [topic] at [qos]. */
    suspend fun publish(topic: String, payload: ByteArray, qos: MqttQos)

    /** Disconnect from the broker. */
    suspend fun disconnect()

    override fun close() {}
}

// ===========================================================================
// InMemoryMqttClient  (deterministic broker-backed stand-in)
// ===========================================================================

/**
 * A deterministic [IMqttClient] backed by a shared [InMemoryMqttBroker]. Multiple
 * clients on the same broker exchange messages in-process: [publish] fans the
 * message out to every OTHER connected client whose subscriptions match the topic
 * (loopback, not echo — a client never receives its own publish). No real network.
 *
 * The client registers itself with the broker on [connect] using its
 * [MqttClientDescriptor] and deregisters on [disconnect]/[close]. Subscriptions are
 * recorded in the broker so [InMemoryMqttBroker.matchingSubscribers] drives routing.
 */
class InMemoryMqttClient(
    private val broker: InMemoryMqttBroker,
) : IMqttClient {

    // Registry of all in-process clients on this broker keyed by client id, so a
    // publisher can deliver into a subscriber's message handler. Shared per-broker.
    private val descriptorHolder = ClientTable.of(broker)

    @Volatile private var descriptor: MqttClientDescriptor? = null
    @Volatile private var connected = false
    @Volatile override var onMessage: (suspend (MqttMessage) -> Unit)? = null

    override val isConnected: Boolean get() = connected

    override suspend fun connect(descriptor: MqttClientDescriptor) {
        this.descriptor = descriptor
        broker.connect(descriptor)
        descriptorHolder[descriptor.clientId] = this
        connected = true
    }

    override suspend fun subscribe(topicFilter: String, qos: MqttQos) {
        val id = descriptor?.clientId ?: error("MQTT client is not connected.")
        broker.subscribe(id, topicFilter)
    }

    override suspend fun publish(topic: String, payload: ByteArray, qos: MqttQos) {
        check(connected) { "MQTT client is not connected." }
        val selfId = descriptor?.clientId
        // Deliver to every matching subscriber except the publisher itself.
        for (clientId in broker.matchingSubscribers(topic)) {
            if (clientId == selfId) continue
            val target = descriptorHolder[clientId] ?: continue
            target.onMessage?.invoke(MqttMessage(topic, payload))
        }
    }

    override suspend fun disconnect() {
        val id = descriptor?.clientId
        connected = false
        if (id != null) {
            broker.disconnect(id)
            descriptorHolder.remove(id)
        }
    }

    override fun close() {
        val id = descriptor?.clientId
        connected = false
        if (id != null) {
            broker.disconnect(id)
            descriptorHolder.remove(id)
        }
    }

    /** Per-broker table of live in-process clients (identity-keyed on the broker). */
    private object ClientTable {
        private val tables = ConcurrentHashMap<InMemoryMqttBroker, ConcurrentHashMap<String, InMemoryMqttClient>>()
        fun of(broker: InMemoryMqttBroker): ConcurrentHashMap<String, InMemoryMqttClient> =
            tables.getOrPut(broker) { ConcurrentHashMap() }
    }
}

// ===========================================================================
// MqttNetworkTransport  (MqttNetworkTransport.cs)
// ===========================================================================

/**
 * [INetworkTransport] backed by an MQTT broker. Publishes to
 * `circle/payloads/{destinationId}` (or `circle/payloads/broadcast` when there is no
 * destination) and subscribes to `circle/payloads/{localClientId}/#`.
 *
 * QoS mirrors the C# reference exactly: [MqttQos.ExactlyOnce] when the payload's
 * priority is >= [MessagePriority.High], otherwise [MqttQos.AtLeastOnce].
 * Availability tracks the injected client's connection.
 *
 * @param client injected broker connection (stand-in for MQTTnet IMqttClient).
 * @param brokerHost broker host name.
 * @param port broker port.
 * @param clientId this client's id — also the subscription root.
 * @param username optional broker credentials (recorded on the descriptor).
 */
class MqttNetworkTransport(
    private val client: IMqttClient,
    private val brokerHost: String,
    private val port: Int,
    private val clientId: String,
    @Suppress("unused") private val username: String? = null,
    @Suppress("unused") private val password: String? = null,
) : INetworkTransport, AutoCloseable {

    private val inbound = Channel<NetworkPayload>(Channel.UNLIMITED)
    private val localClientId: String = clientId

    private val descriptor = MqttClientDescriptor(
        clientId = clientId,
        host = brokerHost,
        port = port,
        useTls = false,
        keepAlive = Duration.ofSeconds(15),
    )

    init {
        // Install the inbound handler synchronously so a message delivered the
        // instant the client connects lands in the UNBOUNDED inbox, never the void.
        client.onMessage = { msg ->
            inbound.trySend(NetworkPayload.create(msg.payload))
        }
    }

    override val kind: TransportKind get() = TransportKind.Mqtt
    override val isAvailable: Boolean get() = client.isConnected

    override suspend fun start() {
        client.connect(descriptor)
        client.subscribe("circle/payloads/$localClientId/#", MqttQos.AtLeastOnce)
    }

    override suspend fun stop() {
        client.disconnect()
        inbound.close()
    }

    /**
     * Publish the payload to `circle/payloads/{dest}` (or `.../broadcast`). QoS is
     * ExactlyOnce for High+ priority, else AtLeastOnce — matching C#.
     */
    override suspend fun send(payload: NetworkPayload) {
        val dest = payload.destinationId
        val topic = if (!dest.isNullOrEmpty()) {
            "circle/payloads/$dest"
        } else {
            "circle/payloads/broadcast"
        }
        val qos = if (payload.priority >= MessagePriority.High) MqttQos.ExactlyOnce else MqttQos.AtLeastOnce
        client.publish(topic, payload.data, qos)
    }

    override fun receive(): Flow<NetworkPayload> = flow {
        for (p in inbound) emit(p)
    }

    override fun close() {
        client.onMessage = null
        client.close()
    }
}
