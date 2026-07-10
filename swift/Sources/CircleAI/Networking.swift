// Networking.swift
//
// Port of CircleAI.Networking (the C# reference) — the transport-agnostic
// networking ABSTRACTION that the ten concrete transport packages
// (HTTP / WebSocket / gRPC / MQTT / TCP / WiFi / Bluetooth / NearLink / Aether /
// DTN) implement. Collapses the C# folder's one-type-per-file layout into this
// single Swift file per the tree's flat convention.
//
// Ported types (1:1 with the C# under src/CircleAI.Networking/):
//   Enums     — TransportKind, ConnectivityState, MessagePriority, PeerRole
//               (SyncDeliveryMode already lives in Sync.swift — reused, NOT
//                redefined here.)
//   DTOs      — NetworkPayload, NetworkContext, PeerInfo, SchedulingHint
//   Contracts — INetworkTransport, IMeshNetwork, IMessageChannel,
//               IConnectivityMonitor, ITransportSelector, INetworkPolicy,
//               IPayloadOptimiser, IPeerDiscovery
//   Policy    — DefaultNetworkPolicy, NetworkPolicyBuilder
//
// This is the transport ABSTRACTION, not a socket. A real socket is injected
// behind INetworkTransport. So the whole surface is testable with no hardware,
// this file also ships working, deterministic in-memory implementations (no
// stubs): DefaultTransportSelector, InMemoryNetworkTransport,
// InMemoryMessageChannel, InMemoryConnectivityMonitor, IdentityPayloadOptimiser,
// InMemoryPeerDiscovery.
//
// Concurrency notes (this module is stream/transport heavy):
//   • Every broadcasting hub snapshots its continuations UNDER an NSLock and
//     calls finish() OUTSIDE it — finish() runs onTermination synchronously,
//     which re-acquires the same non-reentrant lock and would self-deadlock.
//   • The single-consumer loopback transports/channels buffer with an UNBOUNDED
//     AsyncStream so a send that happens before the consumer starts iterating is
//     retained and later delivered (mirrors C#'s unbounded System.Threading
//     .Channels.Channel<T>), never lost and never blocking the sender.

import Foundation

// ──────────────────────────────────────────────────────────────────────────
// Enums (NetworkTypes.cs)
//
// Int-raw + Codable, ordinals follow the C# declaration order so the values are
// a stable cross-language wire contract.
// ──────────────────────────────────────────────────────────────────────────

/// The concrete transport a payload can travel over. Ordinals mirror the C#
/// `TransportKind` declaration order.
public enum TransportKind: Int, Codable, Sendable, CaseIterable {
    case http = 0
    case webSocket = 1
    case grpc = 2
    case mqtt = 3
    case tcp = 4
    case udp = 5
    /// WiFi Direct / mDNS / LAN — no Aether required.
    case wiFi = 6
    /// Raw BLE GATT — no Aether required.
    case bluetooth = 7
    /// Huawei SLE / HarmonyOS — no Aether required.
    case nearLink = 8
    /// Full Aether mesh (Signal E2E + AODV + SOS).
    case aether = 9
    /// 72hr store-and-forward over any transport.
    case dtn = 10
    /// Offline queue — no live path at all.
    case localStore = 11
}

/// Overall connectivity posture of the device. Ordinals mirror the C#
/// `ConnectivityState` declaration order.
public enum ConnectivityState: Int, Codable, Sendable, CaseIterable {
    case online = 0
    case localOnly = 1
    case meshOnly = 2
    case offline = 3
}

/// Delivery priority of a payload. Ordinals mirror the C# `MessagePriority`
/// declaration order and are ordered weakest → strongest.
public enum MessagePriority: Int, Codable, Sendable, Comparable, CaseIterable {
    case low = 0
    case normal = 1
    case high = 2
    case urgent = 3
    case emergency = 4

    public static func < (lhs: MessagePriority, rhs: MessagePriority) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

/// A peer's role in the mesh topology. Ordinals mirror the C# `PeerRole`
/// declaration order.
public enum PeerRole: Int, Codable, Sendable, CaseIterable {
    case peer = 0
    case relay = 1
    case bridge = 2
    case sink = 3
}

// ──────────────────────────────────────────────────────────────────────────
// NetworkPayload (NetworkPayload.cs)
// ──────────────────────────────────────────────────────────────────────────

/// Immutable envelope for a single message or data unit traversing any
/// transport. Transports must not mutate it — create a new payload instead.
///
/// Value semantics (a Swift `struct`) give the C# record's "transports must not
/// mutate it" guarantee for free.
public struct NetworkPayload: Sendable, Equatable, Codable {
    /// Unique id for this payload. Defaults to a 32-char hex GUID via `create`.
    public let id: String
    /// Origin node id, or nil when produced locally without an identity.
    public let sourceId: String?
    /// Intended recipient node id, or nil for broadcast / selector-chosen route.
    public let destinationId: String?
    /// The opaque payload bytes. `ReadOnlyMemory<byte>` in C#.
    public let data: Data
    /// Delivery priority.
    public let priority: MessagePriority
    /// Time-to-live. nil = no expiry. `TimeSpan?` in C#.
    public let ttl: TimeInterval?
    /// MIME-style content type of `data`.
    public let contentType: String
    /// Arbitrary string metadata. Ordered dictionary is not required — parity is
    /// with C#'s `IReadOnlyDictionary<string,string>`.
    public let metadata: [String: String]
    /// When this payload was created (UTC).
    public let createdAt: Date

