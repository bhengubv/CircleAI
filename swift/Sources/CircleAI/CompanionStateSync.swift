// CompanionStateSync.swift
//
// Ported from CircleAI.Memory.Sync (the C# reference), collapsing the C#
// Sync/ folder into a single Swift file per the tree's flat convention:
//
//   HybridLogicalClock              — monotonic 64-bit version stamps (HLC)
//   SyncEnvelopeKind / SyncEnvelope — the convergence protocol message unit
//   StateVectorEntry / RequestItem  — Announce/Request payload rows
//   SyncableEntry                   — the smallest unit the engine moves
//   ISyncableEntryStore             — the seat the engine reads/writes
//   InMemorySyncableEntryStore      — apply-rule store (higher version wins…)
//   ICompanionStateChannel          — transport seam for envelopes
//   InProcessSyncHub / …Channel     — loopback channel for tests + same-device
//   ICompanionStateSyncEngine       — start / SyncNow / WriteLocal contract
//   CompanionStateSyncEngine        — the convergence orchestrator
//   PersonaStateSyncBridge          — IPersonaStore ↔ engine
//   LoraAdapterSyncBridge           — LoRA adapter bytes across devices
//   CompanionConversationSyncBridge — live conversation hand-off
//
// This is the companion-state convergence layer — distinct from the
// transport-hint SyncDelta / ISyncChannel primitives in Sync.swift (the
// CircleAI.Networking-shaped seam). Both coexist: this file is the CRDT-ish
// convergence engine; Sync.swift is the delta transport façade.

import Foundation
import CryptoKit

// MARK: - HybridLogicalClock

/// Hybrid Logical Clock (HLC) — monotonic version stamps that survive small
/// clock skew between peers WITHOUT needing NTP. Composes a physical
/// millisecond timestamp with a logical counter and the node's short ID so
/// every emitted version is globally unique and monotonically increasing.
///
/// Layout of the 64-bit version:
///   high 48 bits — physical time in milliseconds (Unix epoch)
///   mid  10 bits — logical counter (resets when physical advances)
///   low   6 bits — node short ID (0..63)
///
/// Thread-safe: all state transitions are guarded by an `NSLock` held only
/// inside the synchronous `tick` / `observe` helpers (never across an await).
public final class HybridLogicalClock: @unchecked Sendable {
    private let physicalNowMs: @Sendable () -> Int64
    private let nodeShortId: Int64
    private var lastPhysical: Int64
    private var logical: Int64
    private let lock = NSLock()

    /// - Parameters:
    ///   - nodeShortId: 0..63 — packs into the low 6 bits of every version.
    ///     Each device a user has should pick a stable distinct value (any
    ///     deterministic hash works).
    ///   - physicalNowMs: Source of physical time in milliseconds. Defaults to
    ///     system time; override in tests for determinism.
    public init(nodeShortId: Int64, physicalNowMs: (@Sendable () -> Int64)? = nil) {
        precondition(nodeShortId >= 0 && nodeShortId <= 63, "nodeShortId must be in 0..63")
        self.nodeShortId = nodeShortId
        self.physicalNowMs = physicalNowMs ?? HybridLogicalClock.defaultNow
        self.lastPhysical = self.physicalNowMs()
        self.logical = 0
    }

    /// Produces the next outgoing version (for a write we originated).
    public func tick() -> Int64 {
        lock.lock(); defer { lock.unlock() }
        let now = physicalNowMs()
        if now > lastPhysical {
            lastPhysical = now
            logical = 0
        } else {
            logical += 1
            if logical >= 1024 {
                // Logical counter overflowed within the same ms — bump physical.
                lastPhysical += 1
                logical = 0
            }
        }
        return HybridLogicalClock.compose(physicalMs: lastPhysical, logical: logical, nodeShortId: nodeShortId)
    }

    /// Updates the clock from a received version (must be called on every
    /// inbound apply so subsequent local ticks remain monotonic w.r.t. peers).
    @discardableResult
    public func observe(_ incoming: Int64) -> Int64 {
        lock.lock(); defer { lock.unlock() }
        let incomingPhysical = HybridLogicalClock.decompose(incoming).physicalMs
        let now = physicalNowMs()
        let maxPhysical = max(max(lastPhysical, incomingPhysical), now)

        if maxPhysical == lastPhysical && maxPhysical == incomingPhysical {
            logical += 1
        } else if maxPhysical == lastPhysical {
            logical += 1
        } else if maxPhysical == incomingPhysical {
            logical = HybridLogicalClock.decompose(incoming).logical + 1
        } else {
            logical = 0
        }

        lastPhysical = maxPhysical
        return HybridLogicalClock.compose(physicalMs: lastPhysical, logical: logical, nodeShortId: nodeShortId)
    }

    /// Composes the three components into a 64-bit version.
    public static func compose(physicalMs: Int64, logical: Int64, nodeShortId: Int64) -> Int64 {
        (physicalMs << 16) | ((logical & 0x3FF) << 6) | (nodeShortId & 0x3F)
    }

    /// Decomposes a version into its three components.
    public static func decompose(_ version: Int64) -> (physicalMs: Int64, logical: Int64, nodeShortId: Int64) {
        (version >> 16, (version >> 6) & 0x3FF, version & 0x3F)
    }

