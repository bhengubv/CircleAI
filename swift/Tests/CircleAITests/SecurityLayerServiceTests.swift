// SecurityLayerServiceTests.swift
//
// Validates SecurityLayerService (IPeerSecurityLayer) ported from
// AISecurityLayerService.cs: event → degradation → single most-severe directive
// per threshold crossing, posture snapshot, and none-level no-op.

import XCTest
import Foundation
@testable import CircleAI

private final class CapturingConsumer: IPeerDirectiveConsumer, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var directives: [PeerDirective] = []
    func onDirective(_ directive: PeerDirective) {
        lock.lock(); directives.append(directive); lock.unlock()
    }
    func snapshot() -> [PeerDirective] { lock.lock(); defer { lock.unlock() }; return directives }
}

final class SecurityLayerServiceTests: XCTestCase {

    private func makeLayer() -> (SecurityLayerService, NodeTrustRegistry, SecurityOptions, DirectivePublisher, CapturingConsumer) {
        let opts = SecurityOptions()
        let reg = NodeTrustRegistry(options: opts)
        let pub = DirectivePublisher()
        let layer = SecurityLayerService(registry: reg, options: opts, publisher: pub)
        let consumer = CapturingConsumer()
        _ = layer.subscribeToDirectives(consumer)
        return (layer, reg, opts, pub, consumer)
    }

    private func event(
        node: String, kind: PeerSecurityEventKind, level: PeerThreatLevel, desc: String = "e"
    ) -> PeerSecurityEvent {
        PeerSecurityEvent(nodeId: node, kind: kind, threatLevel: level,
                          description: desc, transportId: "test", occurredAt: Date())
    }

    // ── none-level events are inert ──────────────────────────────────────────

    func testNoneLevelEventDoesNotDegradeOrDirect() {
        let (layer, reg, _, _, consumer) = makeLayer()
        layer.handlePeerEvent(event(node: "p", kind: .intrusionSignal, level: .none))
        XCTAssertEqual(reg.getTrustScore("p"), 1.0, accuracy: 1e-9)
        XCTAssertTrue(consumer.snapshot().isEmpty)
    }

    // ── threshold crossings ──────────────────────────────────────────────────

    func testElevateMonitoringDirectiveOnFirstCrossing() {
        let (layer, _, _, _, consumer) = makeLayer()
        // routingAnomaly medium = 0.10 × 1.0 → 1.0→0.90 (>0.75, no directive)
        // Need to cross 0.75. Two such events: 1.0→0.90→0.80 (still >0.75),
        // third → 0.70 crosses 0.75.
        layer.handlePeerEvent(event(node: "p", kind: .routingAnomaly, level: .medium)) // 0.90
        layer.handlePeerEvent(event(node: "p", kind: .routingAnomaly, level: .medium)) // 0.80
        XCTAssertTrue(consumer.snapshot().isEmpty)
        layer.handlePeerEvent(event(node: "p", kind: .routingAnomaly, level: .medium)) // 0.70 → crosses
        let ds = consumer.snapshot()
        XCTAssertEqual(ds.count, 1)
        XCTAssertEqual(ds.first?.kind, .elevateMonitoring)
        XCTAssertEqual(ds.first?.threatLevel, .medium)
        XCTAssertEqual(ds.first?.targetNodeId, "p")
    }

    func testAvoidNodeDirectiveWhenCrossingAvoidThresholdDirectly() {
        // A drop that skips straight past the avoid threshold to below quarantine
        // must issue the MOST severe directive reached (quarantine), and exactly
        // one for that event.
        let (layer, _, _, _, consumer) = makeLayer()
        // dataExfiltration critical = 0.14×3 = 0.42.
        layer.handlePeerEvent(event(node: "p", kind: .dataExfiltration, level: .critical)) // 1.0→0.58 → elevate
        let before = consumer.snapshot().count
        layer.handlePeerEvent(event(node: "p", kind: .dataExfiltration, level: .critical)) // 0.58→0.16
        // 0.16 ≤ quarantine(0.25) so the most-severe rule fires: quarantine.
        let issued = Array(consumer.snapshot().dropFirst(before))
        XCTAssertEqual(issued.count, 1)
        XCTAssertEqual(issued.first?.kind, .quarantineNode)
        XCTAssertEqual(issued.first?.threatLevel, .critical)
    }

