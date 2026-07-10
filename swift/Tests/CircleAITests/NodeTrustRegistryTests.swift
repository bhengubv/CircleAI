// NodeTrustRegistryTests.swift
//
// Validates NodeTrustRegistry — trust degradation clamping, bounded event
// history, passive recovery, recent-event windowing, and the buffered
// trust-score update broadcast (including pre-subscription buffering, which was
// a Wave-1 hazard).

import XCTest
import Foundation
@testable import CircleAI

final class NodeTrustRegistryTests: XCTestCase {

    private func makeOptions() -> SecurityOptions {
        let o = SecurityOptions()
        return o
    }

    private func event(
        node: String, kind: PeerSecurityEventKind = .authAttempt,
        level: PeerThreatLevel = .medium, at: Date = Date(), desc: String = "evt"
    ) -> PeerSecurityEvent {
        PeerSecurityEvent(nodeId: node, kind: kind, threatLevel: level,
                          description: desc, transportId: "test", occurredAt: at)
    }

    // ── get / create ─────────────────────────────────────────────────────────

    func testUnknownNodeReturnsInitialTrust() {
        let reg = NodeTrustRegistry(options: makeOptions())
        XCTAssertEqual(reg.getTrustScore("nobody"), 1.0, accuracy: 1e-9)
    }

    func testGetOrCreateSeedsInitialTrust() {
        let opts = makeOptions()
        opts.initialTrustScore = 0.8
        let reg = NodeTrustRegistry(options: opts)
        let entry = reg.getOrCreate("peer-1")
        XCTAssertEqual(entry.nodeId, "peer-1")
        XCTAssertEqual(entry.trustScore, 0.8, accuracy: 1e-9)
        XCTAssertTrue(reg.allNodeIds.contains("peer-1"))
    }

    // ── applyDegradation ─────────────────────────────────────────────────────

    func testApplyDegradationDropsAndClampsScore() {
        let reg = NodeTrustRegistry(options: makeOptions())
        let (prev, cur) = reg.applyDegradation(event(node: "p"), degradationAmount: 0.3)
        XCTAssertEqual(prev, 1.0, accuracy: 1e-9)
        XCTAssertEqual(cur, 0.7, accuracy: 1e-9)
        XCTAssertEqual(reg.getTrustScore("p"), 0.7, accuracy: 1e-9)
    }

    func testApplyDegradationClampsAtZero() {
        let reg = NodeTrustRegistry(options: makeOptions())
        let (_, cur) = reg.applyDegradation(event(node: "p"), degradationAmount: 5.0)
        XCTAssertEqual(cur, 0.0, accuracy: 1e-9)
    }

    func testApplyDegradationRecordsEvent() {
        let reg = NodeTrustRegistry(options: makeOptions())
        reg.applyDegradation(event(node: "p", desc: "first"), degradationAmount: 0.1)
        let recents = reg.getRecentEvents("p")
        XCTAssertEqual(recents.count, 1)
        XCTAssertEqual(recents.first?.description, "first")
    }

    func testEventHistoryIsBounded() {
        let opts = makeOptions()
        opts.maxEventsPerNode = 3
        let reg = NodeTrustRegistry(options: opts)
        for i in 0..<6 {
            reg.applyDegradation(event(node: "p", desc: "e\(i)"), degradationAmount: 0.0)
        }
        let recents = reg.getRecentEvents("p")
        XCTAssertEqual(recents.count, 3)
        // Oldest dropped first → e3,e4,e5 remain in order.
        XCTAssertEqual(recents.map { $0.description }, ["e3", "e4", "e5"])
    }

    // ── recent-event windowing ───────────────────────────────────────────────

    func testGetRecentEventsHonoursWindow() {
        let opts = makeOptions()
        opts.eventWindow = 60
        let reg = NodeTrustRegistry(options: opts)
        reg.applyDegradation(event(node: "p", at: Date().addingTimeInterval(-120), desc: "old"),
                             degradationAmount: 0.0)
        reg.applyDegradation(event(node: "p", at: Date(), desc: "new"),
                             degradationAmount: 0.0)
        let recents = reg.getRecentEvents("p")
        XCTAssertEqual(recents.map { $0.description }, ["new"])
    }

    func testGetRecentEventsUnknownNodeIsEmpty() {
        let reg = NodeTrustRegistry(options: makeOptions())
        XCTAssertTrue(reg.getRecentEvents("ghost").isEmpty)
    }

