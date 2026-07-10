// CompanionRuntimeTests.swift
// Verifies CompanionRuntime lifecycle: catch-up consolidation on start,
// periodic ticks firing, sync-engine start/dispose wiring, syncNow forwarding,
// and the no-ingester guard. Uses tiny intervals + a counting fake
// consolidator so the loops are observable without real time passing.

import XCTest
@testable import CircleAI

private final class CountingConsolidator: IMemoryConsolidator, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var ticks: [SleepKind] = []

    func tick(kind: SleepKind) async throws -> ConsolidationOutcome {
        lock.lock(); ticks.append(kind); lock.unlock()
        return ConsolidationOutcome(
            kind: kind, dailySummariesProduced: 0, semanticClustersProduced: 0,
            personaDeltasProduced: 0, corePromotions: 0, episodesPruned: 0,
            dailiesPruned: 0, semanticsPruned: 0, ranAtUtc: Date())
    }

    func count(of kind: SleepKind) -> Int {
        lock.lock(); defer { lock.unlock() }
        return ticks.filter { $0 == kind }.count
    }
    var total: Int { lock.lock(); defer { lock.unlock() }; return ticks.count }
}

final class CompanionRuntimeTests: XCTestCase {

    func testCatchUpRunsOnDemandOnStart() async throws {
        let cons = CountingConsolidator()
        // All periodic intervals disabled so ONLY the catch-up pass runs.
        let opts = CompanionRuntimeOptions(
            dailyTickInterval: 0, weeklyTickInterval: 0, monthlyTickInterval: 0,
            syncBroadcastInterval: 0, initialDelay: 0, catchUpOnStart: true)
        let rt = CompanionRuntime(consolidator: cons, options: opts)
        try await rt.start()
        await rt.stop()
        XCTAssertEqual(cons.count(of: .onDemand), 1)
        XCTAssertEqual(cons.total, 1)
    }

    func testCatchUpDisabledRunsNothingOnStart() async throws {
        let cons = CountingConsolidator()
        let opts = CompanionRuntimeOptions(
            dailyTickInterval: 0, weeklyTickInterval: 0, monthlyTickInterval: 0,
            syncBroadcastInterval: 0, initialDelay: 0, catchUpOnStart: false)
        let rt = CompanionRuntime(consolidator: cons, options: opts)
        try await rt.start()
        await rt.stop()
        XCTAssertEqual(cons.total, 0)
    }

    func testPeriodicDailyTickFires() async throws {
        let cons = CountingConsolidator()
        // Very small daily interval so the loop ticks quickly; no catch-up so we
        // only count periodic daily ticks.
        let opts = CompanionRuntimeOptions(
            dailyTickInterval: 0.02, weeklyTickInterval: 0, monthlyTickInterval: 0,
            syncBroadcastInterval: 0, initialDelay: 0, catchUpOnStart: false)
        let rt = CompanionRuntime(consolidator: cons, options: opts)
        try await rt.start()
        try await waitUntil(timeout: 3.0) { cons.count(of: .daily) >= 2 }
        await rt.stop()
        XCTAssertGreaterThanOrEqual(cons.count(of: .daily), 2)
    }

    func testStopHaltsPeriodicTicks() async throws {
        let cons = CountingConsolidator()
        let opts = CompanionRuntimeOptions(
            dailyTickInterval: 0.02, weeklyTickInterval: 0, monthlyTickInterval: 0,
            syncBroadcastInterval: 0, initialDelay: 0, catchUpOnStart: false)
        let rt = CompanionRuntime(consolidator: cons, options: opts)
        try await rt.start()
        try await waitUntil(timeout: 3.0) { cons.count(of: .daily) >= 1 }
        await rt.stop()
        let after = cons.count(of: .daily)
        // Give any stray loop a chance to (wrongly) tick again.
        try? await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(cons.count(of: .daily), after, "ticks continued after stop")
    }

    func testSyncEngineStartedAndDisposedByRuntime() async throws {
        let cons = CountingConsolidator()
        let hub = InProcessSyncHub()
        let chan = InProcessCompanionStateChannel(hub: hub, localNodeId: "A")
        let store = InMemorySyncableEntryStore()
        let clock = HybridLogicalClock(nodeShortId: 1, physicalNowMs: { 1000 })
        let engine = CompanionStateSyncEngine(channel: chan, store: store, clock: clock)

        let opts = CompanionRuntimeOptions(
            dailyTickInterval: 0, weeklyTickInterval: 0, monthlyTickInterval: 0,
            syncBroadcastInterval: 0, initialDelay: 0, catchUpOnStart: false)
        let rt = CompanionRuntime(consolidator: cons, options: opts, syncEngine: engine)
        try await rt.start()

        // Engine is started: writeLocal now broadcasts (subscription present).
        let peerChan = InProcessCompanionStateChannel(hub: hub, localNodeId: "B")
        let peerStore = InMemorySyncableEntryStore()
        let peerEngine = CompanionStateSyncEngine(
            channel: peerChan, store: peerStore,
            clock: HybridLogicalClock(nodeShortId: 2, physicalNowMs: { 2000 }))
        try await peerEngine.start()
        _ = try await engine.writeLocal(entityType: "T", entityId: "1", payload: "p")
        let onPeer = try await peerStore.get(entityType: "T", entityId: "1")
        XCTAssertNotNil(onPeer)

        await rt.stop()
        // After stop the engine is disposed -> writeLocal throws.
        do {
            _ = try await engine.writeLocal(entityType: "T", entityId: "2", payload: "q")
            XCTFail("engine should be disposed after runtime stop")
        } catch let e as SyncEngineError {
            XCTAssertEqual(e, .disposed)
        }
        peerChan.dispose()
    }

