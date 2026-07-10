// CompanionStateSyncTests.swift
// Verifies the companion-state convergence layer: HybridLogicalClock,
// InMemorySyncableEntryStore apply rules, in-process channel fan-out, the
// CompanionStateSyncEngine convergence protocol, and the three bridges.

import XCTest
@testable import CircleAI

final class CompanionStateSyncTests: XCTestCase {

    // ── HybridLogicalClock ────────────────────────────────────────────────

    func testHlcComposeDecomposeRoundTrip() {
        let v = HybridLogicalClock.compose(physicalMs: 1_767_225_600_000, logical: 42, nodeShortId: 7)
        let parts = HybridLogicalClock.decompose(v)
        XCTAssertEqual(parts.physicalMs, 1_767_225_600_000)
        XCTAssertEqual(parts.logical, 42)
        XCTAssertEqual(parts.nodeShortId, 7)
    }

    func testHlcNodeIdPacksLow6Bits() {
        let v = HybridLogicalClock.compose(physicalMs: 100, logical: 0, nodeShortId: 63)
        XCTAssertEqual(v & 0x3F, 63)
        XCTAssertEqual(HybridLogicalClock.decompose(v).nodeShortId, 63)
    }

    func testHlcTickMonotonicSamePhysical() {
        // Physical time frozen — logical must strictly increase each tick.
        let clock = HybridLogicalClock(nodeShortId: 3, physicalNowMs: { 1000 })
        var last: Int64 = -1
        for i in 0..<50 {
            let v = clock.tick()
            XCTAssertGreaterThan(v, last, "tick \(i) not monotonic")
            last = v
        }
    }

    func testHlcTickAdvancesPhysicalResetsLogical() {
        var now: Int64 = 1000
        let clock = HybridLogicalClock(nodeShortId: 1, physicalNowMs: { now })
        _ = clock.tick()          // same ms -> logical bumps
        let v1 = clock.tick()
        XCTAssertEqual(HybridLogicalClock.decompose(v1).logical, 2)
        now = 2000                // physical advanced -> logical resets to 0
        let v2 = clock.tick()
        XCTAssertEqual(HybridLogicalClock.decompose(v2).physicalMs, 2000)
        XCTAssertEqual(HybridLogicalClock.decompose(v2).logical, 0)
    }

    func testHlcLogicalOverflowBumpsPhysical() {
        let clock = HybridLogicalClock(nodeShortId: 0, physicalNowMs: { 5000 })
        var v: Int64 = 0
        // 1024 ticks at the same physical ms forces one physical bump.
        for _ in 0..<1024 { v = clock.tick() }
        XCTAssertEqual(HybridLogicalClock.decompose(v).physicalMs, 5001)
        XCTAssertEqual(HybridLogicalClock.decompose(v).logical, 0)
    }

    func testHlcObserveKeepsLocalAheadOfPeer() {
        let clock = HybridLogicalClock(nodeShortId: 2, physicalNowMs: { 1000 })
        // Peer version far in the future.
        let peer = HybridLogicalClock.compose(physicalMs: 9_000, logical: 5, nodeShortId: 9)
        clock.observe(peer)
        let next = clock.tick()
        // Next local tick must sit at or beyond the observed physical time.
        XCTAssertGreaterThanOrEqual(HybridLogicalClock.decompose(next).physicalMs, 9_000)
    }

    // ── InMemorySyncableEntryStore apply rules ────────────────────────────

    private func mkEntry(type: String = "T", id: String = "1", version: Int64,
                         tombstone: Bool = false, hash: String = "aa",
                         payload: String = "p") -> SyncableEntry {
        SyncableEntry(entityType: type, entityId: id, version: version,
                      isTombstone: tombstone, contentHash: hash, payload: payload,
                      sourceNodeId: "n", authoredAt: Date(timeIntervalSince1970: 1))
    }

