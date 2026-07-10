// NetworkingMqtt.swift
//
// Port of CircleAI.Networking.Mqtt (the C# reference) — the MQTT network
// transport. Collapses the C# folder's two files (MqttNetworkTransport.cs /
// MqttTransportCommons.cs) into this single Swift file per the tree's flat
// convention.
//
// Ported types (1:1 with the C# under src/CircleAI.Networking.Mqtt/):
//   Enum      — MqttQos
//   DTOs      — MqttTopicDescriptor, MqttRetainedMessage, MqttClientDescriptor
//   Broker    — InMemoryMqttBroker (subscription matcher + retained store)
//   Transport — MqttNetworkTransport (INetworkTransport) + IMqttClientSocket
//
// Injected-socket note — the C# MqttNetworkTransport wraps a concrete
// MQTTnet.IMqttClient (a real socket): it builds MqttClientOptions from the
// broker host/port/clientId/credentials, subscribes on connect to
// `circle/payloads/{clientId}/#`, and on ApplicationMessageReceivedAsync writes
// a NetworkPayload into an unbounded inbound Channel. SendAsync builds the
// publish topic + QoS and publishes. This port follows the task rule "inject the
// socket behind an interface; every contract gets a working deterministic
// implementation": the MQTT client is injected behind IMqttClientSocket, handed
// an IMqttInboundWriter on connect so it can push received frames. The topic
// construction (`circle/payloads/{destinationId}` / `circle/payloads/broadcast`,
// subscribe `circle/payloads/{localClientId}/#`) and the QoS selection
// (priority >= High → ExactlyOnce, else AtLeastOnce) are ported byte-for-byte
// from the C# so the wire behaviour is identical.
//
// Concurrency (same rules as Networking.swift):
//   • Snapshot continuations UNDER the NSLock and finish() OUTSIDE it — finish()
//     runs onTermination synchronously and re-enters the same non-reentrant lock.
//   • The inbound stream is single-consumer with UNBOUNDED buffering, so a frame
//     the client pushes before receive() is iterated is retained, not lost
//     (mirrors C#'s unbounded inbound Channel<NetworkPayload>).

import Foundation

// ──────────────────────────────────────────────────────────────────────────
// MqttQos (MqttTransportCommons.cs)
//
// Int-raw + Codable; ordinals follow the C# declaration order and match the
// MQTT-spec QoS levels (0/1/2), the same wire values as MQTTnet's
// MqttQualityOfServiceLevel.
// ──────────────────────────────────────────────────────────────────────────

/// MQTT Quality-of-Service level. Raw values are the on-the-wire QoS bytes
/// (0/1/2), mirroring the C# `MqttQos` explicit values.
public enum MqttQos: Int, Codable, Sendable, CaseIterable {
    case atMostOnce = 0
    case atLeastOnce = 1
    case exactlyOnce = 2
}

// ──────────────────────────────────────────────────────────────────────────
// MqttTopicDescriptor / MqttRetainedMessage / MqttClientDescriptor (records)
// ──────────────────────────────────────────────────────────────────────────

/// A topic + QoS pair (a subscription or publish descriptor). Ported from the
/// C# `MqttTopicDescriptor` record.
public struct MqttTopicDescriptor: Sendable, Equatable, Codable {
    public let topic: String
    public let qos: MqttQos

    public init(topic: String, qos: MqttQos) {
        self.topic = topic
        self.qos = qos
    }
}

/// A retained message held by the broker for a topic. Ported from the C#
/// `MqttRetainedMessage` record. `payload` is `Data` (C#'s ReadOnlyMemory<byte>);
/// `retainedAtUtc` is C#'s `DateTimeOffset`.
public struct MqttRetainedMessage: Sendable, Equatable, Codable {
    public let topic: String
    public let payload: Data
    public let retainedAtUtc: Date

    public init(topic: String, payload: Data, retainedAtUtc: Date) {
        self.topic = topic
        self.payload = payload
        self.retainedAtUtc = retainedAtUtc
    }
}

