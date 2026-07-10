// AetherContractsTests.swift
//
// Validates the CircleAI.Aether port (Aether.swift): enum ordinals (cross-
// language wire), DTO derived-property logic, Codable round-trips, the in-memory
// telemetry hub fan-out, the in-memory auth-challenge gate, and the in-memory
// context version logic.

import XCTest
import Foundation
@testable import CircleAI

final class AetherContractsTests: XCTestCase {

    // ── Enum ordinals (mirror C# declaration order / explicit values) ─────────

    func testThreatLevelOrdinalsAndComparable() {
        XCTAssertEqual(AetherThreatLevel.none.rawValue,     0)
        XCTAssertEqual(AetherThreatLevel.low.rawValue,      1)
        XCTAssertEqual(AetherThreatLevel.medium.rawValue,   2)
        XCTAssertEqual(AetherThreatLevel.high.rawValue,     3)
        XCTAssertEqual(AetherThreatLevel.critical.rawValue, 4)
        XCTAssertTrue(AetherThreatLevel.low < AetherThreatLevel.critical)
        XCTAssertEqual([AetherThreatLevel.none, .high, .low].max(), .high)
    }

    func testAuthMethodExplicitValuesAndOrdering() {
        // C# assigns Biometric = 1 … Custom = 4 (NOT declaration index).
        XCTAssertEqual(AuthMethod.biometric.rawValue,               1)
        XCTAssertEqual(AuthMethod.deviceAdmin.rawValue,             2)
        XCTAssertEqual(AuthMethod.biometricAndDeviceAdmin.rawValue, 3)
        XCTAssertEqual(AuthMethod.custom.rawValue,                  4)
        XCTAssertTrue(AuthMethod.biometric < AuthMethod.biometricAndDeviceAdmin)
        XCTAssertEqual(max(AuthMethod.biometric, AuthMethod.deviceAdmin), .deviceAdmin)
    }

    func testAuthChallengeReasonOrdinals() {
        XCTAssertEqual(AuthChallengeReason.osLevelToggle.rawValue,          0)
        XCTAssertEqual(AuthChallengeReason.threatThresholdReached.rawValue, 1)
        XCTAssertEqual(AuthChallengeReason.privilegedOperation.rawValue,    2)
        XCTAssertEqual(AuthChallengeReason.periodicRevalidation.rawValue,   3)
        XCTAssertEqual(AuthChallengeReason.manualRequest.rawValue,          4)
    }

    func testSecurityDirectiveKindOrdinals() {
        XCTAssertEqual(SecurityDirectiveKind.updateNodeTrust.rawValue,   0)
        XCTAssertEqual(SecurityDirectiveKind.avoidNode.rawValue,         1)
        XCTAssertEqual(SecurityDirectiveKind.quarantineNode.rawValue,    2)
        XCTAssertEqual(SecurityDirectiveKind.releaseNode.rawValue,       3)
        XCTAssertEqual(SecurityDirectiveKind.requestReauth.rawValue,     4)
        XCTAssertEqual(SecurityDirectiveKind.elevateMonitoring.rawValue, 5)
    }

    func testInstallLevelOrdinals() {
        XCTAssertEqual(AetherInstallLevel.none.rawValue, 0)
        XCTAssertEqual(AetherInstallLevel.app.rawValue,  1)
        XCTAssertEqual(AetherInstallLevel.os.rawValue,   2)
    }

    func testEventKindOrdinals() {
        XCTAssertEqual(AetherNodeEventKind.joined.rawValue,        0)
        XCTAssertEqual(AetherNodeEventKind.left.rawValue,          1)
        XCTAssertEqual(AetherNodeEventKind.healthChanged.rawValue, 2)

        XCTAssertEqual(AetherTransportKind.wiFi.rawValue,      0)
        XCTAssertEqual(AetherTransportKind.unknown.rawValue,   6)

        XCTAssertEqual(AetherSecurityEventKind.nodeAuthAttempt.rawValue, 0)
        XCTAssertEqual(AetherSecurityEventKind.privilegeAttempt.rawValue, 5)

        XCTAssertEqual(AetherNetworkEventKind.topologyChanged.rawValue, 0)
        XCTAssertEqual(AetherNetworkEventKind.partitionDetected.rawValue, 2)
    }

    // ── DTO derived-property logic ────────────────────────────────────────────

