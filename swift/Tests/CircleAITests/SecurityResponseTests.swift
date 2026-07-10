// SecurityResponseTests.swift
//
// Validates the SecurityResponse factory helpers ported from SecurityResponse.cs
// (noAction / forKeyRotation / forRollback / composite) and their field wiring.

import XCTest
import Foundation
@testable import CircleAI

final class SecurityResponseTests: XCTestCase {

    private let signalId = UUID()

    func testNoActionResponse() {
        let r = SecurityResponse.noAction(signalId: signalId, reason: "monitoring only")
        XCTAssertEqual(r.signalId, signalId)
        XCTAssertEqual(r.kind, .noAction)
        XCTAssertTrue(r.appliedActions.isEmpty)
        XCTAssertEqual(r.description, "monitoring only")
        XCTAssertNil(r.restoredCheckpoint)
    }

    func testForKeyRotationResponse() {
        let r = SecurityResponse.forKeyRotation(signalId: signalId, description: "rotate now")
        XCTAssertEqual(r.kind, .keyRotation)
        XCTAssertTrue(r.appliedActions.isEmpty)
        XCTAssertNil(r.restoredCheckpoint)
    }

    func testForRollbackRecordsCheckpoint() {
        let cp = SecurityCheckpoint.create(uhidIdentityId: "u", moduleLabel: "CircleAI.Memory",
                                           payload: Data([9]))
        let r = SecurityResponse.forRollback(signalId: signalId, restored: cp)
        XCTAssertEqual(r.kind, .stateRollback)
        XCTAssertEqual(r.restoredCheckpoint?.id, cp.id)
        XCTAssertTrue(r.description.contains(cp.id.uuidString.lowercased()))
        XCTAssertTrue(r.description.contains("CircleAI.Memory"))
    }

    func testCompositeResponseCarriesActions() {
        let actions: [SecurityResponseKind] = [.keyRotation, .meshIsolationSignal, .stateRollback]
        let cp = SecurityCheckpoint.create(uhidIdentityId: "u", moduleLabel: "m", payload: Data([1]))
        let r = SecurityResponse.composite(signalId: signalId, actions: actions,
                                           description: "composite", restoredCheckpoint: cp)
        XCTAssertEqual(r.kind, .composite)
        XCTAssertEqual(r.appliedActions, actions)
        XCTAssertEqual(r.restoredCheckpoint?.id, cp.id)
    }

    func testCompositeWithoutCheckpointHasNilRestore() {
        let r = SecurityResponse.composite(signalId: signalId,
                                           actions: [.keyRotation, .meshIsolationSignal],
                                           description: "no rollback")
        XCTAssertEqual(r.kind, .composite)
        XCTAssertNil(r.restoredCheckpoint)
    }
}