    private static let defaultNow: @Sendable () -> Int64 = {
        Int64((Date().timeIntervalSince1970 * 1000).rounded(.down))
    }
}

// MARK: - SyncEnvelopeKind

/// Kind of sync envelope.
public enum SyncEnvelopeKind: String, Sendable, CaseIterable {
    /// Broadcast of the sender's per-entity-type high-watermark versions.
    case announce
    /// Reply to an Announce asking for entries newer than a known version.
    case request
    /// Unsolicited or replied delivery of syncable entries.
    case push
}

// MARK: - StateVectorEntry / RequestItem

/// Per-entity-type high-watermark — used in Announce/Request payloads.
public struct StateVectorEntry: Sendable, Equatable {
    /// Logical entity type, e.g. "PersonaState".
    public var entityType: String
    /// The highest version the sender knows for this type.
    public var maxKnownVersion: Int64

    public init(entityType: String, maxKnownVersion: Int64) {
        self.entityType = entityType
        self.maxKnownVersion = maxKnownVersion
    }
}

/// Reply-side request item — "send me entries of `entityType` strictly newer
/// than `sinceVersion`".
public struct RequestItem: Sendable, Equatable {
    public var entityType: String
    public var sinceVersion: Int64

    public init(entityType: String, sinceVersion: Int64) {
        self.entityType = entityType
        self.sinceVersion = sinceVersion
    }
}

// MARK: - SyncableEntry

/// A single syncable item — the smallest unit the engine moves between peers.
///
/// `ContentHash` is SHA-256 of the `Payload` — used as the tiebreaker when two
/// peers happen to write the same `Version` (impossibly rare with HLC, but the
/// system must still converge deterministically).
public struct SyncableEntry: Sendable, Equatable {
    /// Logical type — e.g. "PersonaState", "CoreMemory", "DailyMemorySummary".
    public var entityType: String
    /// Identifier within the type — e.g. a user ID, a GUID-N format string.
    public var entityId: String
    /// HLC-produced monotonic version stamp.
    public var version: Int64
    /// True when this entry represents a deletion. Payload is empty in that case.
    public var isTombstone: Bool
    /// SHA-256 hex of `payload` — content tiebreaker when versions collide.
    public var contentHash: String
    /// Opaque payload — type-specific JSON or any string the adapter chose.
    public var payload: String
    /// Identifier of the node that authored this version (provenance).
    public var sourceNodeId: String
    /// UTC wall-clock when authored — for display, not for ordering.
    public var authoredAt: Date

    public init(
        entityType: String,
        entityId: String,
        version: Int64,
        isTombstone: Bool,
        contentHash: String,
        payload: String,
        sourceNodeId: String,
        authoredAt: Date
    ) {
        self.entityType = entityType
        self.entityId = entityId
        self.version = version
        self.isTombstone = isTombstone
        self.contentHash = contentHash
        self.payload = payload
        self.sourceNodeId = sourceNodeId
        self.authoredAt = authoredAt
    }
}

// MARK: - ISyncableEntryStore

/// The seat the sync engine reads from and writes to. Implementations track
/// the local view of all known syncable entries plus their version stamps.
///
/// Apply rules — implementations MUST enforce these for convergence:
///   • Higher `version` wins
///   • On tie (same `version`), higher `contentHash` (string compare) wins
///   • Tombstones replace any non-tombstone of equal-or-lower `version`
public protocol ISyncableEntryStore: AnyObject {
    /// Applies an incoming entry. Returns true when local state was actually
    /// updated (incoming was strictly newer / preferred). Returns false when
    /// the local entry was already at or beyond the incoming version.
    func apply(_ entry: SyncableEntry) async throws -> Bool

    /// Returns the current entry for the given (type, id), or nil when not
    /// known locally. Tombstones ARE returned — callers needing "is it
    /// deleted?" should check `SyncableEntry.isTombstone`.
    func get(entityType: String, entityId: String) async throws -> SyncableEntry?

    /// Returns every entry of the given type whose version is strictly greater
    /// than `sinceVersion`, ordered ascending by version.
    func getSince(entityType: String, sinceVersion: Int64) async throws -> [SyncableEntry]

    /// Returns the highest known version per entity type — the local node's
    /// state vector. Types with no entries are omitted.
    func getStateVector() async throws -> [StateVectorEntry]
}

// MARK: - InMemorySyncableEntryStore

/// In-memory `ISyncableEntryStore`.
public final class InMemorySyncableEntryStore: ISyncableEntryStore, @unchecked Sendable {
    // Keyed by "type\u{0}id" so writes are O(1).
    private var entries: [String: SyncableEntry] = [:]
    private var maxVersionByType: [String: Int64] = [:]
    private let lock = NSLock()

    public init() {}

    private static func key(_ type: String, _ id: String) -> String {
        // NUL separator can never occur inside an entity type/id, so this is a
        // collision-free composite key.
        type + "\u{0}" + id
    }

    public func apply(_ entry: SyncableEntry) async throws -> Bool {
        applySync(entry)
    }