    func testNodeEventIsExit() {
        let h = AetherNodeHealth(trustScore: 1.0, isReachable: true, latency: 0.01, hopCount: 1)
        let joined = AetherNodeEvent(nodeId: "n", kind: .joined, health: h, occurredAt: Date())
        let left = AetherNodeEvent(nodeId: "n", kind: .left, health: h, occurredAt: Date())
        XCTAssertFalse(joined.isExit)
        XCTAssertTrue(left.isExit)
        XCTAssertTrue(h.isValid)
        XCTAssertFalse(AetherNodeHealth(trustScore: 1.5, isReachable: true, latency: 0, hopCount: 0).isValid)
    }

    func testTransportEventExceedsLoss() {
        let e = AetherTransportEvent(nodeId: "n", kind: .packetLoss, transport: .wiFi,
                                     latency: nil, packetLossRate: 0.5, occurredAt: Date())
        XCTAssertTrue(e.exceedsLoss(0.4))
        XCTAssertFalse(e.exceedsLoss(0.6))
        let noRate = AetherTransportEvent(nodeId: "n", kind: .selected, transport: .wiFi,
                                          latency: nil, packetLossRate: nil, occurredAt: Date())
        XCTAssertFalse(noRate.exceedsLoss(0.0))
    }

    func testRouteEventHopCountAndFailed() {
        let e = AetherRouteEvent(sourceNodeId: "a", destinationNodeId: "c",
                                 path: ["a", "b", "c"], kind: .failed,
                                 failureReason: "timeout", occurredAt: Date())
        XCTAssertEqual(e.hopCount, 3)
        XCTAssertTrue(e.isFailed)
    }

    func testSecurityEventHighSeverity() {
        let hi = AetherSecurityEvent(nodeId: "n", kind: .intrusionSignal, threatLevel: .high,
                                     description: "d", metadata: [:], occurredAt: Date())
        let lo = AetherSecurityEvent(nodeId: "n", kind: .routingAnomaly, threatLevel: .low,
                                     description: "d", metadata: [:], occurredAt: Date())
        XCTAssertTrue(hi.isHighSeverity)
        XCTAssertFalse(lo.isHighSeverity)
    }

    func testNetworkEventHighCongestion() {
        let e = AetherNetworkEvent(kind: .congestionDetected, nodeCount: 5, activeRouteCount: 8,
                                   congestionLevel: 0.8, occurredAt: Date())
        XCTAssertTrue(e.isHighCongestion)
        XCTAssertFalse(AetherNetworkEvent(kind: .topologyChanged, nodeCount: 1, activeRouteCount: 1,
                                          congestionLevel: 0.5, occurredAt: Date()).isHighCongestion)
    }

    func testSecurityDirectiveHasTargetAndPermanent() {
        let targeted = SecurityDirective(kind: .avoidNode, targetNodeId: "n", trustScoreOverride: nil,
                                         threatLevel: .high, reason: "r", duration: nil, issuedAt: Date())
        XCTAssertTrue(targeted.hasTarget)
        XCTAssertTrue(targeted.isPermanent)

        let blankTarget = SecurityDirective(kind: .avoidNode, targetNodeId: "   ", trustScoreOverride: nil,
                                            threatLevel: .high, reason: "r", duration: nil, issuedAt: Date())
        XCTAssertFalse(blankTarget.hasTarget)

        let expiring = SecurityDirective(kind: .avoidNode, targetNodeId: "n", trustScoreOverride: nil,
                                         threatLevel: .high, reason: "r", duration: 60, issuedAt: Date())
        XCTAssertFalse(expiring.isPermanent)
    }

    func testTrustScoreUpdateChangeFlags() {
        let degraded = TrustScoreUpdate(nodeId: "n", previousScore: 0.9, currentScore: 0.5,
                                        reason: "r", updatedAt: Date())
        XCTAssertTrue(degraded.hasChanged)
        XCTAssertTrue(degraded.isDegraded)
        let flat = TrustScoreUpdate(nodeId: "n", previousScore: 0.5, currentScore: 0.5,
                                    reason: "r", updatedAt: Date())
        XCTAssertFalse(flat.hasChanged)
        XCTAssertFalse(flat.isDegraded)
    }

    func testReportValidity() {
        XCTAssertTrue(NetworkHealthReport(overallScore: 0.5, trustedNodeCount: 1,
                                          suspiciousNodeCount: 0, summary: "s", generatedAt: Date()).isValid)
        XCTAssertFalse(NetworkHealthReport(overallScore: 1.5, trustedNodeCount: 0,
                                           suspiciousNodeCount: 0, summary: "s", generatedAt: Date()).isValid)
        XCTAssertTrue(ThreatAssessment(nodeId: "n", threatConfidence: 0.0, level: .none,
                                       indicators: [], assessedAt: Date()).isValid)
        XCTAssertFalse(ThreatAssessment(nodeId: "n", threatConfidence: -0.1, level: .none,
                                        indicators: [], assessedAt: Date()).isValid)
    }

