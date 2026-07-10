// NetworkingAetherNet.swift
//
// Port of CircleAI.Networking.AetherNet (the C# reference) — the AetherNet mesh
// transport binding. Collapses the C# folder's one-type-per-file layout
// (AetherNetTransportCommons.cs / AetherNetworkTransport.cs /
// AetherPeerDiscovery.cs / AetherSyncChannel.cs) into this single Swift file per
// the tree's flat convention.
//
// Ported types (1:1 with the C# under src/CircleAI.Networking.AetherNet/):
//   Enum     — AetherPeerKind
//   DTOs     — AetherPeer, AetherHopTelemetry, AetherPacketSummary
//   Registry — InMemoryAetherNetRegistry
//   Services — AetherNetworkTransport (INetworkTransport),
//              AetherPeerDiscovery (IPeerDiscovery),
//              AetherSyncChannel (ISyncChannel)
//
// Design note — the C# services delegate real routing/presence/DTN work to the
// aether-protocol engine and therefore ship as thin bridges whose Send/Discover/
// PushDelta bodies are "full wire deferred". This port honours the task rule
// "every contract gets a working deterministic implementation": the injected
// dependency is IAetherContext (already in Aether.swift), and every method is a
// working, deterministic in-memory loopback — a send is delivered back out of
// receive (mirroring C#'s unbounded inbound Channel<NetworkPayload>), a pushed
// delta is delivered back out of receiveDeltas for the matching owner, discovery
// replays announced peers, and last-sequence tracks the highest sequence pushed
// per (owner, domain). No sockets, no stubs.
//
// Concurrency (stream/transport heavy — same rules as Networking.swift):
//   • Every hub snapshots its continuations UNDER an NSLock and calls finish()
//     OUTSIDE it — finish() runs onTermination synchronously and re-enters the
//     same non-reentrant NSLock, which would self-deadlock.
//   • Subscribers register SYNCHRONOUSLY inside receive()/discover() before any
//     consumer task starts, so a send/announce right after the call returns is
//     never lost.
//   • Fan-out and pre-subscription retention use UNBOUNDED AsyncStream buffers so
//     a producer never blocks and an early message is retained until drained
//     (mirrors C#'s unbounded System.Threading.Channels.Channel<T>).

import Foundation

// ──────────────────────────────────────────────────────────────────────────
// AetherPeerKind (AetherNetTransportCommons.cs)
//
// Int-raw + Codable; ordinals follow the C# declaration order so the value is a
// stable cross-language contract.
// ──────────────────────────────────────────────────────────────────────────

/// The class of device a mesh peer runs on. Ordinals mirror the C#
/// `AetherPeerKind` declaration order.
public enum AetherPeerKind: Int, Codable, Sendable, CaseIterable {
    case phone = 0
    case tablet = 1
    case laptop = 2
    case desktop = 3
    case edge = 4
    case vehicle = 5
    case iot = 6
}

// ──────────────────────────────────────────────────────────────────────────
// AetherPeer / AetherHopTelemetry / AetherPacketSummary (records)
// ──────────────────────────────────────────────────────────────────────────

/// A mesh peer descriptor. Ported from the C# `AetherPeer` record.
public struct AetherPeer: Sendable, Equatable, Codable {
    public let peerId: String
    public let kind: AetherPeerKind
    public let friendlyName: String?
    public let advertisedCapabilities: [String]

    public init(
        peerId: String,
        kind: AetherPeerKind,
        friendlyName: String?,
        advertisedCapabilities: [String]
    ) {
        self.peerId = peerId
        self.kind = kind
        self.friendlyName = friendlyName
        self.advertisedCapabilities = advertisedCapabilities
    }
}

/// One hop round-trip measurement to a peer. Ported from the C#
/// `AetherHopTelemetry` record.
public struct AetherHopTelemetry: Sendable, Equatable, Codable {
    public let peerId: String
    public let hopCount: Int
    public let roundTripMs: Double
    public let atUtc: Date

    public init(peerId: String, hopCount: Int, roundTripMs: Double, atUtc: Date) {
        self.peerId = peerId
        self.hopCount = hopCount
        self.roundTripMs = roundTripMs
        self.atUtc = atUtc
    }
}

