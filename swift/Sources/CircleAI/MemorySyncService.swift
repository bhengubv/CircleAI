// MemorySyncService.swift
//
// Ported from CircleAI.Sync (the C# reference: IMemorySyncService +
// MemorySyncService). The push/receive orchestrator that serialises memory
// deltas, routes them through an `ISyncChannel`, and applies received deltas
// to the local `IEpisodicMemoryStore`.
//
// The transport is determined by `ISyncChannel` — the app code is identical
// whether the delta travels gRPC, BLE mesh, or DTN bundle.
//
// Reconciliation with the C# reference: the C# service lives in CircleAI.Sync
// and depends on CircleAI.Networking's `SyncDelta` / `ISyncChannel` /
// `SyncDeliveryMode` (values BestEffort | Guaranteed | Urgent) and
// CircleAI.Sync's `SyncDomainKeys`. In the Swift tree those transport
// primitives already exist in Sync.swift under the equivalent names, so this
// port maps onto them 1:1:
//   • C# SyncDeliveryMode.Guaranteed  → Swift .reliable  (guaranteed, in-order)
//   • C# SyncDomainKeys.EpisodicMemory→ Swift SyncDomainKeys.memoryEpisodic
// keeping a single canonical set of sync primitives in the Swift module.
//
// The C# ReceiveLoop leaves the episodic-apply body as a "full wire" TODO
// comment. Per the tree's no-stubs rule this Swift port ships a concrete,
// deterministic wire format (see `MemoryDeltaCodec`) that round-trips an
// `EpisodicMemoryEntry` through the delta payload and upserts it into the
// local store on receive.

import Foundation

// MARK: - IMemorySyncService

/// Pushes and receives memory deltas across all owned devices. The transport is
/// determined by `ISyncChannel` — the app code is identical whether the delta
/// travels gRPC, BLE mesh, or DTN bundle.
public protocol IMemorySyncService: AnyObject {
    /// Push a memory delta for `ownerId` to all other devices.
    func pushMemoryDelta(
        ownerId: String, domainKey: String, delta: Data,
        mode: SyncDeliveryMode
    ) async throws

    /// Start receiving and applying incoming deltas for `ownerId`.
    func startReceiving(ownerId: String) async throws

    /// Stop receiving.
    func stopReceiving() async throws
}

public extension IMemorySyncService {
    /// Overload matching the C# default `mode: SyncDeliveryMode.Guaranteed`,
    /// which maps to `.reliable` (guaranteed, in-order delivery) in this tree.
    func pushMemoryDelta(
        ownerId: String, domainKey: String, delta: Data
    ) async throws {
        try await pushMemoryDelta(ownerId: ownerId, domainKey: domainKey,
                                  delta: delta, mode: .reliable)
    }
}

// MARK: - MemorySyncService

/// Default `IMemorySyncService` implementation. Serialises memory deltas, routes
/// through `ISyncChannel`, and applies received deltas to the local
/// `IEpisodicMemoryStore`.
public final class MemorySyncService: IMemorySyncService, @unchecked Sendable {
    private let channel: ISyncChannel
    private let store: IEpisodicMemoryStore
    private let localDeviceId: String

    private let lock = NSLock()
    private var receiveTask: Task<Void, Never>?

    public init(
        channel: ISyncChannel,
        store: IEpisodicMemoryStore,
        localDeviceId: String
    ) {
        self.channel = channel
        self.store = store
        self.localDeviceId = localDeviceId
    }

    public func pushMemoryDelta(
        ownerId: String, domainKey: String, delta: Data,
        mode: SyncDeliveryMode = .reliable
    ) async throws {
        let syncDelta = SyncDelta(
            ownerId: ownerId,
            sourceDeviceId: localDeviceId,
            targetDeviceId: "",        // broadcast to all owned devices
            domainKey: domainKey,
            payload: delta,
            sequence: Int64((Date().timeIntervalSince1970 * 1000).rounded(.down)),
            deliveryMode: mode,
            ttl: nil,
            createdAt: Date())

        try await channel.pushDelta(syncDelta)
    }

