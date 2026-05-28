// AnomalySignalTests.swift
//
// Validates ThreatVector raw values (cross-language stable ordinals) and
// AnomalySignal.create — including the confidence-clamp contract that all
// language ports share.

import XCTest
import Foundation
@testable import CircleAI

final class AnomalySignalTests: XCTestCase {

    // ── ThreatVector raw values ──────────────────────────────────────────────
    // Ordinals are part of the cross-language wire format. Any change here
    // must also update every other port and fixtures/anomaly_signal_schema.json.

    func testThreatVectorRawValues() {
        XCTAssertEqual(ThreatVector.memoryAnomaly.rawValue,         0)
        XCTAssertEqual(ThreatVector.controlFlowDrift.rawValue,      1)
        XCTAssertEqual(ThreatVector.privilegeEscalation.rawValue,   2)
        XCTAssertEqual(ThreatVector.biometricSpoofAttempt.rawValue, 3)
        XCTAssertEqual(ThreatVector.networkPivot.rawValue,          4)
        XCTAssertEqual(ThreatVector.stateCorruption.rawValue,       5)
        XCTAssertEqual(ThreatVector.agentPatchRejected.rawValue,    6)
        XCTAssertEqual(ThreatVector.unknown.rawValue,               7)
    }

    func testThreatVectorRoundTripFromRawValue() {
        for raw in 0...7 {
            XCTAssertNotNil(ThreatVector(rawValue: raw),
                            "ThreatVector(rawValue: \(raw)) should decode")
        }
        XCTAssertNil(ThreatVector(rawValue: 8))
        XCTAssertNil(ThreatVector(rawValue: -1))
    }

    func testThreatVectorCodableRoundTrip() throws {
        let original = ThreatVector.biometricSpoofAttempt
        let encoded = try JSONEncoder().encode(original)
        let decoded = try JSONDecoder().decode(ThreatVector.self, from: encoded)
        XCTAssertEqual(decoded, original)
    }

    // ── AnomalySignal.create — confidence clamp ──────────────────────────────

    func testCreateClampsConfidenceAboveMax() {
        let s = AnomalySignal.create(
            vector: .memoryAnomaly,
            confidence: 1.5,
            affectedModule: "Circle.AI.Companion",
            description: "above-max"
        )
        XCTAssertEqual(s.confidence, 1.0, accuracy: 1e-6)
    }

    func testCreateClampsConfidenceBelowMin() {
        let s = AnomalySignal.create(
            vector: .memoryAnomaly,
            confidence: -0.3,
            affectedModule: "Circle.AI.Companion",
            description: "below-min"
        )
        XCTAssertEqual(s.confidence, 0.0, accuracy: 1e-6)
    }

    func testCreatePreservesConfidenceAtMax() {
        let s = AnomalySignal.create(
            vector: .unknown,
            confidence: 1.0,
            affectedModule: "Circle.AI.Companion",
            description: "at-max"
        )
        XCTAssertEqual(s.confidence, 1.0, accuracy: 1e-6)
    }

    func testCreatePreservesConfidenceAtMin() {
        let s = AnomalySignal.create(
            vector: .unknown,
            confidence: 0.0,
            affectedModule: "Circle.AI.Companion",
            description: "at-min"
        )
        XCTAssertEqual(s.confidence, 0.0, accuracy: 1e-6)
    }

    func testCreatePreservesNominalConfidence() {
        let s = AnomalySignal.create(
            vector: .controlFlowDrift,
            confidence: 0.7,
            affectedModule: "Circle.AI.Companion",
            description: "nominal"
        )
        XCTAssertEqual(s.confidence, 0.7, accuracy: 1e-6)
    }

    // ── AnomalySignal.create — other fields ──────────────────────────────────

    func testCreatePopulatesAllRequiredFields() {
        let signal = AnomalySignal.create(
            vector: .privilegeEscalation,
            confidence: 0.42,
            affectedModule: "Circle.AI.Identity",
            description: "Attempt to read admin-scoped key"
        )
        XCTAssertEqual(signal.vector, .privilegeEscalation)
        XCTAssertEqual(signal.confidence, 0.42, accuracy: 1e-6)
        XCTAssertEqual(signal.affectedModule, "Circle.AI.Identity")
        XCTAssertEqual(signal.description, "Attempt to read admin-scoped key")
    }

    func testCreateGeneratesUniqueIds() {
        let a = AnomalySignal.create(vector: .unknown, confidence: 0.5,
                                     affectedModule: "m", description: "d")
        let b = AnomalySignal.create(vector: .unknown, confidence: 0.5,
                                     affectedModule: "m", description: "d")
        XCTAssertNotEqual(a.id, b.id)
    }

    func testCreateDefaultsEvidenceToEmpty() {
        let s = AnomalySignal.create(
            vector: .stateCorruption,
            confidence: 0.5,
            affectedModule: "Circle.AI.Memory",
            description: "no evidence supplied"
        )
        XCTAssertTrue(s.evidence.isEmpty)
    }

    func testCreatePreservesProvidedEvidence() {
        let evidence: [String: String] = [
            "sha256":   "abc123",
            "callSite": "AffectState.applyPositiveSignal",
            "pid":      "4242",
        ]
        let s = AnomalySignal.create(
            vector: .stateCorruption,
            confidence: 0.9,
            affectedModule: "Circle.AI.Memory",
            description: "AffectState mutated outside trusted pipeline",
            evidence: evidence
        )
        XCTAssertEqual(s.evidence, evidence)
    }

    func testCreateUsesCurrentTimeForDetectedAt() {
        let before = Date()
        let s = AnomalySignal.create(
            vector: .networkPivot,
            confidence: 0.6,
            affectedModule: "Circle.AI.Mesh",
            description: "lateral move probe"
        )
        let after = Date()
        XCTAssertGreaterThanOrEqual(s.detectedAt.timeIntervalSinceReferenceDate,
                                    before.timeIntervalSinceReferenceDate)
        XCTAssertLessThanOrEqual(s.detectedAt.timeIntervalSinceReferenceDate,
                                 after.timeIntervalSinceReferenceDate)
    }
}
