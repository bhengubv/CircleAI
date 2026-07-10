// PeerSecurityTypesTests.swift
//
// Locks the cross-language wire ordinals for the Peer* enums and the
// Codable round-trip of the peer-security DTOs. Ordinals mirror the C#
// declaration order in PeerSecurityTypes.cs / SecurityResponse.cs /
// IAnomalyEventDispatcher.cs — any change here must update every other port.

import XCTest
import Foundation
@testable import CircleAI

final class PeerSecurityTypesTests: XCTestCase {

    // ── PeerSecurityEventKind ────────────────────────────────────────────────

    func testPeerSecurityEventKindOrdinals() {
        XCTAssertEqual(PeerSecurityEventKind.authAttempt.rawValue,       0)
        XCTAssertEqual(PeerSecurityEventKind.routingAnomaly.rawValue,    1)
        XCTAssertEqual(PeerSecurityEventKind.behaviourChange.rawValue,   2)
        XCTAssertEqual(PeerSecurityEventKind.encryptionEvent.rawValue,   3)
        XCTAssertEqual(PeerSecurityEventKind.intrusionSignal.rawValue,   4)
        XCTAssertEqual(PeerSecurityEventKind.privilegeAttempt.rawValue,  5)
        XCTAssertEqual(PeerSecurityEventKind.connectionAnomaly.rawValue, 6)
        XCTAssertEqual(PeerSecurityEventKind.dataExfiltration.rawValue,  7)
        XCTAssertEqual(PeerSecurityEventKind.denialOfService.rawValue,   8)
        XCTAssertEqual(PeerSecurityEventKind.unknown.rawValue,           9)
        XCTAssertEqual(PeerSecurityEventKind.allCases.count, 10)
    }

    // ── PeerThreatLevel ──────────────────────────────────────────────────────

    func testPeerThreatLevelOrdinals() {
        XCTAssertEqual(PeerThreatLevel.none.rawValue,     0)
        XCTAssertEqual(PeerThreatLevel.low.rawValue,      1)
        XCTAssertEqual(PeerThreatLevel.medium.rawValue,   2)
        XCTAssertEqual(PeerThreatLevel.high.rawValue,     3)
        XCTAssertEqual(PeerThreatLevel.critical.rawValue, 4)
    }

    func testPeerThreatLevelIsComparable() {
        XCTAssertTrue(PeerThreatLevel.none < PeerThreatLevel.low)
        XCTAssertTrue(PeerThreatLevel.medium < PeerThreatLevel.critical)
        XCTAssertEqual([PeerThreatLevel.critical, .none, .high, .low].max(), .critical)
    }

    // ── PeerDirectiveKind ────────────────────────────────────────────────────

    func testPeerDirectiveKindOrdinals() {
        XCTAssertEqual(PeerDirectiveKind.elevateMonitoring.rawValue, 0)
        XCTAssertEqual(PeerDirectiveKind.avoidNode.rawValue,         1)
        XCTAssertEqual(PeerDirectiveKind.quarantineNode.rawValue,    2)
        XCTAssertEqual(PeerDirectiveKind.releaseNode.rawValue,       3)
    }

    // ── SecurityResponseKind ─────────────────────────────────────────────────

    func testSecurityResponseKindOrdinals() {
        XCTAssertEqual(SecurityResponseKind.noAction.rawValue,            0)
        XCTAssertEqual(SecurityResponseKind.keyRotation.rawValue,         1)
        XCTAssertEqual(SecurityResponseKind.sessionRevocation.rawValue,   2)
        XCTAssertEqual(SecurityResponseKind.meshIsolationSignal.rawValue, 3)
        XCTAssertEqual(SecurityResponseKind.stateRollback.rawValue,       4)
        XCTAssertEqual(SecurityResponseKind.composite.rawValue,           5)
    }

    // ── AnomalyDispatchOutcome ───────────────────────────────────────────────

    func testAnomalyDispatchOutcomeOrdinals() {
        XCTAssertEqual(AnomalyDispatchOutcome.dispatched.rawValue,     0)
        XCTAssertEqual(AnomalyDispatchOutcome.duplicate.rawValue,      1)
        XCTAssertEqual(AnomalyDispatchOutcome.belowThreshold.rawValue, 2)
        XCTAssertEqual(AnomalyDispatchOutcome.unverified.rawValue,     3)
        XCTAssertEqual(AnomalyDispatchOutcome.cancelled.rawValue,      4)
    }

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testPeerSecurityEventCodableRoundTrip() throws {
        let e = PeerSecurityEvent(
            nodeId: "peer-1", kind: .intrusionSignal, threatLevel: .high,
            description: "probe", transportId: "aether",
            occurredAt: Date(timeIntervalSince1970: 1_700_000_000))
        let data = try JSONEncoder().encode(e)
        let back = try JSONDecoder().decode(PeerSecurityEvent.self, from: data)
        XCTAssertEqual(back, e)
    }

    func testPeerDirectiveCodableRoundTrip() throws {
        let d = PeerDirective(
            kind: .quarantineNode, targetNodeId: "peer-9", trustScore: 0.1,
            threatLevel: .critical, reason: "compromised", duration: nil,
            issuedAt: Date(timeIntervalSince1970: 1_700_000_500))
        let data = try JSONEncoder().encode(d)
        let back = try JSONDecoder().decode(PeerDirective.self, from: data)
        XCTAssertEqual(back, d)
    }

    func testPeerTrustScoreUpdateCodableRoundTrip() throws {
        let u = PeerTrustScoreUpdate(
            nodeId: "peer-3", previousScore: 1.0, newScore: 0.9,
            reason: "auth", changedAt: Date(timeIntervalSince1970: 1))
        let data = try JSONEncoder().encode(u)
        let back = try JSONDecoder().decode(PeerTrustScoreUpdate.self, from: data)
        XCTAssertEqual(back, u)
    }

    func testPeerThreatAssessmentCodableRoundTrip() throws {
        let a = PeerThreatAssessment(
            nodeId: "peer-2", confidence: 0.42, threatLevel: .medium,
            indicators: ["intrusion-signal-detected", "high-severity-event"],
            assessedAt: Date(timeIntervalSince1970: 5))
        let data = try JSONEncoder().encode(a)
        let back = try JSONDecoder().decode(PeerThreatAssessment.self, from: data)
        XCTAssertEqual(back, a)
    }
}