    func testStoreAppliesFirstWrite() async throws {
        let store = InMemorySyncableEntryStore()
        let applied = try await store.apply(mkEntry(version: 10))
        XCTAssertTrue(applied)
        let got = try await store.get(entityType: "T", entityId: "1")
        XCTAssertEqual(got?.version, 10)
    }

    func testStoreHigherVersionWins() async throws {
        let store = InMemorySyncableEntryStore()
        _ = try await store.apply(mkEntry(version: 10))
        let applied = try await store.apply(mkEntry(version: 20))
        XCTAssertTrue(applied)
        let got = try await store.get(entityType: "T", entityId: "1")
        XCTAssertEqual(got?.version, 20)
    }

    func testStoreLowerVersionRejected() async throws {
        let store = InMemorySyncableEntryStore()
        _ = try await store.apply(mkEntry(version: 20))
        let applied = try await store.apply(mkEntry(version: 10))
        XCTAssertFalse(applied)
        let got = try await store.get(entityType: "T", entityId: "1")
        XCTAssertEqual(got?.version, 20)
    }

    func testStoreEqualVersionTombstoneWins() async throws {
        let store = InMemorySyncableEntryStore()
        _ = try await store.apply(mkEntry(version: 10, tombstone: false, hash: "zz"))
        // Same version, incoming is a tombstone -> tombstone wins even though its
        // hash is lower, because tombstone-of-non-tombstone beats hash tiebreak.
        let applied = try await store.apply(mkEntry(version: 10, tombstone: true, hash: "aa", payload: ""))
        XCTAssertTrue(applied)
        let got = try await store.get(entityType: "T", entityId: "1")
        XCTAssertEqual(got?.isTombstone, true)
    }

    func testStoreEqualVersionHigherHashWins() async throws {
        let store = InMemorySyncableEntryStore()
        _ = try await store.apply(mkEntry(version: 10, hash: "aa"))
        let appliedHigher = try await store.apply(mkEntry(version: 10, hash: "bb"))
        XCTAssertTrue(appliedHigher)
        let appliedLower = try await store.apply(mkEntry(version: 10, hash: "ab"))
        XCTAssertFalse(appliedLower)
        let got = try await store.get(entityType: "T", entityId: "1")
        XCTAssertEqual(got?.contentHash, "bb")
    }

    func testStoreGetSinceOrdersAscending() async throws {
        let store = InMemorySyncableEntryStore()
        _ = try await store.apply(mkEntry(id: "a", version: 30))
        _ = try await store.apply(mkEntry(id: "b", version: 10))
        _ = try await store.apply(mkEntry(id: "c", version: 20))
        let since = try await store.getSince(entityType: "T", sinceVersion: 10)
        XCTAssertEqual(since.map { $0.version }, [20, 30]) // strictly > 10, ascending
    }

    func testStoreStateVectorTracksMaxPerType() async throws {
        let store = InMemorySyncableEntryStore()
        _ = try await store.apply(mkEntry(type: "A", id: "1", version: 5))
        _ = try await store.apply(mkEntry(type: "A", id: "2", version: 9))
        _ = try await store.apply(mkEntry(type: "B", id: "1", version: 3))
        let vector = try await store.getStateVector()
        let map = Dictionary(uniqueKeysWithValues: vector.map { ($0.entityType, $0.maxKnownVersion) })
        XCTAssertEqual(map["A"], 9)
        XCTAssertEqual(map["B"], 3)
        // Sorted ordinal by type.
        XCTAssertEqual(vector.map { $0.entityType }, ["A", "B"])
    }

    func testComputeHashDeterministicLowerHex() {
        let h = CompanionStateSyncEngine.computeHash("hello")
        // SHA-256("hello") known vector.
        XCTAssertEqual(h, "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")
    }

