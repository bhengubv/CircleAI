// MemorySyncServiceTests.swift
// Verifies MemorySyncService push/receive orchestration over a loopback
// ISyncChannel, the Guaranteed->.reliable default mapping, own-echo skipping,
// episodic-domain apply into the local store, and the MemoryDeltaCodec
// round-trip. Also covers InMemoryGoalStore.

import XCTest
@testable import CircleAI

/// A loopback `ISyncChannel` for tests: every pushed delta is (a) recorded and
/// (b) fanned out to all live `receiveDeltas` subscribers, so a service that
/// pushes then also receives its own + peers' deltas can be exercised
/// in-process.
private final class LoopbackSyncChannel: ISyncChannel, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var pushed: [SyncDelta] = []
    private var continuations: [UUID: AsyncStream<SyncDelta>.Continuation] = [:]
    private var sequences: [String: Int64] = [:]

    func pushDelta(_ delta: SyncDelta) async throws {
        lock.lock()
        pushed.append(delta)
        let key = delta.ownerId + "|" + delta.domainKey
        sequences[key] = max(sequences[key] ?? 0, delta.sequence)
        let conts = Array(continuations.values)
        lock.unlock()
        for c in conts { c.yield(delta) }
    }

    /// Inject a delta as if it came from a peer (does NOT record into `pushed`).
    func inject(_ delta: SyncDelta) {
        lock.lock(); let conts = Array(continuations.values); lock.unlock()
        for c in conts { c.yield(delta) }
    }

    func receiveDeltas(ownerId: String) -> AsyncStream<SyncDelta> {
        AsyncStream { continuation in
            let id = UUID()
            lock.lock(); continuations[id] = continuation; lock.unlock()
            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock(); self.continuations[id] = nil; self.lock.unlock()
            }
        }
    }

    func getLastSequence(ownerId: String, domainKey: String) async throws -> Int64 {
        lock.lock(); defer { lock.unlock() }
        return sequences[ownerId + "|" + domainKey] ?? 0
    }

    func finishAll() {
        lock.lock(); let conts = Array(continuations.values); continuations.removeAll(); lock.unlock()
        for c in conts { c.finish() }
    }
}

final class MemorySyncServiceTests: XCTestCase {

    func testPushBuildsBroadcastDeltaWithLocalDevice() async throws {
        let chan = LoopbackSyncChannel()
        let store = InMemoryEpisodicStore()
        let svc = MemorySyncService(channel: chan, store: store, localDeviceId: "dev-A")

        let payload = Data([0x01, 0x02, 0x03])
        try await svc.pushMemoryDelta(ownerId: "owner-1", domainKey: SyncDomainKeys.persona, delta: payload)

        XCTAssertEqual(chan.pushed.count, 1)
        let d = chan.pushed[0]
        XCTAssertEqual(d.ownerId, "owner-1")
        XCTAssertEqual(d.sourceDeviceId, "dev-A")
        XCTAssertEqual(d.targetDeviceId, "")           // broadcast
        XCTAssertEqual(d.domainKey, SyncDomainKeys.persona)
        XCTAssertEqual(d.payload, payload)
        XCTAssertNil(d.ttl)
        // Default mode maps C# Guaranteed -> .reliable in this tree.
        XCTAssertEqual(d.deliveryMode, .reliable)
        XCTAssertGreaterThan(d.sequence, 0)
    }

    func testPushHonoursExplicitMode() async throws {
        let chan = LoopbackSyncChannel()
        let store = InMemoryEpisodicStore()
        let svc = MemorySyncService(channel: chan, store: store, localDeviceId: "dev-A")
        try await svc.pushMemoryDelta(
            ownerId: "o", domainKey: SyncDomainKeys.goals, delta: Data(), mode: .dtn)
        XCTAssertEqual(chan.pushed.first?.deliveryMode, .dtn)
    }

    func testReceiveAppliesEpisodicDeltaFromPeer() async throws {
        let chan = LoopbackSyncChannel()
        let store = InMemoryEpisodicStore()
        let svc = MemorySyncService(channel: chan, store: store, localDeviceId: "dev-A")
        try await svc.startReceiving(ownerId: "owner-1")

        // A peer device pushes an episodic entry.
        let entry = EpisodicMemoryEntry(
            userText: "remember milk", assistantText: "noted",
            appContext: "tgn.bruh", tags: ["k": "v"])
        let peerDelta = SyncDelta(
            ownerId: "owner-1", sourceDeviceId: "dev-B", targetDeviceId: "",
            domainKey: SyncDomainKeys.memoryEpisodic,
            payload: MemoryDeltaCodec.encodeEpisodic(entry),
            sequence: 1, deliveryMode: .reliable, ttl: nil, createdAt: Date())
        chan.inject(peerDelta)

        try await waitUntil(timeout: 3.0) { (try? await store.count()) == 1 }
        let recent = try await store.getRecent(count: 5)
        XCTAssertEqual(recent.count, 1)
        XCTAssertEqual(recent.first?.userText, "remember milk")
        XCTAssertEqual(recent.first?.assistantText, "noted")
        XCTAssertEqual(recent.first?.appContext, "tgn.bruh")
        XCTAssertEqual(recent.first?.tags?["k"], "v")

        try await svc.stopReceiving()
        chan.finishAll()
    }

