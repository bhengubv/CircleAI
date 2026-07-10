// AnomalyEventDispatcherTests.swift
//
// Validates DefaultAnomalyEventDispatcher ported from IAnomalyEventDispatcher.cs
// — threshold gate, id-dedup, cancellation, and pass-through to the wrapped
// watchdog. Uses a recording fake watchdog to assert dispatch reached invocation
// exactly when expected.

import XCTest
import Foundation
@testable import CircleAI

private final class RecordingWatchdog: ISecurityWatchdog, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var invocations: [UUID] = []

    func onAnomalyDetected(
        _ signal: AnomalySignal, checkpoint: SecurityCheckpoint?
    ) async throws -> SecurityResponse {
        lock.lock(); invocations.append(signal.id); lock.unlock()
        return SecurityResponse.forKeyRotation(signalId: signal.id, description: "recorded")
    }

    func streamSignals() -> AsyncStream<AnomalySignal> {
        AsyncStream { $0.finish() }
    }

    var invocationCount: Int { lock.lock(); defer { lock.unlock() }; return invocations.count }
}

final class AnomalyEventDispatcherTests: XCTestCase {

    private func signal(confidence: Float, vector: ThreatVector = .memoryAnomaly) -> AnomalySignal {
        AnomalySignal.create(vector: vector, confidence: confidence,
                             affectedModule: "m", description: "d")
    }

    func testDispatchesAboveThreshold() async throws {
        let wd = RecordingWatchdog()
        let dispatcher = DefaultAnomalyEventDispatcher(watchdog: wd, minimumConfidence: 0.30)
        let result = try await dispatcher.verifyAndDispatch(signal(confidence: 0.5))
        XCTAssertEqual(result.outcome, .dispatched)
        XCTAssertNotNil(result.response)
        XCTAssertEqual(wd.invocationCount, 1)
    }

    func testBelowThresholdIsNotDispatched() async throws {
        let wd = RecordingWatchdog()
        let dispatcher = DefaultAnomalyEventDispatcher(watchdog: wd, minimumConfidence: 0.30)
        let result = try await dispatcher.verifyAndDispatch(signal(confidence: 0.2))
        XCTAssertEqual(result.outcome, .belowThreshold)
        XCTAssertNil(result.response)
        XCTAssertEqual(wd.invocationCount, 0)
    }

    func testDuplicateSignalIdIsDeduped() async throws {
        let wd = RecordingWatchdog()
        let dispatcher = DefaultAnomalyEventDispatcher(watchdog: wd)
        let s = signal(confidence: 0.6)
        let first = try await dispatcher.verifyAndDispatch(s)
        let second = try await dispatcher.verifyAndDispatch(s)
        XCTAssertEqual(first.outcome, .dispatched)
        XCTAssertEqual(second.outcome, .duplicate)
        XCTAssertNil(second.response)
        XCTAssertEqual(wd.invocationCount, 1) // watchdog invoked only once
    }

    func testCancellationShortCircuits() async throws {
        let wd = RecordingWatchdog()
        let dispatcher = DefaultAnomalyEventDispatcher(watchdog: wd)
        let result = try await dispatcher.verifyAndDispatch(
            signal(confidence: 0.9), checkpoint: nil, isCancelled: true)
        XCTAssertEqual(result.outcome, .cancelled)
        XCTAssertEqual(wd.invocationCount, 0)
    }

    func testDistinctSignalsBothDispatch() async throws {
        let wd = RecordingWatchdog()
        let dispatcher = DefaultAnomalyEventDispatcher(watchdog: wd)
        _ = try await dispatcher.verifyAndDispatch(signal(confidence: 0.6))
        _ = try await dispatcher.verifyAndDispatch(signal(confidence: 0.6)) // different id via create()
        XCTAssertEqual(wd.invocationCount, 2)
    }

    func testMinimumConfidenceIsClamped() async throws {
        // A negative minimum clamps to 0 → everything at/above 0 dispatches.
        let wd = RecordingWatchdog()
        let dispatcher = DefaultAnomalyEventDispatcher(watchdog: wd, minimumConfidence: -1.0)
        let result = try await dispatcher.verifyAndDispatch(signal(confidence: 0.01))
        XCTAssertEqual(result.outcome, .dispatched)
    }

    func testCheckpointIsForwardedToWatchdog() async throws {
        // Wrap a watchdog that echoes whether it received a checkpoint.
        final class CheckpointProbe: ISecurityWatchdog, @unchecked Sendable {
            var sawCheckpoint = false
            func onAnomalyDetected(_ signal: AnomalySignal, checkpoint: SecurityCheckpoint?) async throws -> SecurityResponse {
                sawCheckpoint = checkpoint != nil
                return SecurityResponse.noAction(signalId: signal.id, reason: "probe")
            }
            func streamSignals() -> AsyncStream<AnomalySignal> { AsyncStream { $0.finish() } }
        }
        let probe = CheckpointProbe()
        let dispatcher = DefaultAnomalyEventDispatcher(watchdog: probe, minimumConfidence: 0.0)
        let cp = SecurityCheckpoint.create(uhidIdentityId: "u", moduleLabel: "m", payload: Data([1]))
        _ = try await dispatcher.verifyAndDispatch(signal(confidence: 0.5), checkpoint: cp)
        XCTAssertTrue(probe.sawCheckpoint)
    }
}