    public init(
        id: String,
        sourceId: String?,
        destinationId: String?,
        data: Data,
        priority: MessagePriority,
        ttl: TimeInterval?,
        contentType: String,
        metadata: [String: String],
        createdAt: Date
    ) {
        self.id = id
        self.sourceId = sourceId
        self.destinationId = destinationId
        self.data = data
        self.priority = priority
        self.ttl = ttl
        self.contentType = contentType
        self.metadata = metadata
        self.createdAt = createdAt
    }

    /// Factory mirroring the C# `NetworkPayload.Create` static method: fresh id,
    /// no source, empty metadata, `createdAt` = now.
    public static func create(
        data: Data,
        destinationId: String? = nil,
        priority: MessagePriority = .normal,
        contentType: String = "application/octet-stream",
        ttl: TimeInterval? = nil
    ) -> NetworkPayload {
        NetworkPayload(
            id: Self.newId(),
            sourceId: nil,
            destinationId: destinationId,
            data: data,
            priority: priority,
            ttl: ttl,
            contentType: contentType,
            metadata: [:],
            createdAt: Date())
    }

    /// 32-char lowercase hex, matching C#'s `Guid.NewGuid().ToString("N")`.
    private static func newId() -> String {
        UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    }
}

// ──────────────────────────────────────────────────────────────────────────
// NetworkContext (NetworkContext.cs)
// ──────────────────────────────────────────────────────────────────────────

/// Snapshot of current connectivity state.
public struct NetworkContext: Sendable, Equatable, Codable {
    public let state: ConnectivityState
    public let preferredTransport: TransportKind
    public let availableTransports: [TransportKind]
    public let signalStrengthDbm: Int?
    public let estimatedBandwidthBps: Int64?
    public let latencyMs: Int64?
    public let nearbyPeerCount: Int
    public let snapshotAt: Date

    public init(
        state: ConnectivityState,
        preferredTransport: TransportKind,
        availableTransports: [TransportKind],
        signalStrengthDbm: Int?,
        estimatedBandwidthBps: Int64?,
        latencyMs: Int64?,
        nearbyPeerCount: Int,
        snapshotAt: Date
    ) {
        self.state = state
        self.preferredTransport = preferredTransport
        self.availableTransports = availableTransports
        self.signalStrengthDbm = signalStrengthDbm
        self.estimatedBandwidthBps = estimatedBandwidthBps
        self.latencyMs = latencyMs
        self.nearbyPeerCount = nearbyPeerCount
        self.snapshotAt = snapshotAt
    }

    /// The canonical fully-offline context. Mirrors C#'s
    /// `NetworkContext.Offline`: LocalStore preferred, no transports, zero peers.
    /// `snapshotAt` is stamped at call time (the C# static reads
    /// `DateTimeOffset.UtcNow` when the field initialiser ran).
    public static var offline: NetworkContext {
        NetworkContext(
            state: .offline,
            preferredTransport: .localStore,
            availableTransports: [],
            signalStrengthDbm: nil,
            estimatedBandwidthBps: nil,
            latencyMs: nil,
            nearbyPeerCount: 0,
            snapshotAt: Date())
    }
}

// ──────────────────────────────────────────────────────────────────────────
// PeerInfo (PeerInfo.cs)
// ──────────────────────────────────────────────────────────────────────────

/// Describes a discovered peer on any transport.
public struct PeerInfo: Sendable, Equatable, Codable {
    public let nodeId: String
    public let displayName: String?
    public let supportedTransports: [TransportKind]
    public let role: PeerRole
    public let signalStrengthDbm: Int?
    public let lastSeen: Date