    func testQuarantineDirectiveOnSevereDrop() {
        let (layer, _, _, _, consumer) = makeLayer()
        // intrusion critical = 0.45. 1.0→0.55 (elevate), then 0.55→0.10 (quarantine).
        layer.handlePeerEvent(event(node: "p", kind: .intrusionSignal, level: .critical)) // 0.55 → elevate
        let before = consumer.snapshot().count
        layer.handlePeerEvent(event(node: "p", kind: .intrusionSignal, level: .critical)) // 0.10 → quarantine
        let issued = Array(consumer.snapshot().dropFirst(before))
        XCTAssertEqual(issued.count, 1)
        XCTAssertEqual(issued.first?.kind, .quarantineNode)
    }

    func testAtMostOneDirectivePerEvent() {
        // A single event that straddles avoid AND quarantine must issue exactly
        // one directive (the most severe reached), never two.
        let (layer, _, _, _, consumer) = makeLayer()
        // denialOfService critical = 0.13×3 = 0.39.
        layer.handlePeerEvent(event(node: "p", kind: .denialOfService, level: .critical)) // 1.0→0.61 → elevate
        let before = consumer.snapshot().count
        layer.handlePeerEvent(event(node: "p", kind: .denialOfService, level: .critical)) // 0.61→0.22 → quarantine
        let issued = Array(consumer.snapshot().dropFirst(before))
        XCTAssertEqual(issued.count, 1)
        XCTAssertEqual(issued.first?.kind, .quarantineNode)
    }

    func testDirectiveDurationIsNilAndPermanent() {
        let (layer, _, _, _, consumer) = makeLayer()
        for _ in 0..<3 { layer.handlePeerEvent(event(node: "p", kind: .routingAnomaly, level: .medium)) }
        XCTAssertNil(consumer.snapshot().first?.duration)
    }

    // ── posture snapshot ─────────────────────────────────────────────────────

    func testPostureWithNoPeersIsIdle() async throws {
        let (layer, _, _, _, _) = makeLayer()
        let posture = try await layer.getPosture()
        XCTAssertEqual(posture.overallThreatLevel, .none)
        XCTAssertEqual(posture.quarantinedPeerCount, 0)
        XCTAssertEqual(posture.monitoredPeerCount, 0)
        XCTAssertFalse(posture.isActive)
    }

    func testPostureCountsQuarantinedAndMonitored() async throws {
        let (layer, reg, _, _, _) = makeLayer()
        // Drive one peer to quarantine (≤0.25) and one into monitoring band.
        _ = reg.applyDegradation(event(node: "q", kind: .authAttempt, level: .medium), degradationAmount: 0.80) // 0.20 quarantined
        _ = reg.applyDegradation(event(node: "m", kind: .authAttempt, level: .medium), degradationAmount: 0.40) // 0.60 monitored band
        let posture = try await layer.getPosture()
        XCTAssertEqual(posture.quarantinedPeerCount, 1)
        XCTAssertEqual(posture.monitoredPeerCount, 1)
        XCTAssertEqual(posture.overallThreatLevel, .critical) // worst peer 0.20
    }

    func testStartFlipsActiveAndStopClearsIt() async throws {
        let (layer, _, _, _, _) = makeLayer()
        XCTAssertFalse(layer.isActive)
        try await layer.start()
        XCTAssertTrue(layer.isActive)
        var posture = try await layer.getPosture()
        XCTAssertTrue(posture.isActive)
        try await layer.stop()
        XCTAssertFalse(layer.isActive)
        posture = try await layer.getPosture()
        XCTAssertFalse(posture.isActive)
    }

    func testApplyRecoveryTickHealsScore() async throws {
        let opts = SecurityOptions()
        opts.recoveryRatePerSecond = 0.01
        let reg = NodeTrustRegistry(options: opts)
        let pub = DirectivePublisher()
        let layer = SecurityLayerService(registry: reg, options: opts, publisher: pub)
        _ = reg.applyDegradation(event(node: "p", kind: .authAttempt, level: .medium), degradationAmount: 0.5)
        layer.applyRecoveryTick(elapsed: 10) // +0.1
        XCTAssertEqual(reg.getTrustScore("p"), 0.6, accuracy: 1e-9)
    }
}
