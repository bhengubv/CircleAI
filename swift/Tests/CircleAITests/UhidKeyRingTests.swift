// UhidKeyRingTests.swift
//
// Validates UhidKeyRing ported from UhidKeyRing.cs — P-256 ECDSA sign/verify,
// revocation semantics (sign throws, verify still works), rotation (fresh ring,
// old stays revoked), and identity binding.

import XCTest
import Foundation
@testable import CircleAI

final class UhidKeyRingTests: XCTestCase {

    func testGenerateFreshBindsIdentityAndPublishesKey() {
        let ring = UhidKeyRing.generateFresh(uhidIdentityId: "uhid-1")
        XCTAssertEqual(ring.uhidIdentityId, "uhid-1")
        XCTAssertFalse(ring.isRevoked)
        XCTAssertNil(ring.revokedAt)
        XCTAssertFalse(ring.publicKeyDer.isEmpty)
    }

    func testSignThenVerifyRoundTrips() throws {
        let ring = UhidKeyRing.generateFresh(uhidIdentityId: "u")
        let data = Data("payload-to-sign".utf8)
        let sig = try ring.sign(data)
        XCTAssertTrue(ring.verify(data, signature: sig))
    }

    func testVerifyRejectsWrongData() throws {
        let ring = UhidKeyRing.generateFresh(uhidIdentityId: "u")
        let sig = try ring.sign(Data("original".utf8))
        XCTAssertFalse(ring.verify(Data("tampered".utf8), signature: sig))
    }

    func testVerifyRejectsGarbageSignature() {
        let ring = UhidKeyRing.generateFresh(uhidIdentityId: "u")
        XCTAssertFalse(ring.verify(Data("x".utf8), signature: Data([0x00, 0x01, 0x02])))
    }

    func testSignThrowsAfterRevoke() throws {
        let ring = UhidKeyRing.generateFresh(uhidIdentityId: "u")
        ring.revoke()
        XCTAssertTrue(ring.isRevoked)
        XCTAssertNotNil(ring.revokedAt)
        XCTAssertThrowsError(try ring.sign(Data("x".utf8))) { error in
            guard case UhidKeyRingError.revoked = error else {
                return XCTFail("expected .revoked, got \(error)")
            }
        }
    }

    func testVerifyStillWorksAfterRevoke() throws {
        let ring = UhidKeyRing.generateFresh(uhidIdentityId: "u")
        let data = Data("historical".utf8)
        let sig = try ring.sign(data)
        ring.revoke()
        // Historical signatures must remain verifiable post-revocation.
        XCTAssertTrue(ring.verify(data, signature: sig))
    }

    func testRevokeIsIdempotent() {
        let ring = UhidKeyRing.generateFresh(uhidIdentityId: "u")
        ring.revoke()
        let firstRevokedAt = ring.revokedAt
        ring.revoke()
        XCTAssertEqual(ring.revokedAt, firstRevokedAt) // unchanged on second revoke
    }

    func testRotateReturnsFreshRingAndRevokesOld() throws {
        let original = UhidKeyRing.generateFresh(uhidIdentityId: "uhid-7")
        let originalRingId = original.ringId
        let rotated = original.rotate()

        XCTAssertTrue(original.isRevoked)
        XCTAssertFalse(rotated.isRevoked)
        XCTAssertEqual(rotated.uhidIdentityId, "uhid-7") // identity preserved
        XCTAssertNotEqual(rotated.ringId, originalRingId) // fresh ring id
        // Fresh ring can sign; old ring cannot.
        XCTAssertNoThrow(try rotated.sign(Data("new".utf8)))
        XCTAssertThrowsError(try original.sign(Data("old".utf8)))
    }

    func testRotatedRingHasDifferentKeyMaterial() throws {
        let a = UhidKeyRing.generateFresh(uhidIdentityId: "u")
        let b = a.rotate()
        // A signature from the new ring must NOT verify against… itself with old
        // data mismatch is already covered; here assert the public keys differ.
        XCTAssertNotEqual(a.publicKeyDer, b.publicKeyDer)
    }

    func testSignThrowsAfterDispose() {
        let ring = UhidKeyRing.generateFresh(uhidIdentityId: "u")
        ring.dispose()
        XCTAssertThrowsError(try ring.sign(Data("x".utf8))) { error in
            guard case UhidKeyRingError.disposed = error else {
                return XCTFail("expected .disposed, got \(error)")
            }
        }
    }

    func testCrossRingSignatureDoesNotVerify() throws {
        let a = UhidKeyRing.generateFresh(uhidIdentityId: "u")
        let b = UhidKeyRing.generateFresh(uhidIdentityId: "u")
        let data = Data("shared".utf8)
        let sigFromA = try a.sign(data)
        XCTAssertFalse(b.verify(data, signature: sigFromA)) // different key pair
        XCTAssertTrue(a.verify(data, signature: sigFromA))
    }
}