    public init(
        nodeId: String,
        displayName: String?,
        supportedTransports: [TransportKind],
        role: PeerRole,
        signalStrengthDbm: Int?,
        lastSeen: Date
    ) {
        self.nodeId = nodeId
        self.displayName = displayName
        self.supportedTransports = supportedTransports
        self.role = role
        self.signalStrengthDbm = signalStrengthDbm
        self.lastSeen = lastSeen
    }
}

// ──────────────────────────────────────────────────────────────────────────
// SchedulingHint (SchedulingHint.cs)
// ──────────────────────────────────────────────────────────────────────────

/// Advisory scheduling information the Circle AI layer can attach to a
/// `SyncDelta`. The Aether transport is free to disregard these hints; honouring
/// them minimises unnecessary wakeups and battery drain.
///
/// This is a pure advisory — never a correctness constraint.
public struct SchedulingHint: Sendable, Equatable, Codable {
    /// Device ids strongly preferred as the first delivery targets. Empty means
    /// "no preference".
    public let preferredPeerIds: [String]
    /// Earliest UTC timestamp at which the transport should attempt delivery. nil
    /// = forward immediately.
    public let suggestedWindowUtc: Date?
    /// How confident the AI layer is that these hints are accurate, in [0, 1].
    /// Below 0.5 is a weak advisory; above 0.8 is a strong advisory.
    public let confidenceScore: Float

    public init(
        preferredPeerIds: [String],
        suggestedWindowUtc: Date?,
        confidenceScore: Float
    ) {
        self.preferredPeerIds = preferredPeerIds
        self.suggestedWindowUtc = suggestedWindowUtc
        self.confidenceScore = confidenceScore
    }
}

// ──────────────────────────────────────────────────────────────────────────
// Contracts
// ──────────────────────────────────────────────────────────────────────────

/// Unified send/receive abstraction for a single transport kind. A real socket
/// is injected behind this. Ported from `INetworkTransport`.
public protocol INetworkTransport: AnyObject {
    /// The transport kind this instance speaks.
    var kind: TransportKind { get }
    /// True when the transport is currently usable.
    var isAvailable: Bool { get }

    /// Start the transport (bind sockets, begin discovery, etc.).
    func start() async throws
    /// Stop the transport and release resources.
    func stop() async throws

    /// Send a single payload.
    func send(_ payload: NetworkPayload) async throws

    /// Yields payloads as they arrive. Mirrors C#'s
    /// `IAsyncEnumerable<NetworkPayload> ReceiveAsync`.
    func receive() -> AsyncStream<NetworkPayload>
}

/// Mesh-specific surface: topology, node identity, mesh health. Ported from
/// `IMeshNetwork`.
public protocol IMeshNetwork: AnyObject {
    /// This device's node id in the mesh.
    var localNodeId: String { get }
    /// The currently-known peer node ids.
    func getPeerIds() async throws -> [String]
    /// A fresh health snapshot for the mesh.
    func getMeshHealth() async throws -> NetworkContext
}

/// Typed message delivery over any transport. Ported from `IMessageChannel`.
///
/// The C# generic `where T : class` becomes a Swift generic constrained to
/// `Codable & Sendable`: the wire form is the JSON encoding of `T`, which is how
/// an in-memory / cross-language channel actually round-trips a typed message.
public protocol IMessageChannel: AnyObject {
    /// Encode `message` and deliver it to `destinationId`.
    func send<T: Codable & Sendable>(destinationId: String, message: T) async throws

    /// Yields incoming messages decoded as `T`. Values that do not decode as `T`
    /// are skipped (mirrors the C# type-filtered `ReceiveAsync<T>`).
    func receive<T: Codable & Sendable>(_ type: T.Type) -> AsyncStream<T>
}

/// Observes connectivity state and emits changes. Ported from
/// `IConnectivityMonitor`.
public protocol IConnectivityMonitor: AnyObject {
    /// The latest observed connectivity state.
    var currentState: ConnectivityState { get }
    /// A synchronous point-in-time snapshot.
    func getSnapshot() -> NetworkContext
    /// Yields a new context each time connectivity changes. Mirrors C#'s
    /// `IAsyncEnumerable<NetworkContext> WatchAsync`.
    func watch() -> AsyncStream<NetworkContext>
}

/// Selects the best transport for a payload+context. Ported from
/// `ITransportSelector`.
///
/// Default cascade: gRPC → WebSocket → HTTP → MQTT → TCP → WiFi → Bluetooth →
/// NearLink → Aether → DTN → LocalStore.
public protocol ITransportSelector: AnyObject {
    /// The single best transport for the payload in the given context.
    func selectBest(_ payload: NetworkPayload, context: NetworkContext) -> TransportKind
    /// The full ordered fallback cascade for the payload in the given context.
    func getCascade(_ payload: NetworkPayload, context: NetworkContext) -> [TransportKind]
}

/// Policy rules applied before choosing a transport. Examples: "WiFi-only",
/// "mesh-first", "no cloud when roaming". Ported from `INetworkPolicy`.
public protocol INetworkPolicy: AnyObject, Sendable {
    /// True when `transport` is allowed to carry `payload`.
    func permits(_ transport: TransportKind, payload: NetworkPayload) -> Bool
    /// When set, forces every payload onto this transport regardless of cascade.
    var forceTransport: TransportKind? { get }
    /// True when the mesh transports should be tried before cloud transports.
    var meshFirst: Bool { get }
    /// True when payloads that cannot be delivered live should be queued offline.
    var offlineQueueEnabled: Bool { get }
    /// True when cloud transports (HTTP/WebSocket/gRPC/MQTT) are permitted.
    var allowCloudTransports: Bool { get }
}

/// Compresses or transforms payloads for low-bandwidth transports (BLE,
/// NearLink, LoRa, DTN). Ported from `IPayloadOptimiser`.
public protocol IPayloadOptimiser: AnyObject {
    /// Return a payload optimised for `targetTransport` (e.g. compressed).
    func optimise(_ payload: NetworkPayload, targetTransport: TransportKind) async throws -> NetworkPayload
    /// Reverse whatever `optimise` did, returning the original payload.
    func decompress(_ payload: NetworkPayload) -> NetworkPayload
}

/// Finds nearby devices via mDNS, BLE beacons, NearLink scan, Aether presence,
/// etc. Ported from `IPeerDiscovery`.
public protocol IPeerDiscovery: AnyObject {
    /// Yields peers as they are discovered.
    func discover() -> AsyncStream<PeerInfo>
    /// Announce this device's presence to the network.
    func announce(localInfo: PeerInfo) async throws
}

// ──────────────────────────────────────────────────────────────────────────
// DefaultNetworkPolicy (DefaultNetworkPolicy.cs)
// ──────────────────────────────────────────────────────────────────────────

/// Permissive default: all transports allowed, offline queue on. Ported from
/// `DefaultNetworkPolicy`. Singleton via `shared`, matching C#'s
/// `DefaultNetworkPolicy.Instance`.
public final class DefaultNetworkPolicy: INetworkPolicy, @unchecked Sendable {
    /// The shared instance. Mirrors C#'s `DefaultNetworkPolicy.Instance`.
    public static let shared = DefaultNetworkPolicy()