    private func applySync(_ entry: SyncableEntry) -> Bool {
        lock.lock(); defer { lock.unlock() }
        let k = InMemorySyncableEntryStore.key(entry.entityType, entry.entityId)
        var applied = false
        if let existing = entries[k] {
            if InMemorySyncableEntryStore.shouldApply(existing: existing, incoming: entry) {
                entries[k] = entry
                applied = true
            }
        } else {
            entries[k] = entry
            applied = true
        }

        if applied {
            let current = maxVersionByType[entry.entityType] ?? 0
            if entry.version > current {
                maxVersionByType[entry.entityType] = entry.version
            }
        }
        return applied
    }

    public func get(entityType: String, entityId: String) async throws -> SyncableEntry? {
        lock.lock(); defer { lock.unlock() }
        return entries[InMemorySyncableEntryStore.key(entityType, entityId)]
    }

    public func getSince(entityType: String, sinceVersion: Int64) async throws -> [SyncableEntry] {
        lock.lock(); let snapshot = Array(entries.values); lock.unlock()
        return snapshot
            .filter { $0.entityType == entityType && $0.version > sinceVersion }
            .sorted { $0.version < $1.version }
    }

    public func getStateVector() async throws -> [StateVectorEntry] {
        lock.lock(); let snapshot = maxVersionByType; lock.unlock()
        return snapshot
            .map { StateVectorEntry(entityType: $0.key, maxKnownVersion: $0.value) }
            .sorted { $0.entityType < $1.entityType }
    }

    /// Apply rule: higher version wins; on tie, higher contentHash (string
    /// compare) wins; tombstone replaces a non-tombstone of equal version.
    private static func shouldApply(existing: SyncableEntry, incoming: SyncableEntry) -> Bool {
        if incoming.version > existing.version { return true }
        if incoming.version < existing.version { return false }
        // Equal versions — tombstone-of-non-tombstone wins.
        if incoming.isTombstone && !existing.isTombstone { return true }
        if !incoming.isTombstone && existing.isTombstone { return false }
        // Same tombstone state, same version — content hash tiebreaker (ordinal).
        return compareOrdinal(incoming.contentHash, existing.contentHash) > 0
    }
}

/// Ordinal (Unicode scalar, byte-order) string comparison — mirrors C#
/// `string.CompareOrdinal`. Returns <0, 0, or >0.
func compareOrdinal(_ a: String, _ b: String) -> Int {
    let au = Array(a.unicodeScalars)
    let bu = Array(b.unicodeScalars)
    let n = min(au.count, bu.count)
    var i = 0
    while i < n {
        let x = au[i].value
        let y = bu[i].value
        if x != y { return x < y ? -1 : 1 }
        i += 1
    }
    if au.count == bu.count { return 0 }
    return au.count < bu.count ? -1 : 1
}

// MARK: - ICompanionStateChannel

/// A handle that, when cancelled, unsubscribes a channel handler.
public protocol ISyncSubscription: AnyObject {
    /// Unregisters the handler. Idempotent.
    func cancel()
}

/// Transport that moves `SyncEnvelope` messages between peers.
///
/// Implementations:
///   • InProcessCompanionStateChannel — loopback for tests + same-device sim
///   • (Phase 3.1) AetherNetCompanionStateChannel — over the live mesh
///   • Any other transport the host wants (TCP, WebSockets, etc.)
public protocol ICompanionStateChannel: AnyObject {
    /// Stable identifier of THIS node on this channel. Stamped onto every
    /// envelope as `SyncEnvelope.fromNodeId`.
    var localNodeId: String { get }

    /// Sends an envelope to peers. Channel decides whether this is broadcast
    /// (to every peer) or routed. For v0.1 every channel implements broadcast
    /// semantics.
    func send(_ envelope: SyncEnvelope) async throws

    /// Subscribe to inbound envelopes. Cancel the returned subscription to
    /// unsubscribe.
    func subscribe(_ handler: @escaping @Sendable (SyncEnvelope) async -> Void) -> ISyncSubscription
}

// MARK: - SyncEnvelope

/// A sync envelope — the message unit that crosses the channel.
public struct SyncEnvelope: Sendable {
    /// Kind (announce / request / push).
    public var kind: SyncEnvelopeKind
    /// The node this envelope came from.
    public var fromNodeId: String
    /// Announce payload — the sender's per-type high-watermarks (nil otherwise).
    public var stateVector: [StateVectorEntry]?
    /// Request payload — items the sender wants (nil otherwise).
    public var requests: [RequestItem]?
    /// Push payload — entries being delivered (nil otherwise).
    public var entries: [SyncableEntry]?

    public init(
        kind: SyncEnvelopeKind,
        fromNodeId: String,
        stateVector: [StateVectorEntry]? = nil,
        requests: [RequestItem]? = nil,
        entries: [SyncableEntry]? = nil
    ) {
        self.kind = kind
        self.fromNodeId = fromNodeId
        self.stateVector = stateVector
        self.requests = requests
        self.entries = entries
    }
}

// MARK: - InProcessSyncHub / InProcessCompanionStateChannel

/// Routes envelopes between every `InProcessCompanionStateChannel` that has
/// joined the hub. One hub per simulated "mesh".
public final class InProcessSyncHub: @unchecked Sendable {
    private var channels: [String: InProcessCompanionStateChannel] = [:]
    private let lock = NSLock()

