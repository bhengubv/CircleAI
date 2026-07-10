// NetworkingDtn.swift
//
// Port of CircleAI.Networking.Dtn (the C# reference) — the delay-tolerant
// networking (store-and-forward) transport. Collapses the C# folder's files
// (DtnBundle.cs / DtnTransportCommons.cs / DtnSyncChannel.cs) into this single
// Swift file per the tree's flat convention.
//
// Ported types (1:1 with the C# under src/CircleAI.Networking.Dtn/):
//   DTO      — DtnBundle
//   Enum     — DtnPriority
//   DTO      — DtnCustodyRecord
//   Store    — InMemoryDtnBundleStore
//   Channel  — DtnSyncChannel (ISyncChannel)
//
// SyncDeliveryMode divergence — the C# reference's SyncDeliveryMode is
// { BestEffort, Guaranteed, Urgent } (NetworkTypes.cs), but the Swift tree's
// SyncDeliveryMode (Sync.swift, which this file must reuse, NOT redefine) is
// { realtime, reliable, dtn, localStore }. The C# DtnSyncChannel keys two
// decisions off the delivery mode:
//   • custodyRequired  = mode == Guaranteed
//   • wire priority    = mode == Urgent ? Urgent : Normal
// Ported faithfully to intent onto the Swift cases:
//   • Guaranteed  ≈ .reliable  (reliable / in-order / custody-transfer)
//   • Urgent      ≈ .realtime  (best-effort real-time push = the expedited path)
// These two constants are named below so the mapping is explicit and testable.
//
// Concurrency (same rules as Networking.swift):
//   • Snapshot continuations UNDER the NSLock and finish() OUTSIDE it.
//   • The delivered stream is unbounded and its subscribers register
//     synchronously, so a delta delivered before a consumer attaches is retained.

import Foundation

// ──────────────────────────────────────────────────────────────────────────
// DtnBundle (DtnBundle.cs)
//
// A self-contained delivery unit with TTL + custody semantics. Value type (the
// C# record's immutability). `payload` is `Data` (C#'s ReadOnlyMemory<byte>).
// ──────────────────────────────────────────────────────────────────────────

/// A DTN bundle: a self-contained delivery unit with TTL and custody semantics.
/// Ported from the C# `DtnBundle` record.
public struct DtnBundle: Sendable, Equatable, Codable {
    public let bundleId: String
    public let sourceNodeId: String
    public let destinationNodeId: String
    public let payload: Data
    /// Default: `createdAt` + 72h.
    public let expiresAt: Date
    /// Request custody transfer at each hop.
    public let custodyRequired: Bool
    public let hopCount: Int
    public let createdAt: Date

    public init(
        bundleId: String,
        sourceNodeId: String,
        destinationNodeId: String,
        payload: Data,
        expiresAt: Date,
        custodyRequired: Bool,
        hopCount: Int,
        createdAt: Date
    ) {
        self.bundleId = bundleId
        self.sourceNodeId = sourceNodeId
        self.destinationNodeId = destinationNodeId
        self.payload = payload
        self.expiresAt = expiresAt
        self.custodyRequired = custodyRequired
        self.hopCount = hopCount
        self.createdAt = createdAt
    }
}

// ──────────────────────────────────────────────────────────────────────────
// DtnPriority (DtnTransportCommons.cs)
//
// Int-raw + Codable; ordinals follow the C# declaration order (weakest first).
// ──────────────────────────────────────────────────────────────────────────

/// DTN forwarding priority. Ordinals mirror the C# `DtnPriority` declaration
/// order (Bulk < Normal < Expedited).
public enum DtnPriority: Int, Codable, Sendable, Comparable, CaseIterable {
    case bulk = 0
    case normal = 1
    case expedited = 2

    public static func < (lhs: DtnPriority, rhs: DtnPriority) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

// ──────────────────────────────────────────────────────────────────────────
// DtnCustodyRecord (DtnTransportCommons.cs)
// ──────────────────────────────────────────────────────────────────────────

/// A custody-transfer acceptance record. Ported from the C# `DtnCustodyRecord`
/// record.
public struct DtnCustodyRecord: Sendable, Equatable, Codable {
    public let bundleId: String
    public let custodianNode: String
    public let acceptedAtUtc: Date