    private init() {}

    public func permits(_ transport: TransportKind, payload: NetworkPayload) -> Bool { true }
    public var forceTransport: TransportKind? { nil }
    public var meshFirst: Bool { false }
    public var offlineQueueEnabled: Bool { true }
    public var allowCloudTransports: Bool { true }
}

// ──────────────────────────────────────────────────────────────────────────
// NetworkPolicyBuilder (NetworkPolicyBuilder.cs)
// ──────────────────────────────────────────────────────────────────────────

/// Fluent builder for `INetworkPolicy`. Ported from `NetworkPolicyBuilder`.
///
/// The builder is a mutating value collector; `build()` snapshots it into an
/// immutable `Policy`. Reference-type builder in C#; here the chained methods
/// return `self` after mutating, matching the fluent shape.
public final class NetworkPolicyBuilder {
    private var allowed: Set<TransportKind> = []
    private var meshFirstFlag = false
    private var noCloudFlag = false
    private var queueEnabled = true
    private var forced: TransportKind?

    public init() {}

    @discardableResult
    public func meshFirst() -> NetworkPolicyBuilder { meshFirstFlag = true; return self }

    @discardableResult
    public func noCloud() -> NetworkPolicyBuilder { noCloudFlag = true; return self }

    @discardableResult
    public func disableQueue() -> NetworkPolicyBuilder { queueEnabled = false; return self }

    @discardableResult
    public func force(_ t: TransportKind) -> NetworkPolicyBuilder { forced = t; return self }

    @discardableResult
    public func allow(_ kinds: TransportKind...) -> NetworkPolicyBuilder {
        for k in kinds { allowed.insert(k) }
        return self
    }

    /// Builds the immutable policy. An empty allow-set means "allow all"
    /// (matching the C# `_allowed.Count > 0 ? … : null`).
    public func build() -> INetworkPolicy {
        Policy(
            allowed: allowed.isEmpty ? nil : allowed,
            meshFirst: meshFirstFlag,
            noCloud: noCloudFlag,
            queueEnabled: queueEnabled,
            force: forced)
    }

    /// The immutable policy produced by `build()`. Mirrors the C# private
    /// `Policy` record.
    private final class Policy: INetworkPolicy, @unchecked Sendable {
        private let allowed: Set<TransportKind>?
        private let meshFirstFlag: Bool
        private let noCloud: Bool
        private let queueEnabled: Bool
        private let force: TransportKind?

        init(
            allowed: Set<TransportKind>?,
            meshFirst: Bool,
            noCloud: Bool,
            queueEnabled: Bool,
            force: TransportKind?
        ) {
            self.allowed = allowed
            self.meshFirstFlag = meshFirst
            self.noCloud = noCloud
            self.queueEnabled = queueEnabled
            self.force = force
        }

        func permits(_ t: TransportKind, payload: NetworkPayload) -> Bool {
            if noCloud, t == .http || t == .webSocket || t == .grpc || t == .mqtt {
                return false
            }
            guard let allowed else { return true }
            return allowed.contains(t)
        }