    public init() {}

    func join(_ channel: InProcessCompanionStateChannel) {
        lock.lock(); defer { lock.unlock() }
        channels[channel.localNodeId] = channel
    }

    func leave(_ nodeId: String) {
        lock.lock(); defer { lock.unlock() }
        channels[nodeId] = nil
    }

    func broadcast(_ envelope: SyncEnvelope, senderNodeId: String) async {
        lock.lock()
        let peers = channels.values.filter { $0.localNodeId != senderNodeId }
        lock.unlock()
        for peer in peers {
            await peer.deliver(envelope)
        }
    }

    /// Channels currently on this hub.
    public var connectedNodeIds: [String] {
        lock.lock(); defer { lock.unlock() }
        return Array(channels.keys)
    }
}

/// In-process `ICompanionStateChannel`. Broadcasts via an `InProcessSyncHub`.
public final class InProcessCompanionStateChannel: ICompanionStateChannel, @unchecked Sendable {
    private let hub: InProcessSyncHub
    public let localNodeId: String
    private var handlers: [UUID: @Sendable (SyncEnvelope) async -> Void] = [:]
    private var disposed = false
    private let lock = NSLock()

    public init(hub: InProcessSyncHub, localNodeId: String) {
        precondition(!localNodeId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "localNodeId required")
        self.hub = hub
        self.localNodeId = localNodeId
        hub.join(self)
    }

    public func send(_ envelope: SyncEnvelope) async throws {
        lock.lock(); let isDisposed = disposed; lock.unlock()
        if isDisposed { throw SyncChannelError.disposed }
        await hub.broadcast(envelope, senderNodeId: localNodeId)
    }

    public func subscribe(_ handler: @escaping @Sendable (SyncEnvelope) async -> Void) -> ISyncSubscription {
        lock.lock()
        if disposed {
            lock.unlock()
            // Return an already-cancelled handle rather than throwing from a
            // non-throwing API; the channel is dead so nothing will deliver.
            return NoopSubscription()
        }
        let id = UUID()
        handlers[id] = handler
        lock.unlock()
        return Subscription(owner: self, id: id)
    }

    func deliver(_ envelope: SyncEnvelope) async {
        lock.lock(); let snapshot = Array(handlers.values); lock.unlock()
        for h in snapshot {
            await h(envelope)
        }
    }

    private func remove(_ id: UUID) {
        lock.lock(); defer { lock.unlock() }
        handlers[id] = nil
    }

    /// Unregisters from the hub and drops all handlers.
    public func dispose() {
        lock.lock()
        if disposed { lock.unlock(); return }
        disposed = true
        handlers.removeAll()
        lock.unlock()
        hub.leave(localNodeId)
    }

    private final class Subscription: ISyncSubscription {
        private weak var owner: InProcessCompanionStateChannel?
        private let id: UUID
        init(owner: InProcessCompanionStateChannel, id: UUID) {
            self.owner = owner; self.id = id
        }
        func cancel() { owner?.remove(id) }
    }

    private final class NoopSubscription: ISyncSubscription {
        func cancel() {}
    }
}

/// Errors raised by the in-process channel.
public enum SyncChannelError: Error, Sendable {
    /// The channel has been disposed.
    case disposed
}

// MARK: - ICompanionStateSyncEngine

/// Engine that broadcasts local state vectors, fulfils peer Requests, and
/// applies inbound Push entries. Hosts call `start` once at startup, then
/// either rely on event-driven sync (handlers respond as envelopes arrive) or
/// trigger `syncNow` after notable local writes to immediately propagate.
public protocol ICompanionStateSyncEngine: AnyObject {
    /// Subscribes the engine to channel envelopes.
    func start() async throws

    /// Broadcasts the local state vector to all peers immediately.
    func syncNow() async throws

    /// Convenience to apply a locally-authored entry: stamps it with a fresh
    /// HLC version, persists it to the local store, and (if started) broadcasts
    /// it via Push. Returns the resulting entry with its assigned Version.
    @discardableResult
    func writeLocal(
        entityType: String, entityId: String, payload: String,
        isTombstone: Bool
    ) async throws -> SyncableEntry

    /// Tears down the channel subscription. Idempotent.
    func dispose() async
}

public extension ICompanionStateSyncEngine {
    /// Overload matching the C# default `isTombstone: false`.
    @discardableResult
    func writeLocal(
        entityType: String, entityId: String, payload: String
    ) async throws -> SyncableEntry {
        try await writeLocal(entityType: entityType, entityId: entityId,
                             payload: payload, isTombstone: false)
    }
}

// MARK: - CompanionStateSyncEngine

/// Default `ICompanionStateSyncEngine`.
///
/// Protocol — convergent in <= 2 round-trips per peer pair:
///   1. syncNow      → broadcast Announce(localStateVector)
///   2. Peer receives Announce → diff against own vector → reply Request(missing)
///   3. We receive Request → gather entries via store.getSince → Push
///   4. Peer receives Push → apply for each entry
///   5. Peer broadcasts Announce again if anything applied — converges.
public final class CompanionStateSyncEngine: ICompanionStateSyncEngine, @unchecked Sendable {
    private let channel: ICompanionStateChannel
    private let store: ISyncableEntryStore
    private let clock: HybridLogicalClock
    private let wallClock: @Sendable () -> Date
    private var subscription: ISyncSubscription?
    private var disposed = false
    private let lock = NSLock()

