// SecurityCheckpointTests.swift
//
// Validates SecurityCheckpoint ported from SecurityCheckpoint.cs — SHA-256
// binding of payload, tamper detection via verify(), identity binding, and the
// redaction-safe debug description (payload bytes never in clear).

import XCTest
import Foundation
@testable import CircleAI

final class SecurityCheckpointTests: XCTestCase {

    func testCreateComputesHashAndBindsFields() {
        let payload = Data("trusted-state".utf8)
        let cp = SecurityCheckpoint.create(
            uhidIdentityId: "uhid-1", moduleLabel: "CircleAI.Memory", payload: payload)
        XCTAssertEqual(cp.uhidIdentityId, "uhid-1")
        XCTAssertEqual(cp.moduleLabel, "CircleAI.Memory")
        XCTAssertEqual(cp.payload, payload)
        XCTAssertEqual(cp.payloadHash.count, 32) // SHA-256
        XCTAssertNotEqual(cp.id, UUID(uuidString: "00000000-0000-0000-0000-000000000000"))
    }

    func testVerifyPassesForUntamperedPayload() {
        let cp = SecurityCheckpoint.create(
            uhidIdentityId: "u", moduleLabel: "m", payload: Data([1, 2, 3, 4]))
        XCTAssertTrue(cp.verify())
    }

    func testVerifyFailsWhenPayloadHashMismatched() {
        // Construct a checkpoint whose stored hash does not match the payload.
        let cp = SecurityCheckpoint(
            id: UUID(), uhidIdentityId: "u", moduleLabel: "m",
            payload: Data("real".utf8),
            payloadHash: Data(repeating: 0xAB, count: 32),
            createdAt: Date())
        XCTAssertFalse(cp.verify())
    }

    func testVerifyFailsWhenHashLengthDiffers() {
        let cp = SecurityCheckpoint(
            id: UUID(), uhidIdentityId: "u", moduleLabel: "m",
            payload: Data("real".utf8),
            payloadHash: Data([0x01]), // wrong length
            createdAt: Date())
        XCTAssertFalse(cp.verify())
    }

    func testTwoCheckpointsWithSamePayloadHaveEqualHash() {
        let p = Data("same".utf8)
        let a = SecurityCheckpoint.create(uhidIdentityId: "u", moduleLabel: "m", payload: p)
        let b = SecurityCheckpoint.create(uhidIdentityId: "u", moduleLabel: "m", payload: p)
        XCTAssertEqual(a.payloadHash, b.payloadHash)
        XCTAssertNotEqual(a.id, b.id) // fresh id per checkpoint
    }

    func testEmptyPayloadIsHashable() {
        let cp = SecurityCheckpoint.create(uhidIdentityId: "u", moduleLabel: "m", payload: Data())
        XCTAssertTrue(cp.verify())
        XCTAssertEqual(cp.payloadHash.count, 32) // SHA-256 of empty is well-defined
    }

    func testDebugDescriptionNeverLeaksPayload() {
        let secret = Data("SUPER-SECRET-STATE".utf8)
        let cp = SecurityCheckpoint.create(
            uhidIdentityId: "uhid-9", moduleLabel: "CircleAI.Companion", payload: secret)
        let s = cp.debugDescription
        XCTAssertFalse(s.contains("SUPER-SECRET-STATE"))
        XCTAssertTrue(s.contains("CircleAI.Companion"))
        XCTAssertTrue(s.contains("uhid-9"))
        XCTAssertTrue(s.contains("PayloadBytes=\(secret.count)"))
        XCTAssertTrue(s.contains("PayloadSha256="))
    }

    func testFixedTimeEqualsLengthMismatch() {
        XCTAssertFalse(SecurityCheckpoint.fixedTimeEquals(Data([1, 2]), Data([1, 2, 3])))
        XCTAssertTrue(SecurityCheckpoint.fixedTimeEquals(Data([1, 2, 3]), Data([1, 2, 3])))
        XCTAssertFalse(SecurityCheckpoint.fixedTimeEquals(Data([1, 2, 3]), Data([1, 2, 4])))
    }
}