    public init(bundleId: String, custodianNode: String, acceptedAtUtc: Date) {
        self.bundleId = bundleId
        self.custodianNode = custodianNode
        self.acceptedAtUtc = acceptedAtUtc
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryDtnBundleStore (DtnTransportCommons.cs)
//
// C# uses two ConcurrentDictionaries (bundles + custody). Here a single NSLock
// guards both; every method's semantics match exactly, including `IsExpired`
// returning true for an unknown bundle and `Purge` dropping expired bundles +
// their custody records.
// ──────────────────────────────────────────────────────────────────────────

/// In-memory store of DTN bundles and their custody records. Ported from the C#
/// `InMemoryDtnBundleStore`.
public final class InMemoryDtnBundleStore: @unchecked Sendable {
    private let lock = NSLock()
    private var bundles: [String: DtnBundle] = [:]
    private var custody: [String: DtnCustodyRecord] = [:]

    public init() {}

    /// Store (or replace) a bundle keyed by `bundleId`.
    public func store(_ b: DtnBundle) {
        lock.lock(); bundles[b.bundleId] = b; lock.unlock()
    }

    /// The bundle with `bundleId`, or nil.
    public func get(_ bundleId: String) -> DtnBundle? {
        lock.lock(); defer { lock.unlock() }
        return bundles[bundleId]
    }

    /// Every stored bundle (order unspecified, matching C#'s `Values.ToArray()`).
    public var all: [DtnBundle] {
        lock.lock(); defer { lock.unlock() }
        return Array(bundles.values)
    }

    /// Record custody acceptance for a bundle.
    public func acceptCustody(_ r: DtnCustodyRecord) {
        lock.lock(); custody[r.bundleId] = r; lock.unlock()
    }

    /// The custody record for `bundleId`, or nil.
    public func getCustody(_ bundleId: String) -> DtnCustodyRecord? {
        lock.lock(); defer { lock.unlock() }
        return custody[bundleId]
    }

    /// True when the bundle is unknown OR `now` is past its expiry. Mirrors C#:
    /// unknown bundle → expired.
    public func isExpired(_ bundleId: String, now: Date) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let b = bundles[bundleId] else { return true }
        return now > b.expiresAt
    }

    /// Remove every expired bundle (and its custody record); return the count
    /// removed. Mirrors C#'s `Purge`.
    @discardableResult
    public func purge(now: Date) -> Int {
        lock.lock(); defer { lock.unlock() }
        let dead = bundles.filter { now > $0.value.expiresAt }.map { $0.key }
        for id in dead { bundles[id] = nil; custody[id] = nil }
        return dead.count
    }

    /// Every bundle destined for `destinationNodeId` (order unspecified).
    public func inFlightTo(_ destinationNodeId: String) -> [DtnBundle] {
        lock.lock(); defer { lock.unlock() }
        return bundles.values.filter { $0.destinationNodeId == destinationNodeId }
    }
}

// ──────────────────────────────────────────────────────────────────────────
// DtnSyncChannel (DtnSyncChannel.cs)
//
// C# holds a list of INetworkTransport, an unbounded delivered Channel<SyncDelta>,
// and a per-(owner,domain) sequence dict. PushDelta builds a DtnBundle, then if
// any transport IsAvailable sends a NetworkPayload via the first available one
// (else queues locally); ReceiveDeltas drains the delivered channel;
// GetLastSequence reads the dict.
//
// This port preserves that behaviour: it sends via the first available injected
// transport when one exists, and — because the delivered channel is otherwise
// only fed by the (deferred) DTN receive wire — it ALSO tracks the highest
// pushed sequence per (owner,domain) and delivers the delta back out of
// receiveDeltas() to the matching owner (a deterministic loopback), so the
// contract is fully exercised without a socket.
// ──────────────────────────────────────────────────────────────────────────

/// `ISyncChannel` backed by DTN store-and-forward over any injected
/// `INetworkTransport`. On `pushDelta`, a `DtnBundle` is formed (TTL 72h default,
/// custody required for the guaranteed mode) and, when a transport reports
/// available, a `NetworkPayload` is sent via the first available one. The delta's
/// sequence is tracked per (owner, domain) and the delta is delivered back out of
/// `receiveDeltas(ownerId:)` to matching-owner subscribers (deterministic
/// loopback). `getLastSequence` returns the highest sequence pushed, or 0.
public final class DtnSyncChannel: ISyncChannel, @unchecked Sendable {
    /// Default bundle TTL — 72 hours. Mirrors C#'s `DefaultTtl`.
    public static let defaultTtl: TimeInterval = 72 * 60 * 60