    public init(
        channel: ICompanionStateChannel,
        store: ISyncableEntryStore,
        clock: HybridLogicalClock,
        wallClock: (@Sendable () -> Date)? = nil
    ) {
        self.channel = channel
        self.store = store
        self.clock = clock
        self.wallClock = wallClock ?? { Date() }
    }

    public func start() async throws {
        try throwIfDisposed()
        lock.lock()
        let alreadySubscribed = subscription != nil
        lock.unlock()
        if alreadySubscribed { return }
        let sub = channel.subscribe { [weak self] envelope in
            await self?.handleEnvelope(envelope)
        }
        lock.lock()
        // If a concurrent start already installed one, cancel ours.
        if subscription == nil {
            subscription = sub
            lock.unlock()
        } else {
            lock.unlock()
            sub.cancel()
        }
    }

    public func syncNow() async throws {
        try throwIfDisposed()
        let vector = try await store.getStateVector()
        try await channel.send(SyncEnvelope(
            kind: .announce,
            fromNodeId: channel.localNodeId,
            stateVector: vector,
            requests: nil,
            entries: nil))
    }

    @discardableResult
    public func writeLocal(
        entityType: String, entityId: String, payload: String,
        isTombstone: Bool
    ) async throws -> SyncableEntry {
        try throwIfDisposed()
        guard !entityType.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw SyncEngineError.argument("entityType required")
        }
        guard !entityId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw SyncEngineError.argument("entityId required")
        }

        let entry = SyncableEntry(
            entityType: entityType,
            entityId: entityId,
            version: clock.tick(),
            isTombstone: isTombstone,
            contentHash: CompanionStateSyncEngine.computeHash(payload),
            payload: payload,
            sourceNodeId: channel.localNodeId,
            authoredAt: wallClock())

        _ = try await store.apply(entry)

        lock.lock(); let started = subscription != nil; lock.unlock()
        if started {
            try await channel.send(SyncEnvelope(
                kind: .push,
                fromNodeId: channel.localNodeId,
                stateVector: nil,
                requests: nil,
                entries: [entry]))
        }
        return entry
    }

    // ── Inbound envelope handling ────────────────────────────────────────

    private func handleEnvelope(_ envelope: SyncEnvelope) async {
        switch envelope.kind {
        case .announce: await handleAnnounce(envelope)
        case .request:  await handleRequest(envelope)
        case .push:     await handlePush(envelope)
        }
    }

    private func handleAnnounce(_ envelope: SyncEnvelope) async {
        guard let peerVector = envelope.stateVector else { return }
        guard let local = try? await store.getStateVector() else { return }
        var localMap: [String: Int64] = [:]
        for v in local { localMap[v.entityType] = v.maxKnownVersion }

        var requests: [RequestItem] = []
        for peer in peerVector {
            let ourMax = localMap[peer.entityType] ?? 0
            if peer.maxKnownVersion > ourMax {
                requests.append(RequestItem(entityType: peer.entityType, sinceVersion: ourMax))
            }
        }
        if requests.isEmpty { return }

        try? await channel.send(SyncEnvelope(
            kind: .request,
            fromNodeId: channel.localNodeId,
            stateVector: nil,
            requests: requests,
            entries: nil))
    }

    private func handleRequest(_ envelope: SyncEnvelope) async {
        guard let reqs = envelope.requests, !reqs.isEmpty else { return }
        var collected: [SyncableEntry] = []
        for req in reqs {
            if let newer = try? await store.getSince(entityType: req.entityType, sinceVersion: req.sinceVersion) {
                collected.append(contentsOf: newer)
            }
        }
        if collected.isEmpty { return }

        try? await channel.send(SyncEnvelope(
            kind: .push,
            fromNodeId: channel.localNodeId,
            stateVector: nil,
            requests: nil,
            entries: collected))
    }

    private func handlePush(_ envelope: SyncEnvelope) async {
        guard let entries = envelope.entries else { return }
        var anyApplied = false
        for e in entries {
            clock.observe(e.version)
            if let applied = try? await store.apply(e) {
                anyApplied = anyApplied || applied
            }
        }
        // If anything applied, re-announce so other peers can converge too.
        if anyApplied { try? await syncNow() }
    }

    // ── Teardown ─────────────────────────────────────────────────────────

    public func dispose() async {
        lock.lock()
        if disposed { lock.unlock(); return }
        disposed = true
        let sub = subscription
        subscription = nil
        lock.unlock()
        sub?.cancel()
    }

    private func throwIfDisposed() throws {
        lock.lock(); defer { lock.unlock() }
        if disposed { throw SyncEngineError.disposed }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// SHA-256 of `payload` (UTF-8) as lowercase hex. Deterministic — safe as
    /// the equal-version tiebreaker across every platform.
    static func computeHash(_ payload: String) -> String {
        let digest = SHA256.hash(data: Data(payload.utf8))
        return digest.map { String(format: "%02x", $0) }.joined()
    }
}