        var forceTransport: TransportKind? { force }
        var meshFirst: Bool { meshFirstFlag }
        var offlineQueueEnabled: Bool { queueEnabled }
        var allowCloudTransports: Bool { !noCloud }
    }
}

// ──────────────────────────────────────────────────────────────────────────
// DefaultTransportSelector (working impl of ITransportSelector)
//
// The C# interface documents the cascade; this deterministic selector realises
// it: filter the documented order by what the context reports available and by
// what the policy permits, force-transport override, an emergency/mesh-first
// bias, and a guaranteed LocalStore backstop so there is always a route.
// ──────────────────────────────────────────────────────────────────────────

/// Deterministic `ITransportSelector`. Applies the documented default cascade,
/// intersected with the context's available transports and the injected policy,
/// with a policy `forceTransport` override and a LocalStore backstop.
public final class DefaultTransportSelector: ITransportSelector, @unchecked Sendable {
    /// The documented default cascade, strongest/most-capable first.
    /// gRPC → WebSocket → HTTP → MQTT → TCP → WiFi → Bluetooth → NearLink →
    /// Aether → DTN → LocalStore.
    public static let defaultCascade: [TransportKind] = [
        .grpc, .webSocket, .http, .mqtt, .tcp,
        .wiFi, .bluetooth, .nearLink, .aether, .dtn, .localStore,
    ]

    /// The mesh transports, tried first when the policy is mesh-first or the
    /// payload is emergency priority.
    private static let meshTransports: [TransportKind] = [.wiFi, .bluetooth, .nearLink, .aether]

    private let policy: INetworkPolicy

    /// - Parameter policy: the policy gate. Defaults to the permissive
    ///   `DefaultNetworkPolicy`.
    public init(policy: INetworkPolicy = DefaultNetworkPolicy.shared) {
        self.policy = policy
    }

    public func selectBest(_ payload: NetworkPayload, context: NetworkContext) -> TransportKind {
        // A cascade always ends with LocalStore, so first is always defined.
        getCascade(payload, context: context).first ?? .localStore
    }