    func testReceiveSkipsOwnEchoes() async throws {
        let chan = LoopbackSyncChannel()
        let store = InMemoryEpisodicStore()
        let svc = MemorySyncService(channel: chan, store: store, localDeviceId: "dev-A")
        try await svc.startReceiving(ownerId: "owner-1")

        // Service pushes an episodic delta (sourceDeviceId == dev-A). The
        // loopback fans it back to the receive loop, which must SKIP it.
        let entry = EpisodicMemoryEntry(userText: "self", assistantText: "echo")
        try await svc.pushMemoryDelta(
            ownerId: "owner-1", domainKey: SyncDomainKeys.memoryEpisodic,
            delta: MemoryDeltaCodec.encodeEpisodic(entry))

        // Give the loop a moment; nothing should be applied.
        try? await Task.sleep(nanoseconds: 200_000_000)
        let n = try await store.count()
        XCTAssertEqual(n, 0, "own echo must not be applied")

        try await svc.stopReceiving()
        chan.finishAll()
    }

    func testReceiveIgnoresNonEpisodicDomain() async throws {
        let chan = LoopbackSyncChannel()
        let store = InMemoryEpisodicStore()
        let svc = MemorySyncService(channel: chan, store: store, localDeviceId: "dev-A")
        try await svc.startReceiving(ownerId: "owner-1")

        // Peer pushes a PERSONA delta — the episodic store must stay empty.
        let peerDelta = SyncDelta(
            ownerId: "owner-1", sourceDeviceId: "dev-B", targetDeviceId: "",
            domainKey: SyncDomainKeys.persona, payload: Data([0x00]),
            sequence: 1, deliveryMode: .reliable, ttl: nil, createdAt: Date())
        chan.inject(peerDelta)

        try? await Task.sleep(nanoseconds: 200_000_000)
        let storeCount = try await store.count()
        XCTAssertEqual(storeCount, 0)

        try await svc.stopReceiving()
        chan.finishAll()
    }

    func testStopReceivingIsIdempotent() async throws {
        let chan = LoopbackSyncChannel()
        let store = InMemoryEpisodicStore()
        let svc = MemorySyncService(channel: chan, store: store, localDeviceId: "dev-A")
        try await svc.stopReceiving()          // before any start
        try await svc.startReceiving(ownerId: "o")
        try await svc.stopReceiving()
        try await svc.stopReceiving()          // double stop
    }

    func testMemoryDeltaCodecRoundTrip() {
        let entry = EpisodicMemoryEntry(
            recordedAt: Date(timeIntervalSince1970: 1_767_225_600),
            userText: "hi", assistantText: "hello",
            appContext: "ctx", embedding: [0.5, -0.25, 1.0], tags: ["a": "b"])
        let data = MemoryDeltaCodec.encodeEpisodic(entry)
        let back = MemoryDeltaCodec.decodeEpisodic(data)
        XCTAssertEqual(back?.id, entry.id)
        XCTAssertEqual(back?.userText, "hi")
        XCTAssertEqual(back?.assistantText, "hello")
        XCTAssertEqual(back?.appContext, "ctx")
        XCTAssertEqual(back?.tags?["a"], "b")
        XCTAssertEqual(back?.embedding?.count, 3)
        XCTAssertEqual(back?.embedding?[0] ?? 0, 0.5, accuracy: 1e-6)
        XCTAssertEqual(back?.embedding?[2] ?? 0, 1.0, accuracy: 1e-6)
        XCTAssertEqual(back?.recordedAt.timeIntervalSince1970 ?? 0, 1_767_225_600, accuracy: 0.01)
    }

    func testMemoryDeltaCodecRejectsGarbage() {
        XCTAssertNil(MemoryDeltaCodec.decodeEpisodic(Data([0xFF, 0x00, 0x11])))
    }

    // ── InMemoryGoalStore ────────────────────────────────────────────────

    private func goal(_ id: String, user: String, status: GoalStatus = .active) -> Goal {
        Goal(id: id, userId: user, title: "t-\(id)", description: "d",
             status: status, priority: .normal, createdAt: Date(timeIntervalSince1970: 1))
    }

    func testGoalStoreUpsertGetList() async throws {
        let store = InMemoryGoalStore()
        _ = try await store.upsert(goal("g1", user: "u1"))
        _ = try await store.upsert(goal("g2", user: "u1"))
        _ = try await store.upsert(goal("g3", user: "u2"))

        let u1 = try await store.list(userId: "u1")
        XCTAssertEqual(Set(u1.map { $0.id }), ["g1", "g2"])
        let fetched = try await store.get(id: "g3")
        XCTAssertEqual(fetched?.userId, "u2")
    }

    func testGoalStoreUpsertReplaces() async throws {
        let store = InMemoryGoalStore()
        _ = try await store.upsert(goal("g1", user: "u1", status: .active))
        _ = try await store.upsert(goal("g1", user: "u1", status: .completed))
        let g = try await store.get(id: "g1")
        XCTAssertEqual(g?.status, .completed)
        let listCount = (try await store.list(userId: "u1")).count
        XCTAssertEqual(listCount, 1)
    }

    func testGoalStoreGetActiveFiltersByStatusAndUser() async throws {
        let store = InMemoryGoalStore()
        _ = try await store.upsert(goal("g1", user: "u1", status: .active))
        _ = try await store.upsert(goal("g2", user: "u1", status: .completed))
        _ = try await store.upsert(goal("g3", user: "u2", status: .active))
        let active = try await store.getActive(userId: "u1")
        XCTAssertEqual(active.map { $0.id }, ["g1"])
    }

    func testGoalStoreDelete() async throws {
        let store = InMemoryGoalStore()
        _ = try await store.upsert(goal("g1", user: "u1"))
        try await store.delete(id: "g1")
        let afterDelete = try await store.get(id: "g1")
        XCTAssertNil(afterDelete)
        // Deleting a missing id is a no-op.
        try await store.delete(id: "nope")
    }

    // ── helper ─────────────────────────────────────────────────────────────

    private func waitUntil(timeout: TimeInterval, _ cond: @escaping () async -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if await cond() { return }
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
        let ok = await cond()
        if !ok { XCTFail("condition not met within \(timeout)s") }
    }
}