/// Errors raised by the sync engine.
public enum SyncEngineError: Error, Sendable, Equatable {
    /// The engine has been disposed.
    case disposed
    /// An argument was missing or invalid.
    case argument(String)
}

// MARK: - PersonaStateSyncBridge

/// Bridges `IPersonaStore` ↔ `ICompanionStateSyncEngine`. On `save`, the
/// persona is JSON-serialised and pushed so PersonaState updates on one device
/// automatically appear on every paired device.
public final class PersonaStateSyncBridge: @unchecked Sendable {
    /// EntityType used on the wire for PersonaState entries.
    public static let entityType = "PersonaState"

    private let store: IPersonaStore
    private let engine: ICompanionStateSyncEngine

    public init(store: IPersonaStore, engine: ICompanionStateSyncEngine) {
        self.store = store
        self.engine = engine
    }

    /// Persists `persona` locally AND broadcasts it via sync.
    public func save(_ persona: PersonaState) async throws {
        try await store.save(persona)
        let payload = PersonaStateSyncBridge.encode(persona)
        try await engine.writeLocal(
            entityType: PersonaStateSyncBridge.entityType,
            entityId: persona.userId,
            payload: payload,
            isTombstone: false)
    }

    /// Decodes a `SyncableEntry` back into a `PersonaState`. Useful for handlers
    /// that subscribe to inbound updates. Returns nil for tombstones or entries
    /// of a different type.
    public static func tryDecode(_ entry: SyncableEntry) -> PersonaState? {
        if entry.isTombstone { return nil }
        if entry.entityType != entityType { return nil }
        return decode(entry.payload)
    }

    // ── Persona <-> JSON (stable, field-for-field with the C# DTO) ─────────

    static func encode(_ p: PersonaState) -> String {
        var obj: [String: PersonaJSON.Value] = [
            "userId": .string(p.userId),
            "lastUpdatedAt": .string(PersonaJSON.iso.string(from: p.lastUpdatedAt)),
            "verbosity": .string(p.verbosity),
            "formality": .string(p.formality),
            "totalInteractions": .int(p.totalInteractions),
            "positiveSignals": .int(p.positiveSignals),
            "negativeSignals": .int(p.negativeSignals),
            "topicWeights": .floatMap(p.topicWeights),
            "disfavouredTopics": .stringArray(p.disfavouredTopics.sorted()),
        ]
        if let locale = p.preferredLocale { obj["preferredLocale"] = .string(locale) }
        return PersonaJSON.serialize(obj)
    }

    static func decode(_ json: String) -> PersonaState? {
        guard let root = try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any] else {
            return nil
        }
        let p = PersonaState(userId: (root["userId"] as? String) ?? "default")
        if let ts = root["lastUpdatedAt"] as? String, let d = PersonaJSON.iso.date(from: ts) {
            p.lastUpdatedAt = d
        }
        if let v = root["verbosity"] as? String { p.verbosity = v }
        if let f = root["formality"] as? String { p.formality = f }
        if let l = root["preferredLocale"] as? String { p.preferredLocale = l }
        if let ti = root["totalInteractions"] as? Int { p.totalInteractions = ti }
        else if let ti = root["totalInteractions"] as? Double { p.totalInteractions = Int(ti) }
        if let ps = root["positiveSignals"] as? Int { p.positiveSignals = ps }
        else if let ps = root["positiveSignals"] as? Double { p.positiveSignals = Int(ps) }
        if let ns = root["negativeSignals"] as? Int { p.negativeSignals = ns }
        else if let ns = root["negativeSignals"] as? Double { p.negativeSignals = Int(ns) }
        if let tw = root["topicWeights"] as? [String: Any] {
            var weights: [String: Float] = [:]
            for (k, val) in tw {
                if let d = val as? Double { weights[k] = Float(d) }
                else if let n = val as? NSNumber { weights[k] = n.floatValue }
            }
            p.topicWeights = weights
        }
        if let dt = root["disfavouredTopics"] as? [Any] {
            p.disfavouredTopics = Set(dt.compactMap { $0 as? String })
        }
        return p
    }
}

/// Minimal, order-stable JSON emitter for the persona DTO. Uses
/// `JSONSerialization` for the value encoding of each field but assembles the
/// object with sorted keys so payloads (and thus content hashes) are
/// deterministic across platforms.
enum PersonaJSON {
    enum Value {
        case string(String)
        case int(Int)
        case floatMap([String: Float])
        case stringArray([String])
    }

    static let iso: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    static func serialize(_ obj: [String: Value]) -> String {
        var parts: [String] = []
        for key in obj.keys.sorted() {
            let encodedValue: String
            switch obj[key]! {
            case .string(let s):
                encodedValue = jsonString(s)
            case .int(let i):
                encodedValue = String(i)
            case .floatMap(let m):
                var inner: [String] = []
                for k in m.keys.sorted() {
                    inner.append("\(jsonString(k)):\(floatString(m[k]!))")
                }
                encodedValue = "{" + inner.joined(separator: ",") + "}"
            case .stringArray(let arr):
                encodedValue = "[" + arr.map { jsonString($0) }.joined(separator: ",") + "]"
            }
            parts.append("\(jsonString(key)):\(encodedValue)")
        }
        return "{" + parts.joined(separator: ",") + "}"
    }