    // ── AuthChallengeResult factories ─────────────────────────────────────────

    func testAuthResultFactories() {
        let ok = AuthChallengeResult.success(.biometricAndDeviceAdmin)
        XCTAssertTrue(ok.succeeded)
        XCTAssertNil(ok.failureReason)
        let bad = AuthChallengeResult.failure(.biometric, reason: "weak")
        XCTAssertFalse(bad.succeeded)
        XCTAssertEqual(bad.failureReason, "weak")
    }

    // ── Codable round-trips ───────────────────────────────────────────────────

    func testSecurityEventCodableRoundTrip() throws {
        let e = AetherSecurityEvent(nodeId: "n", kind: .intrusionSignal, threatLevel: .critical,
                                    description: "probe", metadata: ["k": "v"],
                                    occurredAt: Date(timeIntervalSince1970: 1_700_000_000))
        let data = try JSONEncoder().encode(e)
        let back = try JSONDecoder().decode(AetherSecurityEvent.self, from: data)
        XCTAssertEqual(back, e)
    }

    func testSecurityDirectiveCodableRoundTrip() throws {
        let d = SecurityDirective(kind: .quarantineNode, targetNodeId: "n", trustScoreOverride: 0.1,
                                  threatLevel: .critical, reason: "r", duration: 30,
                                  issuedAt: Date(timeIntervalSince1970: 1))
        let data = try JSONEncoder().encode(d)
        let back = try JSONDecoder().decode(SecurityDirective.self, from: data)
        XCTAssertEqual(back, d)
    }

    func testSemanticVersionComparisonAndCodable() throws {
        XCTAssertTrue(SemanticVersion(major: 2, minor: 6) < SemanticVersion(major: 2, minor: 7))
        XCTAssertTrue(SemanticVersion(major: 3) > SemanticVersion(major: 2, minor: 9, build: 9))
        XCTAssertEqual(SemanticVersion(major: 2, minor: 6, build: 0, revision: 0).description, "2.6.0.0")
        let v = SemanticVersion(major: 2, minor: 6, build: 1, revision: 4)
        let back = try JSONDecoder().decode(SemanticVersion.self, from: JSONEncoder().encode(v))
        XCTAssertEqual(back, v)
    }

    // ── NullAetherTelemetry ───────────────────────────────────────────────────

    func testNullTelemetrySubscribeReturnsNoOpHandle() {
        final class Obs: IAetherTelemetryObserver {
            func onNodeEvent(_ e: AetherNodeEvent) {}
            func onTransportEvent(_ e: AetherTransportEvent) {}
            func onRouteEvent(_ e: AetherRouteEvent) {}
            func onSecurityEvent(_ e: AetherSecurityEvent) {}
            func onNetworkEvent(_ e: AetherNetworkEvent) {}
        }
        let sub = NullAetherTelemetry.shared.subscribe(Obs())
        sub.dispose() // idempotent, no crash
        sub.dispose()
    }

    // ── InMemoryAetherTelemetry fan-out ───────────────────────────────────────

    private final class RecordingObserver: IAetherTelemetryObserver, @unchecked Sendable {
        private let lock = NSLock()
        private(set) var security: [AetherSecurityEvent] = []
        private(set) var nodes: [AetherNodeEvent] = []
        func onNodeEvent(_ e: AetherNodeEvent) { lock.lock(); nodes.append(e); lock.unlock() }
        func onTransportEvent(_ e: AetherTransportEvent) {}
        func onRouteEvent(_ e: AetherRouteEvent) {}
        func onSecurityEvent(_ e: AetherSecurityEvent) { lock.lock(); security.append(e); lock.unlock() }
        func onNetworkEvent(_ e: AetherNetworkEvent) {}
        func securityCount() -> Int { lock.lock(); defer { lock.unlock() }; return security.count }
        func nodeCount() -> Int { lock.lock(); defer { lock.unlock() }; return nodes.count }
    }

