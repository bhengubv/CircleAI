// SecurityWatchdogTests.swift
//
// Validates DefaultSecurityWatchdog ported from ISecurityWatchdog.cs — the
// graduated response policy (noAction / keyRotation / composite + rollback) and
// the buffered signal broadcast (including pre-subscription buffering, a Wave-1
// hazard).

import XCTest
import Foundation
@testable import CircleAI

final class SecurityWatchdogTests: XCTestCase {

    private func signal(
        vector: ThreatVector, confidence: Float, module: String = "CircleAI.Companion"
    ) -> AnomalySignal {
        AnomalySignal.create(vector: vector, confidence: confidence,
                             affectedModule: module, description: "test-\(vector)")
    }

    // ── graduated response policy ────────────────────────────────────────────

    func testLowConfidenceIsNoAction() async throws {
        let wd = DefaultSecurityWatchdog()
        let r = try await wd.onAnomalyDetected(signal(vector: .memoryAnomaly, confidence: 0.2))
        XCTAssertEqual(r.kind, .noAction)
        XCTAssertTrue(r.appliedActions.isEmpty)
        XCTAssertNil(r.restoredCheckpoint)
    }

    func testMidConfidenceIsKeyRotation() async throws {
        let wd = DefaultSecurityWatchdog()
        let r = try await wd.onAnomalyDetected(signal(vector: .memoryAnomaly, confidence: 0.45))
        XCTAssertEqual(r.kind, .keyRotation)
        XCTAssertTrue(r.appliedActions.isEmpty)
    }

    func testHighConfidenceIsCompositeWithRotationAndMesh() async throws {
        let wd = DefaultSecurityWatchdog()
        // memoryAnomaly is NOT a high-severity vector, so no rollback even with a
        // checkpoint — composite is rotation + mesh only.
        let cp = SecurityCheckpoint.create(uhidIdentityId: "u", moduleLabel: "m", payload: Data([1]))
        let r = try await wd.onAnomalyDetected(signal(vector: .memoryAnomaly, confidence: 0.7),
                                               checkpoint: cp)
        XCTAssertEqual(r.kind, .composite)
        XCTAssertEqual(r.appliedActions, [.keyRotation, .meshIsolationSignal])
        XCTAssertNil(r.restoredCheckpoint)
    }

    func testHighConfidenceHighSeverityWithValidCheckpointAddsRollback() async throws {
        let wd = DefaultSecurityWatchdog()
        let cp = SecurityCheckpoint.create(uhidIdentityId: "u", moduleLabel: "CircleAI.Memory",
                                           payload: Data("state".utf8))
        let r = try await wd.onAnomalyDetected(signal(vector: .stateCorruption, confidence: 0.9),
                                               checkpoint: cp)
        XCTAssertEqual(r.kind, .composite)
        XCTAssertEqual(r.appliedActions, [.keyRotation, .meshIsolationSignal, .stateRollback])
        XCTAssertEqual(r.restoredCheckpoint?.id, cp.id)
    }

    func testHighSeverityWithoutCheckpointHasNoRollback() async throws {
        let wd = DefaultSecurityWatchdog()
        let r = try await wd.onAnomalyDetected(signal(vector: .networkPivot, confidence: 0.85),
                                               checkpoint: nil)
        XCTAssertEqual(r.appliedActions, [.keyRotation, .meshIsolationSignal])
        XCTAssertNil(r.restoredCheckpoint)
    }

    func testHighSeverityWithTamperedCheckpointSkipsRollback() async throws {
        let wd = DefaultSecurityWatchdog()
        // Checkpoint whose hash does not match payload → verify() fails → no rollback.
        let bad = SecurityCheckpoint(
            id: UUID(), uhidIdentityId: "u", moduleLabel: "m",
            payload: Data("real".utf8), payloadHash: Data(repeating: 0, count: 32),
            createdAt: Date())
        let r = try await wd.onAnomalyDetected(signal(vector: .privilegeEscalation, confidence: 0.95),
                                               checkpoint: bad)
        XCTAssertEqual(r.appliedActions, [.keyRotation, .meshIsolationSignal])
        XCTAssertNil(r.restoredCheckpoint)
    }

    func testEachHighSeverityVectorGetsRollbackWithValidCheckpoint() async throws {
        let wd = DefaultSecurityWatchdog()
        let cp = SecurityCheckpoint.create(uhidIdentityId: "u", moduleLabel: "m", payload: Data([7]))
        for v in [ThreatVector.controlFlowDrift, .privilegeEscalation, .networkPivot, .stateCorruption] {
            let r = try await wd.onAnomalyDetected(signal(vector: v, confidence: 0.8), checkpoint: cp)
            XCTAssertTrue(r.appliedActions.contains(.stateRollback), "vector \(v) should roll back")
        }
    }

    func testResponseCarriesSignalId() async throws {
        let wd = DefaultSecurityWatchdog()
        let s = signal(vector: .agentPatchRejected, confidence: 0.45)
        let r = try await wd.onAnomalyDetected(s)
        XCTAssertEqual(r.signalId, s.id)
    }

    // ── signal broadcast ─────────────────────────────────────────────────────

    func testStreamDeliversDetectedSignals() async throws {
        let wd = DefaultSecurityWatchdog()
        // Subscribe SYNCHRONOUSLY before emitting.
        var iterator = wd.streamSignals().makeAsyncIterator()
        let s = signal(vector: .memoryAnomaly, confidence: 0.2)
        _ = try await wd.onAnomalyDetected(s)
        let received = await iterator.next()
        XCTAssertEqual(received?.id, s.id)
        XCTAssertEqual(received?.vector, .memoryAnomaly)
    }

    func testSignalsEmittedBeforeSubscriptionAreBuffered() async throws {
        let wd = DefaultSecurityWatchdog()
        // Emit BEFORE any subscriber attaches; the unbounded buffer must retain it.
        let s = signal(vector: .networkPivot, confidence: 0.2)
        _ = try await wd.onAnomalyDetected(s)

        var iterator = wd.streamSignals().makeAsyncIterator()
        let received = await iterator.next()
        XCTAssertEqual(received?.id, s.id)
    }

    func testComponentName() {
        XCTAssertEqual(DefaultSecurityWatchdog().componentName, "DefaultSecurityWatchdog")
    }

    // ── message formatting ───────────────────────────────────────────────────

    func testKeyRotationMessageMentionsVectorAndModule() async throws {
        let wd = DefaultSecurityWatchdog()
        let r = try await wd.onAnomalyDetected(
            signal(vector: .controlFlowDrift, confidence: 0.45, module: "CircleAI.Identity"))
        XCTAssertTrue(r.description.contains("ControlFlowDrift"))
        XCTAssertTrue(r.description.contains("CircleAI.Identity"))
    }
}