    public func startReceiving(ownerId: String) async throws {
        // Cancel any prior receive loop before starting a new one.
        lock.lock()
        let prior = receiveTask
        receiveTask = nil
        lock.unlock()
        prior?.cancel()

        // Subscribe SYNCHRONOUSLY here: AsyncStream registers its continuation at
        // construction, so evaluating receiveDeltas(...) now (not inside the Task)
        // guarantees a delta published right after this call returns reaches us.
        // Spawning the subscription inside the Task races the caller's first push.
        let stream = channel.receiveDeltas(ownerId: ownerId)
        let task = Task { [weak self] in
            guard let self else { return }
            await self.consume(stream)
        }
        lock.lock()
        receiveTask = task
        lock.unlock()
    }

    public func stopReceiving() async throws {
        lock.lock()
        let task = receiveTask
        receiveTask = nil
        lock.unlock()
        task?.cancel()
    }

    private func consume(_ stream: AsyncStream<SyncDelta>) async {
        for await delta in stream {
            if Task.isCancelled { break }
            if delta.sourceDeviceId == localDeviceId { continue } // skip own echoes

            if delta.domainKey == SyncDomainKeys.memoryEpisodic {
                // Full wire: deserialise and upsert into the local episodic store.
                if let entry = MemoryDeltaCodec.decodeEpisodic(delta.payload) {
                    try? await store.add(entry)
                }
            }
            // Additional domain handlers (affect, persona, goals) go here.
        }
    }
}

// MARK: - MemoryDeltaCodec

/// Deterministic wire codec for memory deltas carried in a `SyncDelta.payload`.
///
/// The C# reference left the episodic apply-path as a comment; this codec is
/// the concrete, symmetric implementation the Swift port ships so that a delta
/// pushed by `encodeEpisodic` round-trips back through `decodeEpisodic` on the
/// receiving device. JSON with sorted keys keeps the bytes stable across
/// platforms.
public enum MemoryDeltaCodec {

    private static let iso: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    /// Serialise an `EpisodicMemoryEntry` into delta payload bytes.
    public static func encodeEpisodic(_ entry: EpisodicMemoryEntry) -> Data {
        var obj: [String: Any] = [
            "id": entry.id.uuidString,
            "recordedAt": iso.string(from: entry.recordedAt),
            "userText": entry.userText,
            "assistantText": entry.assistantText,
        ]
        if let ctx = entry.appContext { obj["appContext"] = ctx }
        if let emb = entry.embedding { obj["embedding"] = emb.map { Double($0) } }
        if let tags = entry.tags { obj["tags"] = tags }
        guard let data = try? JSONSerialization.data(withJSONObject: obj, options: [.sortedKeys]) else {
            return Data("{}".utf8)
        }
        return data
    }

    /// Deserialise delta payload bytes back into an `EpisodicMemoryEntry`.
    /// Returns nil when the payload is not a decodable episodic entry.
    public static func decodeEpisodic(_ payload: Data) -> EpisodicMemoryEntry? {
        guard let root = try? JSONSerialization.jsonObject(with: payload) as? [String: Any] else {
            return nil
        }
        let id: UUID = {
            if let s = root["id"] as? String, let u = UUID(uuidString: s) { return u }
            return UUID()
        }()
        let recordedAt: Date = {
            if let s = root["recordedAt"] as? String, let d = iso.date(from: s) { return d }
            return Date()
        }()
        let userText = (root["userText"] as? String) ?? ""
        let assistantText = (root["assistantText"] as? String) ?? ""
        let appContext = root["appContext"] as? String
        let embedding: [Float]? = {
            if let arr = root["embedding"] as? [Any] {
                return arr.compactMap { v -> Float? in
                    if let d = v as? Double { return Float(d) }
                    if let n = v as? NSNumber { return n.floatValue }
                    return nil
                }
            }
            return nil
        }()
        let tags: [String: String]? = {
            if let raw = root["tags"] as? [String: Any] {
                var out: [String: String] = [:]
                for (k, v) in raw { if let s = v as? String { out[k] = s } }
                return out
            }
            return nil
        }()
        return EpisodicMemoryEntry(
            id: id, recordedAt: recordedAt, userText: userText,
            assistantText: assistantText, appContext: appContext,
            embedding: embedding, tags: tags)
    }
}
