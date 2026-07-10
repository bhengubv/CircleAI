// MeshCapabilityRegistry.swift
//
// Port of CircleAI.AetherNet.MeshCapabilityRegistry (MeshCapabilityRegistry.cs).
//
// (RT-12 v1) Mesh capability discovery — peers broadcast what they have loaded
// ("I have Qwen3-1.7B-MNN with 2048 tokens of free KV budget on a Tier=Phone
// device"). v1 ships the contracts + an in-memory registry; the AetherNet
// broadcast transport lands later with RT-12 v2 actual offload.
//
// Self-contained: depends only on `DeviceTier` (already in Device.swift). The
// registry is a faithful port — same upsert-replaces-per-peer semantics, same
// stale filtering, same Find ordering (spare KV budget descending), same
// case-insensitive model matching.
//
// Concurrency: an NSLock guards the entry dictionary. All mutation and query
// helpers acquire it synchronously (never across an await), matching the C#
// ConcurrentDictionary's thread-safety with a single lock.

import Foundation

// MARK: - MeshCapabilityAdvertisement

/// (RT-12 v1) One peer's advertisement of what it can serve right now.
/// Pure data — no execution state.
///
/// - `peerId`: stable opaque identifier for the advertising peer.
/// - `modelId`: the model the peer has loaded, e.g. `"Qwen3-1.7B-MNN"`.
/// - `freeKvTokens`: how many tokens of KV-cache budget the peer has spare.
/// - `tier`: the peer's device tier (wearable .. workstation).
/// - `contextWindowTokens`: the model's configured context window.
/// - `advertisedAtUtc`: when the peer last published this advertisement.
/// - `latencyHintMs`: optional round-trip estimate; nil when unknown.
public struct MeshCapabilityAdvertisement: Sendable, Equatable, Codable {
    public let peerId: String
    public let modelId: String
    public let freeKvTokens: Int
    public let tier: DeviceTier
    public let contextWindowTokens: Int
    public let advertisedAtUtc: Date
    public let latencyHintMs: Int?

    public init(
        peerId: String,
        modelId: String,
        freeKvTokens: Int,
        tier: DeviceTier,
        contextWindowTokens: Int,
        advertisedAtUtc: Date,
        latencyHintMs: Int? = nil
    ) {
        self.peerId = peerId
        self.modelId = modelId
        self.freeKvTokens = freeKvTokens
        self.tier = tier
        self.contextWindowTokens = contextWindowTokens
        self.advertisedAtUtc = advertisedAtUtc
        self.latencyHintMs = latencyHintMs
    }
}

// MARK: - IMeshCapabilityRegistry

/// (RT-12 v1) Holds the latest advertisement per peer + supports filtered query.
/// The AetherNet transport feeds this registry as peers broadcast. v1 lets
/// hosting layers query and reason about availability without yet routing.
public protocol IMeshCapabilityRegistry: AnyObject, Sendable {
    /// Publish or replace an advertisement. Called by the transport on receipt of
    /// a peer broadcast.
    func upsert(_ ad: MeshCapabilityAdvertisement) async throws

    /// Remove a peer (e.g. on explicit disconnect). Idempotent. Returns true when
    /// a peer was actually removed.
    @discardableResult
    func remove(peerId: String) async throws -> Bool

    /// Return every advertisement currently known. Use `staleAfter` to filter out
    /// entries older than this interval. A nil `staleAfter` returns everything.
    func list(staleAfter: TimeInterval?) -> [MeshCapabilityAdvertisement]

    /// Find every peer that has loaded `modelId` with at least `minFreeKvTokens`
    /// of spare KV budget. Sorted by spare budget descending — the most-capable
    /// peer comes first.
    func find(modelId: String, minFreeKvTokens: Int, staleAfter: TimeInterval?) -> [MeshCapabilityAdvertisement]
}

// Default-argument convenience shims (protocol methods can't carry defaults).
extension IMeshCapabilityRegistry {
    public func list() -> [MeshCapabilityAdvertisement] { list(staleAfter: nil) }

    public func find(modelId: String) -> [MeshCapabilityAdvertisement] {
        find(modelId: modelId, minFreeKvTokens: 0, staleAfter: nil)
    }

    public func find(modelId: String, minFreeKvTokens: Int) -> [MeshCapabilityAdvertisement] {
        find(modelId: modelId, minFreeKvTokens: minFreeKvTokens, staleAfter: nil)
    }
}

