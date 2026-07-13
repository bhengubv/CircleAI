// FederationTests.swift
//
// Exercises the Federation port: FederatedAveraging encode/decode + sample-
// weighted mean + validation throws, the InMemoryFederationAggregator round
// lifecycle (open validation, submit incl. unknown/closed/full/empty, commit
// gating on minParticipants + signature validator + idempotency), and the
// delta dispatcher (verify → dedup → submit outcomes). Mirrors CircleAI.Federation/*.

import XCTest
import Foundation
@testable import CircleAI

final class FederationTests: XCTestCase {

    private func delta(round: UUID, id: UUID = UUID(), payload: Data, samples: Int,
                       sig: Data = Data([1])) -> ModelDelta {
        ModelDelta(id: id, roundId: round, contributorUhid: "uhid", modelId: "m",
                   fromVersion: "1.0.0", deltaPayload: payload, sampleCount: samples,
                   signature: sig, submittedAt: Date())
    }

    // ── FederatedAveraging ─────────────────────────────────────────────────────

    func testEncodeDecodeRoundTrip() {
        let floats: [Float] = [1.5, -2.25, 0, 3.75]
        let decoded = FederatedAveraging.decodeFloats(FederatedAveraging.encodeFloats(floats))
        XCTAssertEqual(decoded, floats)
    }

    func testWeightedAverage() throws {
        // delta A: [10], 3 samples; delta B: [20], 1 sample.
        // weighted mean = (10*3 + 20*1) / 4 = 12.5
        let rid = UUID()
        let a = delta(round: rid, payload: FederatedAveraging.encodeFloats([10]), samples: 3)
        let b = delta(round: rid, payload: FederatedAveraging.encodeFloats([20]), samples: 1)
        let avg = try FederatedAveraging.average([a, b])
        XCTAssertEqual(FederatedAveraging.decodeFloats(avg), [12.5])
    }

    func testAverageValidationThrows() {
        let rid = UUID()
        XCTAssertThrowsError(try FederatedAveraging.average([]))
        // 3-byte payload (not multiple of 4).
        XCTAssertThrowsError(try FederatedAveraging.average([delta(round: rid, payload: Data([1, 2, 3]), samples: 1)]))
        // zero total samples.
        XCTAssertThrowsError(try FederatedAveraging.average([delta(round: rid, payload: FederatedAveraging.encodeFloats([1]), samples: 0)]))
        // mismatched lengths.
        let x = delta(round: rid, payload: FederatedAveraging.encodeFloats([1, 2]), samples: 1)
        let y = delta(round: rid, payload: FederatedAveraging.encodeFloats([1]), samples: 1)
        XCTAssertThrowsError(try FederatedAveraging.average([x, y]))
    }

    func testDecodeFloatsCheckedRejectsBadLength() {
        XCTAssertThrowsError(try FederatedAveraging.decodeFloatsChecked(Data([1, 2, 3])))
    }

    // ── Aggregator lifecycle ────────────────────────────────────────────────────

    func testOpenRoundValidation() async {
        let agg = InMemoryFederationAggregator { _ in true }
        await XCTAssertThrowsErrorAsync(try await agg.openRound(modelId: "", fromVersion: "1", toVersion: "2", minParticipants: 1, maxParticipants: 1))
        await XCTAssertThrowsErrorAsync(try await agg.openRound(modelId: "m", fromVersion: "1", toVersion: "2", minParticipants: 0, maxParticipants: 1))
        await XCTAssertThrowsErrorAsync(try await agg.openRound(modelId: "m", fromVersion: "1", toVersion: "2", minParticipants: 3, maxParticipants: 2))
    }

    func testSubmitAndCommitHappyPath() async throws {
        let agg = InMemoryFederationAggregator { _ in true }
        let round = try await agg.openRound(modelId: "m", fromVersion: "1", toVersion: "2",
                                            minParticipants: 2, maxParticipants: 5)
        // Below min → no commit yet.
        try await agg.submitDelta(delta(round: round.id, payload: FederatedAveraging.encodeFloats([4]), samples: 1))
        let belowMin = try await agg.tryCommit(round.id)
        XCTAssertNil(belowMin)

        try await agg.submitDelta(delta(round: round.id, payload: FederatedAveraging.encodeFloats([8]), samples: 1))
        let snapshot = try await agg.getRound(round.id)
        XCTAssertEqual(snapshot.currentParticipantCount, 2)

        let payload = try await agg.tryCommit(round.id)
        XCTAssertNotNil(payload)
        XCTAssertEqual(FederatedAveraging.decodeFloats(payload!), [6])  // (4+8)/2

        // Round is now committed; idempotent re-commit returns same payload.
        let again = try await agg.tryCommit(round.id)
        XCTAssertEqual(again, payload)
        let committedRound = try await agg.getRound(round.id)
        XCTAssertEqual(committedRound.status, .committed)
    }

    func testSubmitToUnknownRoundThrows() async {
        let agg = InMemoryFederationAggregator { _ in true }
        await XCTAssertThrowsErrorAsync(try await agg.submitDelta(delta(round: UUID(), payload: Data([1, 2, 3, 4]), samples: 1)))
    }