/// A connected MQTT client's descriptor. Ported from the C#
/// `MqttClientDescriptor` record. `keepAlive` is seconds (C#'s TimeSpan).
public struct MqttClientDescriptor: Sendable, Equatable, Codable {
    public let clientId: String
    public let host: String
    public let port: Int
    public let useTls: Bool
    public let keepAlive: TimeInterval

    public init(
        clientId: String,
        host: String,
        port: Int,
        useTls: Bool,
        keepAlive: TimeInterval
    ) {
        self.clientId = clientId
        self.host = host
        self.port = port
        self.useTls = useTls
        self.keepAlive = keepAlive
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryMqttBroker (MqttTransportCommons.cs)
//
// C# uses three ConcurrentDictionaries (retained / clients / subscriptions) plus
// a lock for the subscription HashSet mutations and the matching queries. Here a
// single NSLock guards all mutable state; the topic-filter `Matches` algorithm
// (`#` multi-level, `+` single-level, exact segment else) is ported
// segment-for-segment, and the connected-clients / matching-subscribers queries
// match exactly.
// ──────────────────────────────────────────────────────────────────────────

/// An in-memory MQTT broker: connected clients, retained messages, and a topic
/// subscription matcher. Ported from the C# `InMemoryMqttBroker`.
public final class InMemoryMqttBroker: @unchecked Sendable {
    private let lock = NSLock()
    private var retained: [String: MqttRetainedMessage] = [:]
    private var clients: [String: MqttClientDescriptor] = [:]
    /// clientId → set of subscribed topic filters.
    private var subscriptions: [String: Set<String>] = [:]

    public init() {}

    /// Connect (register) a client descriptor keyed by its `clientId`. Mirrors
    /// C#'s `Connect` (which does `ArgumentNullException.ThrowIfNull`; the Swift
    /// value type can't be nil so that guard is vacuous here).
    public func connect(_ c: MqttClientDescriptor) {
        lock.lock(); clients[c.clientId] = c; lock.unlock()
    }

    /// Disconnect (remove) the client with `clientId`.
    public func disconnect(_ clientId: String) {
        lock.lock(); clients[clientId] = nil; lock.unlock()
    }

    /// Every connected client descriptor (order unspecified, matching C#'s
    /// `Values.ToArray()`).
    public var connectedClients: [MqttClientDescriptor] {
        lock.lock(); defer { lock.unlock() }
        return Array(clients.values)
    }

    /// Subscribe `clientId` to `topicFilter`. Both must be non-empty/non-blank
    /// (matches C#'s `ArgumentException` guards).
    public func subscribe(_ clientId: String, _ topicFilter: String) throws {
        if clientId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw MqttBrokerError.argument("clientId required")
        }
        if topicFilter.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw MqttBrokerError.argument("topicFilter required")
        }
        lock.lock()
        subscriptions[clientId, default: []].insert(topicFilter)
        lock.unlock()
    }

    /// True when `topic` matches the MQTT `topicFilter`. Ported segment-for-segment
    /// from C#'s `Matches`: `#` matches the rest of the topic (multi-level), `+`
    /// matches exactly one level, any other segment must be byte-equal, and the
    /// lengths must match when the filter runs out without a `#`. Empty topic or
    /// filter → false.
    public func matches(_ topic: String, _ topicFilter: String) -> Bool {
        if topic.isEmpty || topicFilter.isEmpty { return false }
        let t = topic.components(separatedBy: "/")
        let f = topicFilter.components(separatedBy: "/")
        for i in 0..<f.count {
            if f[i] == "#" { return true }
            if i >= t.count { return false }
            if f[i] == "+" { continue }
            if f[i] != t[i] { return false }
        }
        return t.count == f.count
    }

    /// Store (or replace) a retained message for its topic. Mirrors C#'s
    /// `PublishRetained`.
    public func publishRetained(_ m: MqttRetainedMessage) {
        lock.lock(); retained[m.topic] = m; lock.unlock()
    }

    /// The retained message for `topic`, or nil (matches C#'s
    /// `GetValueOrDefault`).
    public func getRetained(_ topic: String) -> MqttRetainedMessage? {
        lock.lock(); defer { lock.unlock() }
        return retained[topic]
    }