// MARK: - InMemoryMeshCapabilityRegistry

/// (RT-12 v1) Default `IMeshCapabilityRegistry` — in-memory, thread-safe. The
/// AetherNet transport plugs into this; without a transport, the registry just
/// stays empty (no peers).
public final class InMemoryMeshCapabilityRegistry: IMeshCapabilityRegistry, @unchecked Sendable {
    private let lock = NSLock()
    private var entries: [String: MeshCapabilityAdvertisement] = [:]

    /// Clock override for stale filtering (tests). Defaults to `Date()`.
    private let nowUtc: @Sendable () -> Date

    public init(nowUtc: @escaping @Sendable () -> Date = { Date() }) {
        self.nowUtc = nowUtc
    }

    public func upsert(_ ad: MeshCapabilityAdvertisement) async throws {
        let trimmed = ad.peerId.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            throw MeshCapabilityError.invalidPeerId
        }
        lock.lock()
        entries[ad.peerId] = ad
        lock.unlock()
    }

    @discardableResult
    public func remove(peerId: String) async throws -> Bool {
        let trimmed = peerId.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            throw MeshCapabilityError.invalidPeerId
        }
        lock.lock()
        let removed = entries.removeValue(forKey: peerId) != nil
        lock.unlock()
        return removed
    }

    public func list(staleAfter: TimeInterval?) -> [MeshCapabilityAdvertisement] {
        lock.lock()
        let all = Array(entries.values)
        lock.unlock()

        guard let stale = staleAfter else { return all }
        let cutoff = nowUtc().addingTimeInterval(-stale)
        return all.filter { $0.advertisedAtUtc >= cutoff }
    }

    public func find(
        modelId: String,
        minFreeKvTokens: Int,
        staleAfter: TimeInterval?
    ) -> [MeshCapabilityAdvertisement] {
        let trimmed = modelId.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return [] }

        lock.lock()
        let all = Array(entries.values)
        lock.unlock()

        // A nil staleAfter uses Date.distantPast as the cutoff (matches the C#
        // DateTimeOffset.MinValue sentinel — no entry is filtered by staleness).
        let cutoff = staleAfter.map { nowUtc().addingTimeInterval(-$0) } ?? Date.distantPast

        return all
            .filter { $0.modelId.caseInsensitiveCompare(modelId) == .orderedSame }
            .filter { $0.freeKvTokens >= minFreeKvTokens }
            .filter { $0.advertisedAtUtc >= cutoff }
            .sorted { $0.freeKvTokens > $1.freeKvTokens }
    }
}

/// Errors thrown by the mesh capability registry.
public enum MeshCapabilityError: Error, Equatable, Sendable {
    /// A peer id was null, empty, or whitespace.
    case invalidPeerId
}

// MARK: - IMeshCapabilityBroadcaster

/// (RT-12 v1) Contract for the broadcaster that publishes OUR advertisement to
/// the mesh. v1 ships a no-op default; the AetherNet transport binding (v2)
/// supersedes it.
public protocol IMeshCapabilityBroadcaster: AnyObject, Sendable {
    /// Publish our current advertisement to the mesh. v1 may be a no-op when no
    /// transport is registered.
    func broadcast(_ ad: MeshCapabilityAdvertisement) async throws
}

/// Default broadcaster — does nothing. Used when no AetherNet transport is
/// bound. Existing CircleAI deployments work unchanged.
public final class NullMeshCapabilityBroadcaster: IMeshCapabilityBroadcaster, @unchecked Sendable {
    public static let shared = NullMeshCapabilityBroadcaster()
    public init() {}
    public func broadcast(_ ad: MeshCapabilityAdvertisement) async throws {}
}

/// A working in-memory broadcaster that mirrors every advertisement it is asked
/// to broadcast straight into a local `IMeshCapabilityRegistry`. Useful for
/// single-process / test topologies where "the mesh" is the local registry:
/// broadcasting our advert makes it immediately discoverable via `find`.
///
/// This is the loopback analogue of the AetherNet transport binding — no
/// network, but a real, non-stub effect (the registry gains the entry).
public final class LoopbackMeshCapabilityBroadcaster: IMeshCapabilityBroadcaster, @unchecked Sendable {
    private let registry: IMeshCapabilityRegistry

    public init(registry: IMeshCapabilityRegistry) {
        self.registry = registry
    }

    public func broadcast(_ ad: MeshCapabilityAdvertisement) async throws {
        try await registry.upsert(ad)
    }
}