    func testCompareOrdinal() {
        XCTAssertLessThan(compareOrdinal("aa", "ab"), 0)
        XCTAssertGreaterThan(compareOrdinal("b", "aa"), 0)
        XCTAssertEqual(compareOrdinal("abc", "abc"), 0)
        XCTAssertLessThan(compareOrdinal("ab", "abc"), 0) // prefix is smaller
    }

    // ── In-process channel ─────────────────────────────────────────────────

    func testChannelBroadcastsToOtherPeersOnly() async throws {
        let hub = InProcessSyncHub()
        let a = InProcessCompanionStateChannel(hub: hub, localNodeId: "A")
        let b = InProcessCompanionStateChannel(hub: hub, localNodeId: "B")
        defer { a.dispose(); b.dispose() }

        let received = Locked<[String]>([])
        _ = b.subscribe { env in received.mutate { $0.append(env.fromNodeId) } }
        // A's own handler must NOT fire for A's own broadcast.
        _ = a.subscribe { env in received.mutate { $0.append("A-self:" + env.fromNodeId) } }

        try await a.send(SyncEnvelope(kind: .announce, fromNodeId: "A"))
        XCTAssertEqual(received.value, ["A"])
    }

    func testChannelSubscriptionCancelStopsDelivery() async throws {
        let hub = InProcessSyncHub()
        let a = InProcessCompanionStateChannel(hub: hub, localNodeId: "A")
        let b = InProcessCompanionStateChannel(hub: hub, localNodeId: "B")
        defer { a.dispose(); b.dispose() }

        let count = Locked<Int>(0)
        let sub = b.subscribe { _ in count.mutate { $0 += 1 } }
        try await a.send(SyncEnvelope(kind: .announce, fromNodeId: "A"))
        sub.cancel()
        try await a.send(SyncEnvelope(kind: .announce, fromNodeId: "A"))
        XCTAssertEqual(count.value, 1)
    }

    func testChannelSendAfterDisposeThrows() async throws {
        let hub = InProcessSyncHub()
        let a = InProcessCompanionStateChannel(hub: hub, localNodeId: "A")
        a.dispose()
        do {
            try await a.send(SyncEnvelope(kind: .announce, fromNodeId: "A"))
            XCTFail("expected disposed error")
        } catch let e as SyncChannelError {
            XCTAssertEqual(e, .disposed)
        }
    }

    // ── Engine convergence ──────────────────────────────────────────────────

    /// Builds a two-node mesh with independent stores + engines on one hub.
    private func makeMesh() -> (
        hub: InProcessSyncHub,
        a: CompanionStateSyncEngine, aStore: InMemorySyncableEntryStore, aChan: InProcessCompanionStateChannel,
        b: CompanionStateSyncEngine, bStore: InMemorySyncableEntryStore, bChan: InProcessCompanionStateChannel
    ) {
        let hub = InProcessSyncHub()
        let aChan = InProcessCompanionStateChannel(hub: hub, localNodeId: "A")
        let bChan = InProcessCompanionStateChannel(hub: hub, localNodeId: "B")
        let aStore = InMemorySyncableEntryStore()
        let bStore = InMemorySyncableEntryStore()
        // Distinct node short ids keep versions globally unique even at the same
        // frozen physical time; each engine advances its own logical counter.
        let aClock = HybridLogicalClock(nodeShortId: 1, physicalNowMs: { 1000 })
        let bClock = HybridLogicalClock(nodeShortId: 2, physicalNowMs: { 2000 })
        let a = CompanionStateSyncEngine(channel: aChan, store: aStore, clock: aClock)
        let b = CompanionStateSyncEngine(channel: bChan, store: bStore, clock: bClock)
        return (hub, a, aStore, aChan, b, bStore, bChan)
    }