    /// The client ids whose subscriptions match `topic`. Mirrors C#'s
    /// `MatchingSubscribers` (any filter of a client matches → the client id is
    /// included). Order is unspecified (C# iterates a dictionary).
    public func matchingSubscribers(_ topic: String) -> [String] {
        lock.lock()
        let snapshot = subscriptions
        lock.unlock()
        return snapshot
            .filter { _, filters in filters.contains { matches(topic, $0) } }
            .map { $0.key }
    }
}

/// Errors thrown by `InMemoryMqttBroker`. `argument` is the analogue of C#'s
/// `ArgumentException`.
public enum MqttBrokerError: Error, Equatable, Sendable {
    case argument(String)
}

// ──────────────────────────────────────────────────────────────────────────
// IMqttInboundWriter / IMqttClientSocket (MqttNetworkTransport.cs)
//
// The injected socket seam (the Swift analogue of MQTTnet.IMqttClient). On
// connect the transport hands the socket an IMqttInboundWriter so the socket can
// push received application messages (the analogue of the
// ApplicationMessageReceivedAsync event → inbound Channel). The transport builds
// the topic + QoS exactly as the C# does and calls publish().
// ──────────────────────────────────────────────────────────────────────────

/// The sink an `IMqttClientSocket` uses to push a received application-message
/// payload into the transport's inbound stream. The Swift analogue of the C#
/// `ApplicationMessageReceivedAsync` handler writing into the inbound Channel.
public protocol IMqttInboundWriter: AnyObject, Sendable {
    /// Deliver a received payload into the transport's inbound stream. Returns
    /// false once the inbound stream has been completed (stopped/disposed).
    @discardableResult
    func push(_ payload: NetworkPayload) -> Bool
}

/// One outbound publish, assembled by the transport for the injected socket.
/// Mirrors the fields the C# `MqttApplicationMessageBuilder` sets (topic, payload,
/// QoS).
public struct MqttPublishRequest: Sendable, Equatable {
    public let topic: String
    public let payload: Data
    public let qos: MqttQos

    public init(topic: String, payload: Data, qos: MqttQos) {
        self.topic = topic
        self.payload = payload
        self.qos = qos
    }
}

/// The injected MQTT client — the Swift analogue of MQTTnet's `IMqttClient`.
/// Implement per platform (or in tests). `isConnected` backs the transport's
/// `isAvailable`.
public protocol IMqttClientSocket: AnyObject {
    /// True when the client is connected (C#'s `IMqttClient.IsConnected`).
    var isConnected: Bool { get }

    /// Connect to the broker, retaining `inbound` so received messages can be
    /// pushed into the transport (C#'s `ConnectAsync`).
    func connect(inbound: IMqttInboundWriter) async throws

    /// Subscribe to `topicFilter` (C#'s `SubscribeAsync`).
    func subscribe(_ topicFilter: String) async throws

    /// Publish `request` (C#'s `PublishAsync` with the built message).
    func publish(_ request: MqttPublishRequest) async throws

    /// Disconnect from the broker (C#'s `DisconnectAsync`).
    func disconnect() async throws
}

// ──────────────────────────────────────────────────────────────────────────
// MqttNetworkTransport (MqttNetworkTransport.cs)
// ──────────────────────────────────────────────────────────────────────────

/// `INetworkTransport` backed by an MQTT broker via an injected client socket.
/// `start` connects and subscribes to `circle/payloads/{localClientId}/#`;
/// `send` publishes to `circle/payloads/{destinationId}` (or
/// `circle/payloads/broadcast` when there is no destination) at QoS ExactlyOnce
/// for High+ priority, else AtLeastOnce; `receive` drains messages the socket
/// pushes inbound; `stop` disconnects and completes the inbound stream. Topic
/// construction and QoS selection are ported byte-for-byte from the C#
/// `MqttNetworkTransport`.
public final class MqttNetworkTransport: INetworkTransport, @unchecked Sendable {
    /// The topic prefix all Circle payloads travel under. Mirrors the C# literal.
    public static let topicPrefix = "circle/payloads"
    /// The topic used when a payload has no destination id. Mirrors the C#
    /// `"circle/payloads/broadcast"`.
    public static let broadcastTopic = "circle/payloads/broadcast"