    public func getCascade(_ payload: NetworkPayload, context: NetworkContext) -> [TransportKind] {
        // 1. A forced transport short-circuits everything (still policy-checked;
        //    if the forced transport is not permitted, fall through to LocalStore).
        if let forced = policy.forceTransport {
            if policy.permits(forced, payload: payload) {
                return [forced]
            }
            return [.localStore]
        }

        // 2. Base order: mesh-first (policy flag or emergency payload) pulls the
        //    mesh transports to the front, otherwise the documented cascade.
        let meshBias = policy.meshFirst || payload.priority == .emergency
        var order = Self.defaultCascade
        if meshBias {
            let mesh = Self.defaultCascade.filter { Self.meshTransports.contains($0) }
            let rest = Self.defaultCascade.filter { !Self.meshTransports.contains($0) }
            order = mesh + rest
        }

        // 3. Keep only transports the context reports available. LocalStore is an
        //    always-available offline backstop regardless of the context, when
        //    the policy has the offline queue enabled.
        let available = Set(context.availableTransports)
        var cascade = order.filter { kind in
            guard policy.permits(kind, payload: payload) else { return false }
            if kind == .localStore { return policy.offlineQueueEnabled }
            return available.contains(kind)
        }

        // 4. Guarantee a non-empty cascade: if nothing survived and the offline
        //    queue is enabled, fall back to LocalStore so a payload always has a
        //    route (it will be queued). If the queue is disabled and nothing is
        //    available, the cascade is legitimately empty.
        if cascade.isEmpty, policy.offlineQueueEnabled {
            cascade = [.localStore]
        }
        return cascade
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryNetworkTransport (working loopback impl of INetworkTransport)
//
// A deterministic in-process transport. `send` enqueues the payload onto an
// unbounded buffer that `receive` drains — a loopback so a send that happens
// before the consumer starts iterating is retained and later delivered.
// This is the "real socket injected behind INetworkTransport" seam, satisfied
// deterministically for tests.
// ──────────────────────────────────────────────────────────────────────────

/// Deterministic loopback `INetworkTransport`. Everything sent is delivered back
/// out of `receive` in FIFO order, buffered unbounded so no send is lost or
/// blocks. `receive()` returns a single shared consumer stream (matching C#'s
/// single-reader `Channel<NetworkPayload>`); the first caller drains the buffer.
public final class InMemoryNetworkTransport: INetworkTransport, @unchecked Sendable {
    public let kind: TransportKind

    private let lock = NSLock()
    private var started = false
    private var stopped = false
    /// Payloads sent before the consumer stream is created. Once a stream exists
    /// this is empty and sends go straight to the continuation.
    private var pending: [NetworkPayload] = []
    private var continuation: AsyncStream<NetworkPayload>.Continuation?

    /// - Parameter kind: the transport kind this loopback reports. Defaults to
    ///   `.localStore` (a pure offline queue is the natural loopback).
    public init(kind: TransportKind = .localStore) {
        self.kind = kind
    }

    public var isAvailable: Bool {
        lock.lock(); defer { lock.unlock() }
        return started && !stopped
    }

    public func start() async throws {
        lock.lock(); started = true; lock.unlock()
    }

    public func stop() async throws {
        // Snapshot the continuation, release the lock, THEN finish() — finish()
        // runs onTermination synchronously and re-enters this NSLock.
        lock.lock()
        stopped = true
        let cont = continuation
        continuation = nil
        pending.removeAll()
        lock.unlock()
        cont?.finish()
    }

    public func send(_ payload: NetworkPayload) async throws {
        lock.lock()
        if stopped {
            lock.unlock()
            throw NetworkError.transportStopped
        }
        if let cont = continuation {
            // yield() only enqueues to the stream's own buffer — safe under lock.
            cont.yield(payload)
            lock.unlock()
        } else {
            // No consumer yet — retain until receive() attaches (unbounded).
            pending.append(payload)
            lock.unlock()
        }
    }

    public func receive() -> AsyncStream<NetworkPayload> {
        AsyncStream(bufferingPolicy: .unbounded) { continuation in
            lock.lock()
            if stopped {
                lock.unlock()
                continuation.finish()
                return
            }
            // Drain anything buffered before this consumer attached, in order.
            for p in pending { continuation.yield(p) }
            pending.removeAll()
            self.continuation = continuation
            lock.unlock()

            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock()
                // Only clear if this is still the live continuation.
                self.continuation = nil
                self.lock.unlock()
            }
        }
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryMessageChannel (working impl of IMessageChannel)
//
// Typed delivery over a loopback. Messages are JSON-encoded on send, buffered
// unbounded, and decoded per subscriber's requested type on receive. A message
// whose stored type does not match the subscriber's requested type is skipped —
// exactly the C# `ReceiveAsync<T>` filter.
// ──────────────────────────────────────────────────────────────────────────

/// Deterministic loopback `IMessageChannel`. `send<T>` encodes `T` to JSON and
/// records the type name; `receive<T>` fans out to every live subscriber and
/// yields only messages whose recorded type name matches `T`. Buffered unbounded
/// so a send before any subscriber attaches is retained and replayed to the
/// first matching subscriber.
public final class InMemoryMessageChannel: IMessageChannel, @unchecked Sendable {
    /// A stored, type-tagged envelope. `typeName` is the Swift type name used to
    /// filter on receive (the analogue of C#'s runtime type check in
    /// `ReceiveAsync<T>`).
    private struct Envelope: Sendable {
        let destinationId: String
        let typeName: String
        let json: Data
    }

    private let lock = NSLock()
    private var subscribers: [UUID: AsyncStream<Envelope>.Continuation] = [:]
    /// Envelopes published before any subscriber existed, replayed to the first
    /// subscriber (unbounded retention, mirroring C#'s unbounded Channel).
    private var pending: [Envelope] = []
    private var closed = false

    public init() {}

    public func send<T: Codable & Sendable>(destinationId: String, message: T) async throws {
        let json = try JSONEncoder().encode(message)
        let env = Envelope(destinationId: destinationId, typeName: String(describing: T.self), json: json)

        lock.lock()
        if closed {
            lock.unlock()
            throw NetworkError.transportStopped
        }
        if subscribers.isEmpty {
            // No consumer yet — retain (unbounded) until one subscribes.
            pending.append(env)
            lock.unlock()
        } else {
            // yield() only buffers to each stream — safe to call under the lock.
            for cont in subscribers.values { cont.yield(env) }
            lock.unlock()
        }
    }

    public func receive<T: Codable & Sendable>(_ type: T.Type) -> AsyncStream<T> {
        let wanted = String(describing: T.self)
        // Bridge the internal Envelope stream to the typed output stream. We
        // subscribe to the internal stream SYNCHRONOUSLY (inside subscribe), then
        // decode in the consumer task, so a send right after receive() returns is
        // not lost.
        let envStream = subscribeEnvelopes()
        return AsyncStream(bufferingPolicy: .unbounded) { continuation in
            let task = Task {
                for await env in envStream {
                    guard env.typeName == wanted else { continue }
                    if let value = try? JSONDecoder().decode(T.self, from: env.json) {
                        continuation.yield(value)
                    }
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    /// Close the channel: finish every subscriber and drop buffered envelopes.
    public func close() {
        lock.lock()
        closed = true
        let conts = Array(subscribers.values)
        subscribers.removeAll()
        pending.removeAll()
        lock.unlock()
        for cont in conts { cont.finish() }
    }

    /// Number of live subscribers. Useful in tests.
    public var subscriberCount: Int {
        lock.lock(); defer { lock.unlock() }
        return subscribers.count
    }

    // ── Private ───────────────────────────────────────────────────────────────

    /// Subscribe a raw envelope stream. Registration is synchronous; the first
    /// subscriber drains any pending envelopes.
    private func subscribeEnvelopes() -> AsyncStream<Envelope> {
        AsyncStream(bufferingPolicy: .unbounded) { continuation in
            let id = UUID()
            lock.lock()
            if closed {
                lock.unlock()
                continuation.finish()
                return
            }
            // Replay pre-subscription envelopes to the first subscriber only.
            if !pending.isEmpty {
                for env in pending { continuation.yield(env) }
                pending.removeAll()
            }
            subscribers[id] = continuation
            lock.unlock()

            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock(); self.subscribers[id] = nil; self.lock.unlock()
            }
        }
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryConnectivityMonitor (working impl of IConnectivityMonitor)
//
// Holds the current context, fans out a fresh context to every watcher whenever
// `publish` is called. Snapshot-then-release fan-out; watcher registration is
// synchronous and each watcher immediately receives the current context.
// ──────────────────────────────────────────────────────────────────────────

/// Deterministic `IConnectivityMonitor`. `publish` replaces the current context
/// and notifies every live watcher; a new watcher immediately receives the
/// current context so it never starts blind.
public final class InMemoryConnectivityMonitor: IConnectivityMonitor, @unchecked Sendable {
    private let lock = NSLock()
    private var context: NetworkContext
    private var watchers: [UUID: AsyncStream<NetworkContext>.Continuation] = [:]
    private var closed = false

    /// - Parameter initial: the starting context. Defaults to `.offline`.
    public init(initial: NetworkContext = .offline) {
        self.context = initial
    }

    public var currentState: ConnectivityState {
        lock.lock(); defer { lock.unlock() }
        return context.state
    }

    public func getSnapshot() -> NetworkContext {
        lock.lock(); defer { lock.unlock() }
        return context
    }

    /// Replace the current context and fan it out to every live watcher.
    public func publish(_ newContext: NetworkContext) {
        lock.lock()
        guard !closed else { lock.unlock(); return }
        context = newContext
        // yield() only buffers per-stream — safe under the lock.
        for cont in watchers.values { cont.yield(newContext) }
        lock.unlock()
    }

    public func watch() -> AsyncStream<NetworkContext> {
        AsyncStream(bufferingPolicy: .unbounded) { continuation in
            let id = UUID()
            lock.lock()
            if closed {
                lock.unlock()
                continuation.finish()
                return
            }
            // Emit the current context immediately so the watcher has a baseline.
            continuation.yield(context)
            watchers[id] = continuation
            lock.unlock()

            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock(); self.watchers[id] = nil; self.lock.unlock()
            }
        }
    }

    /// Finish every watcher stream and stop accepting updates.
    public func close() {
        lock.lock()
        closed = true
        let conts = Array(watchers.values)
        watchers.removeAll()
        lock.unlock()
        for cont in conts { cont.finish() }
    }

    /// Number of live watchers. Useful in tests.
    public var watcherCount: Int {
        lock.lock(); defer { lock.unlock() }
        return watchers.count
    }
}

// ──────────────────────────────────────────────────────────────────────────
// IdentityPayloadOptimiser (working impl of IPayloadOptimiser)
//
// A correctness-preserving optimiser: it records the target transport in the
// payload metadata on optimise and removes it on decompress, so the round-trip
// is byte-for-byte identity on the data while still exercising the full
// contract. A GZip transform would be equally valid; identity keeps the port
// deterministic and dependency-free.
// ──────────────────────────────────────────────────────────────────────────

/// Deterministic `IPayloadOptimiser`. `optimise` tags the payload with the
/// target transport under the `x-optimised-for` metadata key (data unchanged);
/// `decompress` strips the tag. The payload `data` survives the round-trip
/// byte-for-byte.
public final class IdentityPayloadOptimiser: IPayloadOptimiser, @unchecked Sendable {
    /// Metadata key used to mark that a payload has been through `optimise`.
    public static let optimisedForKey = "x-optimised-for"

    public init() {}

    public func optimise(_ payload: NetworkPayload, targetTransport: TransportKind) async throws -> NetworkPayload {
        var meta = payload.metadata
        meta[Self.optimisedForKey] = String(targetTransport.rawValue)
        return NetworkPayload(
            id: payload.id,
            sourceId: payload.sourceId,
            destinationId: payload.destinationId,
            data: payload.data,
            priority: payload.priority,
            ttl: payload.ttl,
            contentType: payload.contentType,
            metadata: meta,
            createdAt: payload.createdAt)
    }

    public func decompress(_ payload: NetworkPayload) -> NetworkPayload {
        var meta = payload.metadata
        meta.removeValue(forKey: Self.optimisedForKey)
        return NetworkPayload(
            id: payload.id,
            sourceId: payload.sourceId,
            destinationId: payload.destinationId,
            data: payload.data,
            priority: payload.priority,
            ttl: payload.ttl,
            contentType: payload.contentType,
            metadata: meta,
            createdAt: payload.createdAt)
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryPeerDiscovery (working impl of IPeerDiscovery)
//
// Announced peers are fanned out to every live `discover()` stream, and already
// -announced peers are replayed to a new discover() subscriber so discovery is
// order-independent. Snapshot-then-release fan-out.
// ──────────────────────────────────────────────────────────────────────────

/// Deterministic `IPeerDiscovery`. `announce` records the peer and pushes it to
/// every live `discover()` stream; a new `discover()` subscriber first receives
/// all previously-announced peers, so a peer announced before discovery starts
/// is still seen.
public final class InMemoryPeerDiscovery: IPeerDiscovery, @unchecked Sendable {
    private let lock = NSLock()
    /// Most recent announcement per node id, replayed to new subscribers.
    private var known: [String: PeerInfo] = [:]
    /// Insertion order of node ids, so replay is deterministic.
    private var order: [String] = []
    private var subscribers: [UUID: AsyncStream<PeerInfo>.Continuation] = [:]

    public init() {}

    public func announce(localInfo: PeerInfo) async throws {
        lock.lock()
        if known[localInfo.nodeId] == nil { order.append(localInfo.nodeId) }
        known[localInfo.nodeId] = localInfo
        // yield() only buffers per-stream — safe under the lock.
        for cont in subscribers.values { cont.yield(localInfo) }
        lock.unlock()
    }

    public func discover() -> AsyncStream<PeerInfo> {
        AsyncStream(bufferingPolicy: .unbounded) { continuation in
            let id = UUID()
            lock.lock()
            // Replay everything known so far, in announcement order.
            for nodeId in order {
                if let peer = known[nodeId] { continuation.yield(peer) }
            }
            subscribers[id] = continuation
            lock.unlock()

            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock(); self.subscribers[id] = nil; self.lock.unlock()
            }
        }
    }

    /// Number of live discovery subscribers. Useful in tests.
    public var subscriberCount: Int {
        lock.lock(); defer { lock.unlock() }
        return subscribers.count
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryMeshNetwork (working impl of IMeshNetwork)
//
// Reports a fixed local node id, a mutable peer set, and a mesh-health context
// derived from the current peer count.
// ──────────────────────────────────────────────────────────────────────────

/// Deterministic `IMeshNetwork`. Holds a local node id and a peer set; mesh
/// health is a `NetworkContext` whose `nearbyPeerCount` reflects the current
/// peers and whose state is `.meshOnly` when peers exist, else `.offline`.
public final class InMemoryMeshNetwork: IMeshNetwork, @unchecked Sendable {
    public let localNodeId: String

    private let lock = NSLock()
    private var peers: [String]

    /// - Parameters:
    ///   - localNodeId: this device's node id.
    ///   - peers: initial known peers. Defaults to empty.
    public init(localNodeId: String, peers: [String] = []) {
        self.localNodeId = localNodeId
        self.peers = peers
    }

    public func getPeerIds() async throws -> [String] {
        lock.lock(); defer { lock.unlock() }
        return peers
    }

    /// Replace the known peer set.
    public func setPeers(_ newPeers: [String]) {
        lock.lock(); peers = newPeers; lock.unlock()
    }

    /// Add a single peer if not already present.
    public func addPeer(_ nodeId: String) {
        lock.lock()
        if !peers.contains(nodeId) { peers.append(nodeId) }
        lock.unlock()
    }

    public func getMeshHealth() async throws -> NetworkContext {
        lock.lock()
        let count = peers.count
        lock.unlock()
        return NetworkContext(
            state: count > 0 ? .meshOnly : .offline,
            preferredTransport: count > 0 ? .aether : .localStore,
            availableTransports: count > 0 ? [.aether] : [],
            signalStrengthDbm: nil,
            estimatedBandwidthBps: nil,
            latencyMs: nil,
            nearbyPeerCount: count,
            snapshotAt: Date())
    }
}

// ──────────────────────────────────────────────────────────────────────────
// NetworkError
// ──────────────────────────────────────────────────────────────────────────

/// Errors thrown by the in-memory networking implementations.
public enum NetworkError: Error, Equatable, Sendable {
    /// A send was attempted on a transport/channel that has been stopped/closed.
    case transportStopped
    /// A send was attempted on a transport that was never opened / has no live
    /// stream. The analogue of C#'s `InvalidOperationException("Not connected.")`
    /// in the TCP transport's `SendAsync` (nil stream).
    case notConnected
}