    func testWriteLocalStampsAndStores() async throws {
        let m = makeMesh()
        defer { m.aChan.dispose(); m.bChan.dispose() }
        let entry = try await m.a.writeLocal(entityType: "PersonaState", entityId: "u1", payload: "{}")
        XCTAssertEqual(entry.entityType, "PersonaState")
        XCTAssertEqual(entry.entityId, "u1")
        XCTAssertFalse(entry.isTombstone)
        XCTAssertEqual(entry.sourceNodeId, "A")
        XCTAssertEqual(entry.contentHash, CompanionStateSyncEngine.computeHash("{}"))
        let got = try await m.aStore.get(entityType: "PersonaState", entityId: "u1")
        XCTAssertEqual(got?.version, entry.version)
    }

    func testPushOnWriteConvergesPeer() async throws {
        let m = makeMesh()
        defer { m.aChan.dispose(); m.bChan.dispose() }
        try await m.a.start()
        try await m.b.start()
        // A writes locally -> Push broadcasts -> B applies.
        _ = try await m.a.writeLocal(entityType: "PersonaState", entityId: "u1", payload: "hello")
        let onB = try await m.bStore.get(entityType: "PersonaState", entityId: "u1")
        XCTAssertNotNil(onB)
        XCTAssertEqual(onB?.payload, "hello")
    }

    func testAnnounceRequestPushConvergesPreexistingState() async throws {
        let m = makeMesh()
        defer { m.aChan.dispose(); m.bChan.dispose() }
        // A has state BEFORE anyone starts (so no Push happened).
        _ = try await m.a.writeLocal(entityType: "CoreMemory", entityId: "c1", payload: "fact")
        try await m.a.start()
        try await m.b.start()
        // B announces its (empty) vector; but convergence is driven by whoever is
        // behind requesting. Have A announce so B discovers it is behind.
        try await m.a.syncNow()
        let onB = try await m.bStore.get(entityType: "CoreMemory", entityId: "c1")
        XCTAssertEqual(onB?.payload, "fact")
    }

    func testWriteLocalBeforeStartDoesNotBroadcast() async throws {
        let m = makeMesh()
        defer { m.aChan.dispose(); m.bChan.dispose() }
        // Not started -> no subscription -> no Push.
        _ = try await m.a.writeLocal(entityType: "PersonaState", entityId: "u1", payload: "x")
        let onB = try await m.bStore.get(entityType: "PersonaState", entityId: "u1")
        XCTAssertNil(onB)
    }

    func testDisposedEngineWriteThrows() async throws {
        let m = makeMesh()
        defer { m.aChan.dispose(); m.bChan.dispose() }
        await m.a.dispose()
        do {
            _ = try await m.a.writeLocal(entityType: "T", entityId: "1", payload: "p")
            XCTFail("expected disposed error")
        } catch let e as SyncEngineError {
            XCTAssertEqual(e, .disposed)
        }
    }

    func testWriteLocalRejectsBlankIds() async throws {
        let m = makeMesh()
        defer { m.aChan.dispose(); m.bChan.dispose() }
        do {
            _ = try await m.a.writeLocal(entityType: "  ", entityId: "1", payload: "p")
            XCTFail("expected argument error")
        } catch let e as SyncEngineError {
            XCTAssertEqual(e, .argument("entityType required"))
        }
    }

    // ── PersonaStateSyncBridge ──────────────────────────────────────────────