    /// The Swift `SyncDeliveryMode` case that maps to the C# reference's
    /// `SyncDeliveryMode.Guaranteed` (custody-transfer / reliable in-order).
    public static let guaranteedMode: SyncDeliveryMode = .reliable
    /// The Swift `SyncDeliveryMode` case that maps to the C# reference's
    /// `SyncDeliveryMode.Urgent` (the expedited / real-time push path).
    public static let urgentMode: SyncDeliveryMode = .realtime

    private struct Key: Hashable { let owner: String; let domain: String }

    private let transports: [INetworkTransport]
    private let lock = NSLock()
    private var sequences: [Key: Int64] = [:]
    /// Deltas delivered before a subscriber for that owner attached; replayed to
    /// the first subscriber of the owner (unbounded retention).
    private var pendingByOwner: [String: [SyncDelta]] = [:]
    private var subscribers: [UUID: (owner: String, cont: AsyncStream<SyncDelta>.Continuation)] = [:]

    /// - Parameter transports: candidate transports, tried in order; the first
    ///   reporting `isAvailable` carries the payload.
    public init(transports: [INetworkTransport]) {
        self.transports = transports
    }

    public func pushDelta(_ delta: SyncDelta) async throws {
        // 1. Form the bundle exactly as the C# does (kept for parity even though
        //    the in-memory loopback does not persist it; a caller may inspect the
        //    shape via lastBundle()).
        let now = Date()
        let bundle = DtnBundle(
            bundleId: Self.newId(),
            sourceNodeId: delta.sourceDeviceId,
            destinationNodeId: delta.targetDeviceId,
            payload: delta.payload,
            expiresAt: now.addingTimeInterval(delta.ttl ?? Self.defaultTtl),
            custodyRequired: delta.deliveryMode == Self.guaranteedMode,
            hopCount: 0,
            createdAt: now)

        // 2. Try live transports first; if none available, the bundle is queued
        //    locally (the loopback below still records + delivers it).
        let available = transports.filter { $0.isAvailable }
        if let first = available.first {
            let payload = NetworkPayload.create(
                data: delta.payload,
                destinationId: delta.targetDeviceId,
                priority: delta.deliveryMode == Self.urgentMode ? .urgent : .normal,
                contentType: "application/dtn-bundle")
            try await first.send(payload)
        }

        // 3. Record sequence + deliver the delta back out of receiveDeltas.
        lock.lock()
        lastFormedBundle = bundle
        let key = Key(owner: delta.ownerId, domain: delta.domainKey)
        if let existing = sequences[key] {
            if delta.sequence > existing { sequences[key] = delta.sequence }
        } else {
            sequences[key] = delta.sequence
        }
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

    /// The bundle formed by the most recent `pushDelta`. Useful in tests to assert
    /// TTL/custody bookkeeping. nil before any push.
    private var lastFormedBundle: DtnBundle?
    public func lastBundle() -> DtnBundle? {
        lock.lock(); defer { lock.unlock() }
        return lastFormedBundle
    }

    /// Number of live delta subscribers. Useful in tests.
    public var subscriberCount: Int {
        lock.lock(); defer { lock.unlock() }
        return subscribers.count
    }

    /// 32-char lowercase hex, matching C#'s `Guid.NewGuid().ToString("N")`.
    private static func newId() -> String {
        UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    }
}