    func testSyncBroadcastLoopSendsAnnounce() async throws {
        let cons = CountingConsolidator()
        let hub = InProcessSyncHub()
        let chan = InProcessCompanionStateChannel(hub: hub, localNodeId: "A")
        let store = InMemorySyncableEntryStore()
        // Seed a state vector so the Announce carries a non-empty vector.
        _ = try await store.apply(SyncableEntry(
            entityType: "T", entityId: "1", version: 10, isTombstone: false,
            contentHash: "h", payload: "p", sourceNodeId: "A", authoredAt: Date()))
        let engine = CompanionStateSyncEngine(
            channel: chan, store: store,
            clock: HybridLogicalClock(nodeShortId: 1, physicalNowMs: { 1000 }))

        // Peer channel that records received announces.
        let peerChan = InProcessCompanionStateChannel(hub: hub, localNodeId: "B")
        let announces = Locked<Int>(0)
        _ = peerChan.subscribe { env in
            if env.kind == .announce { announces.mutate { $0 += 1 } }
        }
        defer { peerChan.dispose() }

        let opts = CompanionRuntimeOptions(
            dailyTickInterval: 0, weeklyTickInterval: 0, monthlyTickInterval: 0,
            syncBroadcastInterval: 0.02, initialDelay: 0, catchUpOnStart: false)
        let rt = CompanionRuntime(consolidator: cons, options: opts, syncEngine: engine)
        try await rt.start()
        try await waitUntil(timeout: 3.0) { announces.value >= 2 }
        await rt.stop()
        XCTAssertGreaterThanOrEqual(announces.value, 2)
    }

    func testIngestMediaThrowsWhenNoIngester() async throws {
        let cons = CountingConsolidator()
        let opts = CompanionRuntimeOptions(
            dailyTickInterval: 0, weeklyTickInterval: 0, monthlyTickInterval: 0,
            syncBroadcastInterval: 0, initialDelay: 0, catchUpOnStart: false)
        let rt = CompanionRuntime(consolidator: cons, options: opts)
        do {
            _ = try await rt.ingestMedia(modality: .image, sourceBytes: [1, 2, 3])
            XCTFail("expected noIngester")
        } catch let e as CompanionRuntimeError {
            XCTAssertEqual(e, .noIngester)
        }
    }

    func testConsolidateNowRunsOnDemand() async throws {
        let cons = CountingConsolidator()
        let opts = CompanionRuntimeOptions(
            dailyTickInterval: 0, weeklyTickInterval: 0, monthlyTickInterval: 0,
            syncBroadcastInterval: 0, initialDelay: 0, catchUpOnStart: false)
        let rt = CompanionRuntime(consolidator: cons, options: opts)
        let outcome = try await rt.consolidateNow()
        XCTAssertEqual(outcome.kind, .onDemand)
        XCTAssertEqual(cons.count(of: .onDemand), 1)
    }

    func testSyncNowNoOpWithoutEngine() async throws {
        let cons = CountingConsolidator()
        let rt = CompanionRuntime(consolidator: cons)
        // Should simply not throw.
        try await rt.syncNow()
    }

    func testOptionsDefaults() {
        let o = CompanionRuntimeOptions()
        XCTAssertEqual(o.dailyTickInterval, 6 * 3600, accuracy: 0.001)
        XCTAssertEqual(o.weeklyTickInterval, 24 * 3600, accuracy: 0.001)
        XCTAssertEqual(o.monthlyTickInterval, 48 * 3600, accuracy: 0.001)
        XCTAssertEqual(o.syncBroadcastInterval, 5 * 60, accuracy: 0.001)
        XCTAssertEqual(o.initialDelay, 30, accuracy: 0.001)
        XCTAssertTrue(o.catchUpOnStart)
    }

    // ── helper ─────────────────────────────────────────────────────────────

    private func waitUntil(timeout: TimeInterval, _ cond: @escaping () -> Bool) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if cond() { return }
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
        if !cond() { XCTFail("condition not met within \(timeout)s") }
    }
}
