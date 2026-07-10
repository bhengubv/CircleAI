// ThreatDetectorTests.swift
//
// Validates the pure static threat logic ported from ThreatDetector.cs:
//   • computeDegradation = baseWeight(kind) × threatMultiplier(level)
//   • detectIndicators windowing + each indicator rule.

import XCTest
import Foundation
@testable import CircleAI

final class ThreatDetectorTests: XCTestCase {

    private func evt(
        _ kind: PeerSecurityEventKind,
        _ level: PeerThreatLevel,
        ageSeconds: TimeInterval = 0,
        node: String = "peer-1"
    ) -> PeerSecurityEvent {
        PeerSecurityEvent(
            nodeId: node, kind: kind, threatLevel: level,
            description: "\(kind)", transportId: "test",
            occurredAt: Date().addingTimeInterval(-ageSeconds))
    }

    // ── computeDegradation ───────────────────────────────────────────────────

    func testDegradationNoneLevelIsZero() {
        XCTAssertEqual(ThreatDetector.computeDegradation(evt(.intrusionSignal, .none)), 0.0, accuracy: 1e-9)
    }

    func testDegradationAuthAttemptMedium() {
        // baseWeight(auth)=0.05 × mult(medium)=1.0
        XCTAssertEqual(ThreatDetector.computeDegradation(evt(.authAttempt, .medium)), 0.05, accuracy: 1e-9)
    }

    func testDegradationIntrusionCritical() {
        // baseWeight(intrusion)=0.15 × mult(critical)=3.0 = 0.45
        XCTAssertEqual(ThreatDetector.computeDegradation(evt(.intrusionSignal, .critical)), 0.45, accuracy: 1e-9)
    }

    func testDegradationDataExfiltrationHigh() {
        // 0.14 × 2.0 = 0.28
        XCTAssertEqual(ThreatDetector.computeDegradation(evt(.dataExfiltration, .high)), 0.28, accuracy: 1e-9)
    }

    func testDegradationLowLevelHalvesWeight() {
        // denialOfService 0.13 × low 0.5 = 0.065
        XCTAssertEqual(ThreatDetector.computeDegradation(evt(.denialOfService, .low)), 0.065, accuracy: 1e-9)
    }

    func testDegradationCoversEveryKind() {
        // Each kind maps to its documented base weight at medium (mult 1.0).
        let expected: [(PeerSecurityEventKind, Double)] = [
            (.authAttempt, 0.05), (.routingAnomaly, 0.10), (.behaviourChange, 0.08),
            (.encryptionEvent, 0.06), (.intrusionSignal, 0.15), (.privilegeAttempt, 0.12),
            (.connectionAnomaly, 0.07), (.dataExfiltration, 0.14), (.denialOfService, 0.13),
            (.unknown, 0.05),
        ]
        for (kind, weight) in expected {
            XCTAssertEqual(ThreatDetector.computeDegradation(evt(kind, .medium)), weight, accuracy: 1e-9,
                           "kind \(kind)")
        }
    }

    // ── detectIndicators — empty / windowing ─────────────────────────────────

    func testDetectIndicatorsEmptyWhenNoEvents() {
        XCTAssertTrue(ThreatDetector.detectIndicators([], window: 300).isEmpty)
    }

    func testDetectIndicatorsIgnoresEventsOutsideWindow() {
        // All events are older than the 60s window → nothing detected.
        let old = (0..<5).map { _ in evt(.authAttempt, .low, ageSeconds: 120) }
        XCTAssertTrue(ThreatDetector.detectIndicators(old, window: 60).isEmpty)
    }

    // ── detectIndicators — individual rules ──────────────────────────────────

    func testRepeatedAuthAttemptsAtThreeInWindow() {
        let events = (0..<3).map { _ in evt(.authAttempt, .low) }
        XCTAssertTrue(ThreatDetector.detectIndicators(events, window: 300).contains("repeated-auth-attempts"))
    }

    func testRepeatedAuthAttemptsNotFlaggedAtTwo() {
        let events = (0..<2).map { _ in evt(.authAttempt, .low) }
        XCTAssertFalse(ThreatDetector.detectIndicators(events, window: 300).contains("repeated-auth-attempts"))
    }

    func testIntrusionSignalDetected() {
        let out = ThreatDetector.detectIndicators([evt(.intrusionSignal, .low)], window: 300)
        XCTAssertTrue(out.contains("intrusion-signal-detected"))
    }

    func testHighSeverityEventFlag() {
        let out = ThreatDetector.detectIndicators([evt(.behaviourChange, .critical)], window: 300)
        XCTAssertTrue(out.contains("high-severity-event"))
    }

    func testMultiVectorActivityAtThreeDistinctKinds() {
        let events = [
            evt(.authAttempt, .low),
            evt(.routingAnomaly, .low),
            evt(.encryptionEvent, .low),
        ]
        XCTAssertTrue(ThreatDetector.detectIndicators(events, window: 300).contains("multi-vector-activity"))
    }

    func testMultiVectorNotFlaggedAtTwoDistinctKinds() {
        let events = [evt(.authAttempt, .low), evt(.routingAnomaly, .low), evt(.authAttempt, .low)]
        XCTAssertFalse(ThreatDetector.detectIndicators(events, window: 300).contains("multi-vector-activity"))
    }

    func testPrivilegeEscalationIndicator() {
        let out = ThreatDetector.detectIndicators([evt(.privilegeAttempt, .low)], window: 300)
        XCTAssertTrue(out.contains("privilege-escalation-attempt"))
    }

    func testDataExfiltrationIndicator() {
        let out = ThreatDetector.detectIndicators([evt(.dataExfiltration, .low)], window: 300)
        XCTAssertTrue(out.contains("data-exfiltration-signal"))
    }

    // ── detectIndicators — ordering / composition ────────────────────────────

    func testIndicatorOrderMatchesReference() {
        // 3 auth (brute force) + intrusion + a critical + 3 distinct kinds +
        // privilege + exfil — all rules fire; order must match the C# append order.
        let events = [
            evt(.authAttempt, .low), evt(.authAttempt, .low), evt(.authAttempt, .low),
            evt(.intrusionSignal, .critical),
            evt(.privilegeAttempt, .low),
            evt(.dataExfiltration, .low),
        ]
        let out = ThreatDetector.detectIndicators(events, window: 300)
        XCTAssertEqual(out, [
            "repeated-auth-attempts",
            "intrusion-signal-detected",
            "high-severity-event",
            "multi-vector-activity",
            "privilege-escalation-attempt",
            "data-exfiltration-signal",
        ])
    }
}