    func testSubmitToClosedRoundThrows() async throws {
        let agg = InMemoryFederationAggregator { _ in true }
        let round = try await agg.openRound(modelId: "m", fromVersion: "1", toVersion: "2",
                                            minParticipants: 1, maxParticipants: 5)
        try await agg.submitDelta(delta(round: round.id, payload: FederatedAveraging.encodeFloats([1]), samples: 1))
        _ = try await agg.tryCommit(round.id)  // commits
        await XCTAssertThrowsErrorAsync(try await agg.submitDelta(delta(round: round.id, payload: FederatedAveraging.encodeFloats([2]), samples: 1)))
    }

    func testSubmitBeyondMaxThrows() async throws {
        let agg = InMemoryFederationAggregator { _ in true }
        let round = try await agg.openRound(modelId: "m", fromVersion: "1", toVersion: "2",
                                            minParticipants: 1, maxParticipants: 1)
        try await agg.submitDelta(delta(round: round.id, payload: FederatedAveraging.encodeFloats([1]), samples: 1))
        await XCTAssertThrowsErrorAsync(try await agg.submitDelta(delta(round: round.id, payload: FederatedAveraging.encodeFloats([2]), samples: 1)))
    }

    func testEmptyPayloadNotCounted() async throws {
        let agg = InMemoryFederationAggregator { _ in true }
        let round = try await agg.openRound(modelId: "m", fromVersion: "1", toVersion: "2",
                                            minParticipants: 1, maxParticipants: 5)
        try await agg.submitDelta(delta(round: round.id, payload: Data(), samples: 1))  // empty — ignored
        let emptyRound = try await agg.getRound(round.id)
        XCTAssertEqual(emptyRound.currentParticipantCount, 0)
        let noCommit = try await agg.tryCommit(round.id)
        XCTAssertNil(noCommit)
    }

    func testCommitDropsInvalidSignatures() async throws {
        // Only deltas with a non-empty signature validate.
        let agg = InMemoryFederationAggregator { !$0.signature.isEmpty }
        let round = try await agg.openRound(modelId: "m", fromVersion: "1", toVersion: "2",
                                            minParticipants: 2, maxParticipants: 5)
        try await agg.submitDelta(delta(round: round.id, payload: FederatedAveraging.encodeFloats([4]), samples: 1, sig: Data()))  // invalid
        try await agg.submitDelta(delta(round: round.id, payload: FederatedAveraging.encodeFloats([8]), samples: 1, sig: Data([1])))  // valid
        // Only 1 valid delta < min 2 → no commit.
        let noCommit = try await agg.tryCommit(round.id)
        XCTAssertNil(noCommit)
    }

    func testRoundCount() async throws {
        let agg = InMemoryFederationAggregator { _ in true }
        _ = try await agg.openRound(modelId: "m", fromVersion: "1", toVersion: "2", minParticipants: 1, maxParticipants: 1)
        _ = try await agg.openRound(modelId: "m2", fromVersion: "1", toVersion: "2", minParticipants: 1, maxParticipants: 1)
        XCTAssertEqual(agg.roundCount, 2)
    }

    // ── Delta dispatcher ─────────────────────────────────────────────────────────

    func testDispatcherOutcomes() async throws {
        let agg = InMemoryFederationAggregator { _ in true }
        let dispatcher = InMemoryFederationDeltaDispatcher(aggregator: agg, signatureValidator: { !$0.signature.isEmpty })
        let round = try await agg.openRound(modelId: "m", fromVersion: "1", toVersion: "2",
                                            minParticipants: 1, maxParticipants: 5)

        // Invalid signature.
        let bad = delta(round: round.id, payload: FederatedAveraging.encodeFloats([1]), samples: 1, sig: Data())
        let badResult = await dispatcher.verifyAndSubmit(bad)
        XCTAssertEqual(badResult, .signatureInvalid)

        // Accepted.
        let d1 = delta(round: round.id, payload: FederatedAveraging.encodeFloats([1]), samples: 1)
        let acceptedResult = await dispatcher.verifyAndSubmit(d1)
        XCTAssertEqual(acceptedResult, .accepted)

        // Duplicate (same id).
        let duplicateResult = await dispatcher.verifyAndSubmit(d1)
        XCTAssertEqual(duplicateResult, .duplicate)

        // Unknown round.
        let unknown = delta(round: UUID(), payload: FederatedAveraging.encodeFloats([1]), samples: 1)
        let unknownResult = await dispatcher.verifyAndSubmit(unknown)
        XCTAssertEqual(unknownResult, .roundUnknown)
    }

    // Async throwing assertion helper.
    private func XCTAssertThrowsErrorAsync<T>(_ expr: @autoclosure () async throws -> T,
                                              file: StaticString = #filePath, line: UInt = #line) async {
        do { _ = try await expr(); XCTFail("expected throw", file: file, line: line) }
        catch { /* expected */ }
    }
}