    func testPersonaBridgeSavesLocallyAndSyncs() async throws {
        let m = makeMesh()
        defer { m.aChan.dispose(); m.bChan.dispose() }
        try await m.a.start(); try await m.b.start()

        let personaStore = InMemoryPersonaStore()
        let bridge = PersonaStateSyncBridge(store: personaStore, engine: m.a)

        let persona = PersonaState(userId: "u42")
        persona.verbosity = "brief"
        persona.formality = "formal"
        persona.preferredLocale = "en-ZA"
        persona.topicWeights = ["ai": 0.9, "sports": 0.2]
        persona.disfavouredTopics = ["ads"]
        persona.totalInteractions = 5
        persona.positiveSignals = 4
        persona.negativeSignals = 1

        try await bridge.save(persona)

        // Persisted locally.
        let loaded = try await personaStore.load(userId: "u42")
        XCTAssertEqual(loaded.verbosity, "brief")

        // Synced to peer B and decodable.
        let onB = try await m.bStore.get(entityType: PersonaStateSyncBridge.entityType, entityId: "u42")
        XCTAssertNotNil(onB)
        let decoded = PersonaStateSyncBridge.tryDecode(onB!)
        XCTAssertEqual(decoded?.userId, "u42")
        XCTAssertEqual(decoded?.verbosity, "brief")
        XCTAssertEqual(decoded?.formality, "formal")
        XCTAssertEqual(decoded?.preferredLocale, "en-ZA")
        XCTAssertEqual(decoded?.totalInteractions, 5)
        XCTAssertEqual(decoded?.positiveSignals, 4)
        XCTAssertEqual(decoded?.negativeSignals, 1)
        XCTAssertEqual(decoded?.topicWeights["ai"] ?? 0, 0.9, accuracy: 1e-6)
        XCTAssertTrue(decoded?.disfavouredTopics.contains("ads") ?? false)
    }

    func testPersonaBridgeTryDecodeRejectsTombstoneAndWrongType() {
        let tomb = SyncableEntry(entityType: "PersonaState", entityId: "u", version: 1,
                                 isTombstone: true, contentHash: "", payload: "",
                                 sourceNodeId: "n", authoredAt: Date())
        XCTAssertNil(PersonaStateSyncBridge.tryDecode(tomb))
        let wrong = SyncableEntry(entityType: "Other", entityId: "u", version: 1,
                                  isTombstone: false, contentHash: "", payload: "{}",
                                  sourceNodeId: "n", authoredAt: Date())
        XCTAssertNil(PersonaStateSyncBridge.tryDecode(wrong))
    }

    // ── CompanionConversationSyncBridge ─────────────────────────────────────

    func testConversationBridgeRoundTrip() async throws {
        let m = makeMesh()
        defer { m.aChan.dispose(); m.bChan.dispose() }
        try await m.a.start(); try await m.b.start()

        let bridge = CompanionConversationSyncBridge(engine: m.a)
        let delta = ConversationStateDelta(
            sessionId: "s1", userText: "hi", assistantText: "hel",
            isTurnComplete: false,
            startedAtUtc: Date(timeIntervalSince1970: 1_000_000),
            updatedAtUtc: Date(timeIntervalSince1970: 1_000_005))
        try await bridge.publish(delta)

        let onB = try await m.bStore.get(entityType: CompanionConversationSyncBridge.entityType, entityId: "s1")
        XCTAssertNotNil(onB)
        let decoded = CompanionConversationSyncBridge.tryDecode(onB!)
        XCTAssertEqual(decoded?.sessionId, "s1")
        XCTAssertEqual(decoded?.userText, "hi")
        XCTAssertEqual(decoded?.assistantText, "hel")
        XCTAssertEqual(decoded?.isTurnComplete, false)
        XCTAssertEqual(decoded?.startedAtUtc.timeIntervalSince1970 ?? 0, 1_000_000, accuracy: 0.01)
    }

    func testConversationBridgeTerminateIsTombstone() async throws {
        let m = makeMesh()
        defer { m.aChan.dispose(); m.bChan.dispose() }
        try await m.a.start(); try await m.b.start()

        let bridge = CompanionConversationSyncBridge(engine: m.a)
        try await bridge.publish(ConversationStateDelta(
            sessionId: "s2", userText: "u", assistantText: "a", isTurnComplete: true,
            startedAtUtc: Date(), updatedAtUtc: Date()))
        try await bridge.terminate(sessionId: "s2")

        let onB = try await m.bStore.get(entityType: CompanionConversationSyncBridge.entityType, entityId: "s2")
        XCTAssertEqual(onB?.isTombstone, true)
        XCTAssertNil(CompanionConversationSyncBridge.tryDecode(onB!))
    }