    private static func jsonString(_ s: String) -> String {
        // Encode a single string via JSONSerialization to get correct escaping.
        if let data = try? JSONSerialization.data(withJSONObject: [s], options: []),
           let arr = String(data: data, encoding: .utf8) {
            // arr is "[\"...\"]" — strip the surrounding brackets.
            return String(arr.dropFirst().dropLast())
        }
        return "\"\(s)\""
    }

    private static func floatString(_ f: Float) -> String {
        // Compact representation; drops a trailing ".0" so integers stay clean.
        if f == f.rounded() && abs(f) < 1e15 {
            return String(Int(f))
        }
        return String(f)
    }
}

// MARK: - LoraAdapterSyncBridge

/// (Phase D4) Payload of a synced LoRA adapter snapshot.
public struct LoraAdapterSnapshot: Sendable, Equatable {
    /// Stable id (typically "personal-{userId}").
    public var adapterId: String
    /// Adapter file contents, base64-encoded.
    public var base64Bytes: String
    /// When training that produced these bytes finished.
    public var trainedAtUtc: Date
    /// Total training steps so far (monotonic).
    public var stepCount: Int64

    public init(adapterId: String, base64Bytes: String, trainedAtUtc: Date, stepCount: Int64) {
        self.adapterId = adapterId
        self.base64Bytes = base64Bytes
        self.trainedAtUtc = trainedAtUtc
        self.stepCount = stepCount
    }
}

/// (Phase D4) Bridges trained LoRA adapter bytes across the user's devices
/// through the `ICompanionStateSyncEngine`. Adapter bytes are base64-encoded
/// into the `SyncableEntry` payload; receiving devices decode and persist to
/// disk for the adapter manager to apply.
public final class LoraAdapterSyncBridge: @unchecked Sendable {
    /// EntityType used on the wire.
    public static let entityType = "LoraAdapter"

    private let engine: ICompanionStateSyncEngine

    public init(engine: ICompanionStateSyncEngine) {
        self.engine = engine
    }

    /// Publish a trained adapter to peer devices.
    public func publish(adapterId: String, adapterPath: String, stepCount: Int64) async throws {
        guard !adapterId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw SyncEngineError.argument("adapterId required")
        }
        guard !adapterPath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw SyncEngineError.argument("adapterPath required")
        }
        guard FileManager.default.fileExists(atPath: adapterPath) else {
            throw LoraAdapterError.fileNotFound(adapterPath)
        }
        let bytes = try Data(contentsOf: URL(fileURLWithPath: adapterPath))
        let snapshot = LoraAdapterSnapshot(
            adapterId: adapterId,
            base64Bytes: bytes.base64EncodedString(),
            trainedAtUtc: Date(),
            stepCount: stepCount)
        let payload = LoraAdapterSyncBridge.encode(snapshot)
        try await engine.writeLocal(
            entityType: LoraAdapterSyncBridge.entityType,
            entityId: adapterId,
            payload: payload,
            isTombstone: false)
    }

    /// Decode an inbound `SyncableEntry`, write the adapter to
    /// `destinationPath`. Returns the decoded snapshot for caller-side
    /// bookkeeping (e.g. trigger Apply). Returns nil for tombstones / wrong
    /// type / undecodable payloads.
    @discardableResult
    public static func tryWrite(_ entry: SyncableEntry, destinationPath: String) async -> LoraAdapterSnapshot? {
        if entry.isTombstone { return nil }
        if entry.entityType != entityType { return nil }
        guard let snapshot = decode(entry.payload) else { return nil }
        if snapshot.base64Bytes.isEmpty { return snapshot }
        if let bytes = Data(base64Encoded: snapshot.base64Bytes) {
            let dir = (destinationPath as NSString).deletingLastPathComponent
            if !dir.isEmpty {
                try? FileManager.default.createDirectory(
                    atPath: dir, withIntermediateDirectories: true)
            }
            try? bytes.write(to: URL(fileURLWithPath: destinationPath))
        }
        return snapshot
    }

    static func encode(_ s: LoraAdapterSnapshot) -> String {
        let obj: [String: Any] = [
            "AdapterId": s.adapterId,
            "Base64Bytes": s.base64Bytes,
            "TrainedAtUtc": PersonaJSON.iso.string(from: s.trainedAtUtc),
            "StepCount": s.stepCount,
        ]
        guard let data = try? JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys]),
              let json = String(data: data, encoding: .utf8) else {
            return "{}"
        }
        return json
    }

    static func decode(_ json: String) -> LoraAdapterSnapshot? {
        guard let root = try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any] else {
            return nil
        }
        let adapterId = (root["AdapterId"] as? String) ?? ""
        let base64 = (root["Base64Bytes"] as? String) ?? ""
        let trained: Date = {
            if let ts = root["TrainedAtUtc"] as? String, let d = PersonaJSON.iso.date(from: ts) { return d }
            return Date(timeIntervalSince1970: 0)
        }()
        let steps: Int64 = {
            if let n = root["StepCount"] as? Int64 { return n }
            if let n = root["StepCount"] as? Int { return Int64(n) }
            if let n = root["StepCount"] as? Double { return Int64(n) }
            if let n = root["StepCount"] as? NSNumber { return n.int64Value }
            return 0
        }()
        return LoraAdapterSnapshot(adapterId: adapterId, base64Bytes: base64,
                                   trainedAtUtc: trained, stepCount: steps)
    }
}