    // ── passive recovery ─────────────────────────────────────────────────────

    func testApplyRecoveryHealsTowardOne() {
        let opts = makeOptions()
        opts.recoveryRatePerSecond = 0.01
        let reg = NodeTrustRegistry(options: opts)
        reg.applyDegradation(event(node: "p"), degradationAmount: 0.5) // → 0.5
        reg.applyRecovery(10) // +0.1 → 0.6
        XCTAssertEqual(reg.getTrustScore("p"), 0.6, accuracy: 1e-9)
    }

    func testApplyRecoveryCapsAtOneAndSkipsFullyTrusted() {
        let opts = makeOptions()
        opts.recoveryRatePerSecond = 1.0
        let reg = NodeTrustRegistry(options: opts)
        reg.applyDegradation(event(node: "p"), degradationAmount: 0.2) // 0.8
        reg.applyRecovery(10) // would add 10 → capped at 1.0
        XCTAssertEqual(reg.getTrustScore("p"), 1.0, accuracy: 1e-9)
    }

    func testApplyRecoveryNoOpForNonPositiveAmount() {
        let opts = makeOptions()
        opts.recoveryRatePerSecond = 0.0
        let reg = NodeTrustRegistry(options: opts)
        reg.applyDegradation(event(node: "p"), degradationAmount: 0.5)
        reg.applyRecovery(1000)
        XCTAssertEqual(reg.getTrustScore("p"), 0.5, accuracy: 1e-9)
    }

    // ── trust-score update broadcast ─────────────────────────────────────────

    func testDegradationEmitsTrustScoreUpdate() async {
        let reg = NodeTrustRegistry(options: makeOptions())
        // Subscribe SYNCHRONOUSLY, then emit — the stream is registered before we
        // publish, so the update is delivered.
        var iterator = reg.trustScoreUpdates().makeAsyncIterator()
        reg.applyDegradation(event(node: "p", desc: "auth-fail"), degradationAmount: 0.25)
        let update = await iterator.next()
        XCTAssertEqual(update?.nodeId, "p")
        XCTAssertEqual(update?.previousScore ?? -1, 1.0, accuracy: 1e-9)
        XCTAssertEqual(update?.newScore ?? -1, 0.75, accuracy: 1e-9)
        XCTAssertEqual(update?.reason, "auth-fail")
    }

    func testUpdatesEmittedBeforeSubscriptionAreBuffered() async {
        // Emit BEFORE any subscriber attaches; the unbounded buffer must retain
        // the update and flush it to the first subscriber (matches C# Channel).
        let reg = NodeTrustRegistry(options: makeOptions())
        reg.applyDegradation(event(node: "p", desc: "early"), degradationAmount: 0.1)

        var iterator = reg.trustScoreUpdates().makeAsyncIterator()
        let update = await iterator.next()
        XCTAssertEqual(update?.reason, "early")
        XCTAssertEqual(update?.nodeId, "p")
    }

    func testNoUpdateWhenScoreUnchanged() async {
        // A zero-degradation event does not move the score → no update published,
        // so with nothing buffered the (finished-less) stream just has no item
        // yet. We verify by emitting a real change afterwards and asserting the
        // FIRST delivered update is the real one, not a spurious zero-delta.
        let reg = NodeTrustRegistry(options: makeOptions())
        reg.applyDegradation(event(node: "p", desc: "noop"), degradationAmount: 0.0)
        reg.applyDegradation(event(node: "p", desc: "real"), degradationAmount: 0.2)

        var iterator = reg.trustScoreUpdates().makeAsyncIterator()
        let first = await iterator.next()
        XCTAssertEqual(first?.reason, "real")
    }

    func testRecoveryEmitsPassiveRecoveryUpdate() async {
        let opts = makeOptions()
        opts.recoveryRatePerSecond = 0.01
        let reg = NodeTrustRegistry(options: opts)
        reg.applyDegradation(event(node: "p"), degradationAmount: 0.5)

        var iterator = reg.trustScoreUpdates().makeAsyncIterator()
        // Drain the degradation update first.
        _ = await iterator.next()
        reg.applyRecovery(10)
        let recovery = await iterator.next()
        XCTAssertEqual(recovery?.reason, "passive-recovery")
        XCTAssertEqual(recovery?.newScore ?? -1, 0.6, accuracy: 1e-9)
    }
}