    func testConversationBridgeRejectsBlankSession() async throws {
        let m = makeMesh()
        defer { m.aChan.dispose(); m.bChan.dispose() }
        let bridge = CompanionConversationSyncBridge(engine: m.a)
        do {
            try await bridge.publish(ConversationStateDelta(
                sessionId: " ", userText: "", assistantText: "", isTurnComplete: false,
                startedAtUtc: Date(), updatedAtUtc: Date()))
            XCTFail("expected argument error")
        } catch let e as SyncEngineError {
            XCTAssertEqual(e, .argument("SessionId required"))
        }
    }

    // ── LoraAdapterSyncBridge ───────────────────────────────────────────────

    func testLoraBridgePublishAndReceiveWritesFile() async throws {
        let m = makeMesh()
        defer { m.aChan.dispose(); m.bChan.dispose() }
        try await m.a.start(); try await m.b.start()

        // Write a source adapter file to the scratch dir.
        let tmp = FileManager.default.temporaryDirectory
        let src = tmp.appendingPathComponent("adapter-\(UUID().uuidString).bin")
        let dst = tmp.appendingPathComponent("adapter-out-\(UUID().uuidString).bin")
        let bytes = Data([0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02])
        try bytes.write(to: src)
        defer { try? FileManager.default.removeItem(at: src); try? FileManager.default.removeItem(at: dst) }

        let bridge = LoraAdapterSyncBridge(engine: m.a)
        try await bridge.publish(adapterId: "personal-u1", adapterPath: src.path, stepCount: 123)

        let onB = try await m.bStore.get(entityType: LoraAdapterSyncBridge.entityType, entityId: "personal-u1")
        XCTAssertNotNil(onB)
        let snap = await LoraAdapterSyncBridge.tryWrite(onB!, destinationPath: dst.path)
        XCTAssertEqual(snap?.adapterId, "personal-u1")
        XCTAssertEqual(snap?.stepCount, 123)
        let written = try Data(contentsOf: dst)
        XCTAssertEqual(written, bytes)
    }

    func testLoraBridgeMissingFileThrows() async throws {
        let m = makeMesh()
        defer { m.aChan.dispose(); m.bChan.dispose() }
        let bridge = LoraAdapterSyncBridge(engine: m.a)
        do {
            try await bridge.publish(adapterId: "x", adapterPath: "/no/such/file.bin", stepCount: 1)
            XCTFail("expected fileNotFound")
        } catch let e as LoraAdapterError {
            XCTAssertEqual(e, .fileNotFound("/no/such/file.bin"))
        }
    }

    func testLoraSnapshotCodecRoundTrip() {
        let s = LoraAdapterSnapshot(adapterId: "a1", base64Bytes: "AAEC",
                                    trainedAtUtc: Date(timeIntervalSince1970: 1_700_000_000),
                                    stepCount: 999)
        let json = LoraAdapterSyncBridge.encode(s)
        let back = LoraAdapterSyncBridge.decode(json)
        XCTAssertEqual(back?.adapterId, "a1")
        XCTAssertEqual(back?.base64Bytes, "AAEC")
        XCTAssertEqual(back?.stepCount, 999)
        XCTAssertEqual(back?.trainedAtUtc.timeIntervalSince1970 ?? 0, 1_700_000_000, accuracy: 0.01)
    }
}

// MARK: - Test helper

/// A tiny thread-safe box so async channel handlers can record observations.
final class Locked<Value>: @unchecked Sendable {
    private var _value: Value
    private let lock = NSLock()
    init(_ value: Value) { self._value = value }
    var value: Value { lock.lock(); defer { lock.unlock() }; return _value }
    func mutate(_ body: (inout Value) -> Void) {
        lock.lock(); defer { lock.unlock() }; body(&_value)
    }
}