    /// The inbound sink handed to the socket. Buffers frames pushed before
    /// `receive()` is iterated (unbounded) so none are lost.
    private final class InboundWriter: IMqttInboundWriter, @unchecked Sendable {
        private let lock = NSLock()
        private var completed = false
        private var pending: [NetworkPayload] = []
        private var continuation: AsyncStream<NetworkPayload>.Continuation?

        @discardableResult
        func push(_ payload: NetworkPayload) -> Bool {
            lock.lock()
            if completed { lock.unlock(); return false }
            if let cont = continuation {
                cont.yield(payload)
            } else {
                pending.append(payload)
            }
            lock.unlock()
            return true
        }

        func stream() -> AsyncStream<NetworkPayload> {
            AsyncStream(bufferingPolicy: .unbounded) { continuation in
                lock.lock()
                if completed {
                    lock.unlock()
                    continuation.finish()
                    return
                }
                for p in pending { continuation.yield(p) }
                pending.removeAll()
                self.continuation = continuation
                lock.unlock()

                continuation.onTermination = { [weak self] _ in
                    guard let self else { return }
                    self.lock.lock(); self.continuation = nil; self.lock.unlock()
                }
            }
        }

        func complete() {
            lock.lock()
            completed = true
            let cont = continuation
            continuation = nil
            pending.removeAll()
            lock.unlock()
            cont?.finish()
        }
    }

    private let socket: IMqttClientSocket
    private let localClientId: String
    private let inbound = InboundWriter()

    /// - Parameters:
    ///   - socket: the injected MQTT client (the socket seam).
    ///   - clientId: this client's id; used both to identify the client and to
    ///     build the subscription topic `circle/payloads/{clientId}/#`.
    ///
    /// The C# constructor also takes brokerHost/port/username/password to build
    /// MqttClientOptions; those belong to the injected socket's construction in
    /// this port (the socket owns the connection parameters), so the transport
    /// only needs the clientId that drives topic construction.
    public init(socket: IMqttClientSocket, clientId: String) {
        self.socket = socket
        self.localClientId = clientId
    }

    public var kind: TransportKind { .mqtt }

    /// Mirrors C#'s `IsAvailable => _client.IsConnected`.
    public var isAvailable: Bool { socket.isConnected }

    /// The subscription topic filter this transport subscribes to on start:
    /// `circle/payloads/{localClientId}/#`. Mirrors the C# `SubscribeAsync`
    /// argument.
    public var subscriptionTopic: String {
        "\(Self.topicPrefix)/\(localClientId)/#"
    }

    /// Connect, then subscribe to `circle/payloads/{localClientId}/#` (C#'s
    /// `ConnectAsync` then `SubscribeAsync`).
    public func start() async throws {
        try await socket.connect(inbound: inbound)
        try await socket.subscribe(subscriptionTopic)
    }

    /// Disconnect, then complete the inbound stream (C#'s `DisconnectAsync` then
    /// `_inbound.Writer.TryComplete()`).
    public func stop() async throws {
        try await socket.disconnect()
        inbound.complete()
    }

    /// Publish the payload. The topic is `circle/payloads/{destinationId}` when a
    /// destination is set, else `circle/payloads/broadcast`; the QoS is
    /// ExactlyOnce for High+ priority, else AtLeastOnce. Ported byte-for-byte from
    /// the C# `SendAsync`.
    public func send(_ payload: NetworkPayload) async throws {
        let topic: String
        if let dest = payload.destinationId, !dest.isEmpty {
            topic = "\(Self.topicPrefix)/\(dest)"
        } else {
            topic = Self.broadcastTopic
        }
        let qos: MqttQos = payload.priority >= .high ? .exactlyOnce : .atLeastOnce
        try await socket.publish(MqttPublishRequest(topic: topic, payload: payload.data, qos: qos))
    }

    /// Yields inbound payloads the socket pushed. Mirrors C#'s
    /// `_inbound.Reader.ReadAllAsync(ct)`.
    public func receive() -> AsyncStream<NetworkPayload> {
        inbound.stream()
    }
}