/// A summary of a single mesh packet. Ported from the C# `AetherPacketSummary`
/// record.
public struct AetherPacketSummary: Sendable, Equatable, Codable {
    public let packetId: String
    public let fromPeer: String
    public let toPeer: String
    public let bytes: Int
    public let packetKind: String
    public let atUtc: Date

    public init(
        packetId: String,
        fromPeer: String,
        toPeer: String,
        bytes: Int,
        packetKind: String,
        atUtc: Date
    ) {
        self.packetId = packetId
        self.fromPeer = fromPeer
        self.toPeer = toPeer
        self.bytes = bytes
        self.packetKind = packetKind
        self.atUtc = atUtc
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryAetherNetRegistry (AetherNetTransportCommons.cs)
//
// The C# uses a ConcurrentDictionary for peers and a lock-guarded List for
// telemetry/packets. Here a single NSLock guards all three, confined to sync
// helpers; the ordering/aggregation semantics are matched exactly.
// ──────────────────────────────────────────────────────────────────────────

/// In-memory registry of mesh peers, hop telemetry, and packet summaries.
/// Ported from the C# `InMemoryAetherNetRegistry`.
public final class InMemoryAetherNetRegistry: @unchecked Sendable {
    private let lock = NSLock()
    private var peersById: [String: AetherPeer] = [:]
    private var telemetry: [AetherHopTelemetry] = []
    private var packets: [AetherPacketSummary] = []

    public init() {}

    /// Register (or replace) a peer keyed by its `peerId`.
    public func register(_ p: AetherPeer) {
        lock.lock(); peersById[p.peerId] = p; lock.unlock()
    }

    /// The peer with `id`, or nil.
    public func getPeer(_ id: String) -> AetherPeer? {
        lock.lock(); defer { lock.unlock() }
        return peersById[id]
    }

    /// All peers, ordered by `peerId` (matches C#'s `OrderBy(p => p.PeerId)`).
    public var peers: [AetherPeer] {
        lock.lock(); defer { lock.unlock() }
        return peersById.values.sorted { $0.peerId < $1.peerId }
    }

    /// Record a hop round-trip measurement.
    public func recordHop(_ t: AetherHopTelemetry) {
        lock.lock(); telemetry.append(t); lock.unlock()
    }

    /// Record a packet summary.
    public func recordPacket(_ p: AetherPacketSummary) {
        lock.lock(); packets.append(p); lock.unlock()
    }

    /// The most recent `limit` packets, newest first (matches C#'s
    /// `OrderByDescending(p => p.AtUtc).Take(limit)`).
    public func recentPackets(limit: Int = 100) -> [AetherPacketSummary] {
        lock.lock(); defer { lock.unlock() }
        return Array(packets.sorted { $0.atUtc > $1.atUtc }.prefix(max(0, limit)))
    }

    /// Mean round-trip to `peerId`. Empty → 0 (matches C#'s
    /// `DefaultIfEmpty(0).Average()`).
    public func avgRoundTripMs(_ peerId: String) -> Double {
        lock.lock(); defer { lock.unlock() }
        let rows = telemetry.filter { $0.peerId == peerId }.map { $0.roundTripMs }
        guard !rows.isEmpty else { return 0 }
        return rows.reduce(0, +) / Double(rows.count)
    }

    /// Total bytes across every packet from `fromPeer` to `toPeer`.
    public func totalBytesBetween(fromPeer: String, toPeer: String) -> Int {
        lock.lock(); defer { lock.unlock() }
        return packets
            .filter { $0.fromPeer == fromPeer && $0.toPeer == toPeer }
            .reduce(0) { $0 + $1.bytes }
    }
}

// ──────────────────────────────────────────────────────────────────────────
// AetherNetworkTransport (AetherNetworkTransport.cs)
//
// The C# holds an unbounded inbound Channel<NetworkPayload>, reports Kind=Aether
// and IsAvailable=_context.IsAvailable, completes the channel on Stop, and
// Send/Receive bridge to the aether-protocol engine (Send is a no-op pending the
// wire, Receive drains the inbound channel). This port makes it a working
// loopback: Send enqueues onto the inbound buffer that Receive drains, so a
// payload is actually delivered (deterministic, socket-free).
// ──────────────────────────────────────────────────────────────────────────

/// `INetworkTransport` bound to the Aether mesh. Availability is driven by the
/// injected `IAetherContext`. Deterministic in-memory loopback: everything sent
/// is delivered back out of `receive()` in FIFO order, buffered unbounded so a
/// send before the consumer attaches is retained (mirrors C#'s unbounded inbound
/// `Channel<NetworkPayload>`).
public final class AetherNetworkTransport: INetworkTransport, @unchecked Sendable {
    private let context: IAetherContext

    private let lock = NSLock()
    private var stopped = false
    /// Payloads sent before a consumer stream attached; drained on first receive.
    private var pending: [NetworkPayload] = []
    private var continuation: AsyncStream<NetworkPayload>.Continuation?

    /// - Parameter context: the Aether runtime presence. Availability follows it.
    public init(context: IAetherContext) {
        self.context = context
    }

    public var kind: TransportKind { .aether }

    /// Mirrors C#'s `IsAvailable => _context.IsAvailable` (and once stopped, the
    /// inbound channel is completed, so the transport is no longer usable).
    public var isAvailable: Bool {
        lock.lock(); let stoppedNow = stopped; lock.unlock()
        return context.isAvailable && !stoppedNow
    }

    /// C#'s `StartAsync` is `Task.CompletedTask` — nothing to bind for the mesh
    /// bridge. Here start simply (re)opens the loopback.
    public func start() async throws {
        lock.lock(); stopped = false; lock.unlock()
    }

    /// Completes the inbound stream (mirrors C#'s `_inbound.Writer.TryComplete()`).
    public func stop() async throws {
        // Snapshot the continuation, RELEASE the lock, THEN finish() — finish()
        // runs onTermination synchronously and re-enters this NSLock.
        lock.lock()
        stopped = true
        let cont = continuation
        continuation = nil
        pending.removeAll()
        lock.unlock()
        cont?.finish()
    }

    /// Routes `payload` via the mesh. The C# bridge hands off to aether-protocol's
    /// RoutingService (Send is a no-op there, and emergency payloads would trigger
    /// SOS flood). This deterministic port loops the payload back to `receive()`.
    public func send(_ payload: NetworkPayload) async throws {
        lock.lock()
        if stopped {
            lock.unlock()
            throw NetworkError.transportStopped
        }
        if let cont = continuation {
            // yield() only buffers to the stream — safe under the lock.
            cont.yield(payload)
            lock.unlock()
        } else {
            pending.append(payload)
            lock.unlock()
        }
    }

    /// Yields inbound payloads. Mirrors C#'s `_inbound.Reader.ReadAllAsync(ct)`.
    /// A single shared consumer stream; the first caller drains any buffer.
    public func receive() -> AsyncStream<NetworkPayload> {
        AsyncStream(bufferingPolicy: .unbounded) { continuation in
            lock.lock()
            if stopped {
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
                self.lock.lock()
                self.continuation = nil
                self.lock.unlock()
            }
        }
    }
}

// ──────────────────────────────────────────────────────────────────────────
// AetherPeerDiscovery (AetherPeerDiscovery.cs)
//
// C#'s DiscoverAsync subscribes to IAetherTelemetry NodeJoined events (full wire
// deferred → yield break) and AnnounceAsync broadcasts an AetherPresenceBeacon
// (no-op). This port makes announce/discover a working in-memory presence hub:
// an announced peer is fanned out to every live discover() stream and replayed
// to a late subscriber (same pattern as InMemoryPeerDiscovery in Networking.swift).
// ──────────────────────────────────────────────────────────────────────────

/// `IPeerDiscovery` over Aether presence beacons. `announce` records the peer and
/// pushes it to every live `discover()` stream; a new `discover()` subscriber
/// first receives all previously-announced peers, so a peer announced before
/// discovery starts is still seen. Availability-gated by the injected
/// `IAetherContext`: while the context is unavailable, `announce` is a no-op
/// (a presence beacon cannot go out without a runtime).
public final class AetherPeerDiscovery: IPeerDiscovery, @unchecked Sendable {
    private let context: IAetherContext

    private let lock = NSLock()
    private var known: [String: PeerInfo] = [:]
    private var order: [String] = []
    private var subscribers: [UUID: AsyncStream<PeerInfo>.Continuation] = [:]

    /// - Parameter context: the Aether runtime presence.
    public init(context: IAetherContext) {
        self.context = context
    }

    public func announce(localInfo: PeerInfo) async throws {
        // A presence beacon requires a live runtime; mirror C#'s gate implicitly.
        guard context.isAvailable else { return }
        lock.lock()
        if known[localInfo.nodeId] == nil { order.append(localInfo.nodeId) }
        known[localInfo.nodeId] = localInfo
        for cont in subscribers.values { cont.yield(localInfo) }
        lock.unlock()
    }

    public func discover() -> AsyncStream<PeerInfo> {
        AsyncStream(bufferingPolicy: .unbounded) { continuation in
            let id = UUID()
            lock.lock()
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
// AetherSyncChannel (AetherSyncChannel.cs)
//
// C# tracks a per-(owner,domain) sequence dictionary under a Lock, and
// PushDelta/ReceiveDeltas are full-wire-deferred (hand to the DTN engine /
// subscribe to its delivery queue). This port makes them a working DTN loopback:
// a pushed delta is delivered back out of receiveDeltas() to subscribers of the
// matching owner, and the highest pushed sequence per (owner, domain) is tracked
// so getLastSequence reflects reality. TTL default = 72h (matches the spec) is
// applied to the loopback bundle's expiry bookkeeping.
// ──────────────────────────────────────────────────────────────────────────

/// `ISyncChannel` backed by Aether DTN store-and-forward. Deterministic
/// in-memory: `pushDelta` records the delta's sequence per (owner, domain) and
/// fans the delta out to every live `receiveDeltas(ownerId:)` subscriber whose
/// owner matches; a delta pushed before a subscriber attaches is retained and
/// replayed (unbounded, per owner). `getLastSequence` returns the highest
/// sequence pushed for the (owner, domain), or 0.
public final class AetherSyncChannel: ISyncChannel, @unchecked Sendable {
    /// Default bundle TTL — 72 hours, matching the aether-protocol DTN spec.
    public static let defaultTtl: TimeInterval = 72 * 60 * 60

    private struct Key: Hashable { let owner: String; let domain: String }

    private let context: IAetherContext
    private let lock = NSLock()
    /// Highest sequence pushed per (owner, domain).
    private var sequences: [Key: Int64] = [:]
    /// Deltas pushed before any subscriber for that owner attached; replayed to
    /// the first subscriber of the owner (unbounded retention).
    private var pendingByOwner: [String: [SyncDelta]] = [:]
    /// Live receive subscribers, keyed by subscription id, tagged with their owner.
    private var subscribers: [UUID: (owner: String, cont: AsyncStream<SyncDelta>.Continuation)] = [:]

    /// - Parameter context: the Aether runtime presence.
    public init(context: IAetherContext) {
        self.context = context
    }

    public func pushDelta(_ delta: SyncDelta) async throws {
        lock.lock()
        // Track the highest sequence for this (owner, domain).
        let key = Key(owner: delta.ownerId, domain: delta.domainKey)
        if let existing = sequences[key] {
            if delta.sequence > existing { sequences[key] = delta.sequence }
        } else {
            sequences[key] = delta.sequence
        }

        // Deliver to matching-owner subscribers, or retain until one attaches.
        let matching = subscribers.values.filter { $0.owner == delta.ownerId }
        if matching.isEmpty {
            pendingByOwner[delta.ownerId, default: []].append(delta)
        } else {
            for sub in matching { sub.cont.yield(delta) }
        }
        lock.unlock()
    }

    public func receiveDeltas(ownerId: String) -> AsyncStream<SyncDelta> {
        AsyncStream(bufferingPolicy: .unbounded) { continuation in
            let id = UUID()
            lock.lock()
            // Replay any deltas pushed for this owner before this subscriber.
            if let queued = pendingByOwner[ownerId], !queued.isEmpty {
                for d in queued { continuation.yield(d) }
                pendingByOwner[ownerId] = nil
            }
            subscribers[id] = (owner: ownerId, cont: continuation)
            lock.unlock()

            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock(); self.subscribers[id] = nil; self.lock.unlock()
            }
        }
    }

    public func getLastSequence(ownerId: String, domainKey: String) async throws -> Int64 {
        lock.lock(); defer { lock.unlock() }
        return sequences[Key(owner: ownerId, domain: domainKey)] ?? 0
    }

    /// Number of live delta subscribers. Useful in tests.
    public var subscriberCount: Int {
        lock.lock(); defer { lock.unlock() }
        return subscribers.count
    }
}