    func testInMemoryTelemetryFansOutToAllObservers() {
        let hub = InMemoryAetherTelemetry()
        let a = RecordingObserver()
        let b = RecordingObserver()
        let sa = hub.subscribe(a)
        _ = hub.subscribe(b)
        XCTAssertEqual(hub.subscriberCount, 2)

        let ev = AetherSecurityEvent(nodeId: "n", kind: .intrusionSignal, threatLevel: .high,
                                     description: "d", metadata: [:], occurredAt: Date())
        hub.publishSecurityEvent(ev)
        XCTAssertEqual(a.securityCount(), 1)
        XCTAssertEqual(b.securityCount(), 1)

        // Dispose one; it stops receiving. The other still does.
        sa.dispose()
        XCTAssertEqual(hub.subscriberCount, 1)
        hub.publishSecurityEvent(ev)
        XCTAssertEqual(a.securityCount(), 1) // unchanged
        XCTAssertEqual(b.securityCount(), 2)

        hub.publishNodeEvent(AetherNodeEvent(
            nodeId: "n", kind: .left,
            health: AetherNodeHealth(trustScore: 1, isReachable: false, latency: 0, hopCount: 0),
            occurredAt: Date()))
        XCTAssertEqual(b.nodeCount(), 1)
    }

    // ── InMemoryAetherContext version logic ───────────────────────────────────

    func testContextSufficiencyAndAuthFlags() {
        let ctxOk = InMemoryAetherContext(
            runtimeVersion: SemanticVersion(major: 2, minor: 7),
            minimumRequired: SemanticVersion(major: 2, minor: 6))
        XCTAssertTrue(ctxOk.isSufficient)
        XCTAssertTrue(ctxOk.isAvailable)
        XCTAssertEqual(ctxOk.installLevel, .app)
        XCTAssertFalse(ctxOk.requiresAuth)

        let ctxTooOld = InMemoryAetherContext(
            runtimeVersion: SemanticVersion(major: 2, minor: 5),
            minimumRequired: SemanticVersion(major: 2, minor: 6))
        XCTAssertFalse(ctxTooOld.isSufficient)

        // No minimum → always sufficient (even absent runtime).
        let noMin = InMemoryAetherContext(runtimeVersion: nil, minimumRequired: nil)
        XCTAssertTrue(noMin.isSufficient)

        // OS install requires auth; disabled → not available.
        let osCtx = InMemoryAetherContext(installLevel: .os,
                                          runtimeVersion: SemanticVersion(major: 1),
                                          minimumRequired: nil, isEnabled: false)
        XCTAssertTrue(osCtx.requiresAuth)
        XCTAssertFalse(osCtx.isAvailable)

        // None install → not available regardless of enabled flag.
        let noneCtx = InMemoryAetherContext(installLevel: .none, runtimeVersion: nil, isEnabled: true)
        XCTAssertFalse(noneCtx.isAvailable)
    }

    // ── InMemoryAuthChallenge gate ────────────────────────────────────────────

    func testAuthChallengeSucceedsWhenDeviceMeetsMinimum() async throws {
        let gate = InMemoryAuthChallenge() // fully capable
        let r = try await gate.challenge(reason: .privilegedOperation,
                                         minimumMethod: .biometricAndDeviceAdmin,
                                         prompt: "confirm")
        XCTAssertTrue(r.succeeded)
        XCTAssertEqual(r.methodUsed, .custom) // strongest available
    }

    func testAuthChallengeFloorsBelowBiometricAndDeviceAdmin() async throws {
        // A device that can only do biometric cannot satisfy the absolute floor.
        let gate = InMemoryAuthChallenge(available: [.biometric])
        // Even requesting only .biometric, the floor raises the bar to
        // .biometricAndDeviceAdmin, which the device can't meet → failure.
        let r = try await gate.challenge(reason: .manualRequest, minimumMethod: .biometric, prompt: "p")
        XCTAssertFalse(r.succeeded)
        XCTAssertNotNil(r.failureReason)
    }

    func testAuthChallengeNilMinimumUsesFloor() async throws {
        let capable = InMemoryAuthChallenge(available: [.biometricAndDeviceAdmin])
        let r = try await capable.challenge(reason: .osLevelToggle, minimumMethod: nil, prompt: "p")
        XCTAssertTrue(r.succeeded)
    }

    func testRequestOsToggleAlwaysDemandsFloor() async throws {
        let weak = InMemoryAuthChallenge(available: [.deviceAdmin]) // can't reach floor(3)
        let deny = try await weak.requestOsToggle(enable: true)
        XCTAssertFalse(deny.succeeded)

        let strong = InMemoryAuthChallenge(available: [.biometricAndDeviceAdmin, .custom])
        let allow = try await strong.requestOsToggle(enable: false)
        XCTAssertTrue(allow.succeeded)
        XCTAssertEqual(allow.methodUsed, .custom)
    }
}