/// Errors raised by the LoRA adapter bridge.
public enum LoraAdapterError: Error, Sendable, Equatable {
    /// The adapter file was not found at the given path.
    case fileNotFound(String)
}

// MARK: - CompanionConversationSyncBridge

/// (Phase A2) Wire-format payload of an in-flight conversation turn. The
/// EntityId is the SessionId so multiple sessions converge independently.
public struct ConversationStateDelta: Sendable, Equatable {
    /// Stable identifier the originating device uses for this conversation.
    public var sessionId: String
    /// The latest user utterance for this turn (may be partial transcript).
    public var userText: String
    /// Assistant reply so far — empty until the model starts emitting tokens.
    public var assistantText: String
    /// True once the turn finished; false during streaming.
    public var isTurnComplete: Bool
    /// When the originating device started the turn.
    public var startedAtUtc: Date
    /// When this delta was authored.
    public var updatedAtUtc: Date

    public init(
        sessionId: String,
        userText: String,
        assistantText: String,
        isTurnComplete: Bool,
        startedAtUtc: Date,
        updatedAtUtc: Date
    ) {
        self.sessionId = sessionId
        self.userText = userText
        self.assistantText = assistantText
        self.isTurnComplete = isTurnComplete
        self.startedAtUtc = startedAtUtc
        self.updatedAtUtc = updatedAtUtc
    }
}

/// (Phase A2) Bridges live `ConversationStateDelta` snapshots to the
/// `ICompanionStateSyncEngine` wire so any peer device subscribing to the
/// "ConversationState" entity type can mirror or hand off the conversation.
public final class CompanionConversationSyncBridge: @unchecked Sendable {
    /// EntityType used on the wire for conversation-state entries.
    public static let entityType = "ConversationState"

    private let engine: ICompanionStateSyncEngine

    public init(engine: ICompanionStateSyncEngine) {
        self.engine = engine
    }

    /// Broadcast a conversation-state snapshot to peer devices.
    public func publish(_ delta: ConversationStateDelta) async throws {
        guard !delta.sessionId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw SyncEngineError.argument("SessionId required")
        }
        let payload = CompanionConversationSyncBridge.encode(delta)
        try await engine.writeLocal(
            entityType: CompanionConversationSyncBridge.entityType,
            entityId: delta.sessionId,
            payload: payload,
            isTombstone: false)
    }

    /// Mark the session as ended so peers can clean up shadow state. Uses the
    /// sync-layer tombstone primitive — peers receive an empty payload.
    public func terminate(sessionId: String) async throws {
        guard !sessionId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw SyncEngineError.argument("sessionId required")
        }
        try await engine.writeLocal(
            entityType: CompanionConversationSyncBridge.entityType,
            entityId: sessionId,
            payload: "",
            isTombstone: true)
    }

    /// Decode a sync-layer entry back to a typed delta. Returns nil for
    /// tombstones, wrong type, or undecodable payloads.
    public static func tryDecode(_ entry: SyncableEntry) -> ConversationStateDelta? {
        if entry.isTombstone { return nil }
        if entry.entityType != entityType { return nil }
        return decode(entry.payload)
    }

    static func encode(_ d: ConversationStateDelta) -> String {
        let obj: [String: Any] = [
            "SessionId": d.sessionId,
            "UserText": d.userText,
            "AssistantText": d.assistantText,
            "IsTurnComplete": d.isTurnComplete,
            "StartedAtUtc": PersonaJSON.iso.string(from: d.startedAtUtc),
            "UpdatedAtUtc": PersonaJSON.iso.string(from: d.updatedAtUtc),
        ]
        guard let data = try? JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys]),
              let json = String(data: data, encoding: .utf8) else {
            return "{}"
        }
        return json
    }

    static func decode(_ json: String) -> ConversationStateDelta? {
        guard let root = try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any] else {
            return nil
        }
        let sessionId = (root["SessionId"] as? String) ?? ""
        let userText = (root["UserText"] as? String) ?? ""
        let assistantText = (root["AssistantText"] as? String) ?? ""
        let complete = (root["IsTurnComplete"] as? Bool) ?? false
        let started: Date = {
            if let ts = root["StartedAtUtc"] as? String, let d = PersonaJSON.iso.date(from: ts) { return d }
            return Date(timeIntervalSince1970: 0)
        }()
        let updated: Date = {
            if let ts = root["UpdatedAtUtc"] as? String, let d = PersonaJSON.iso.date(from: ts) { return d }
            return Date(timeIntervalSince1970: 0)
        }()
        return ConversationStateDelta(
            sessionId: sessionId, userText: userText, assistantText: assistantText,
            isTurnComplete: complete, startedAtUtc: started, updatedAtUtc: updated)
    }
}
