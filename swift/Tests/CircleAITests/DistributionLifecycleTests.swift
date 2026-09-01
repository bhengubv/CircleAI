import XCTest
@testable import CircleAI

/// Losing a phone, losing a person, and going quiet.
final class DistributionLifecycleTests: XCTestCase {

    func testARemoteWipeIsRecordedPerDevice() throws {
        let f = DefaultLostDeviceFlow()
        XCTAssertFalse(f.isWiped(deviceId: "d1"))
        try f.remoteWipe(deviceId: "d1")
        XCTAssertTrue(f.isWiped(deviceId: "d1"))
        XCTAssertFalse(f.isWiped(deviceId: "d2"))
    }

    // A self-designation is an inheritance that never triggers.
    func testAPersonCannotInheritFromThemselves() {
        let p = DefaultInheritanceProtocol()
        XCTAssertThrowsError(try p.designate(ownerId: "a", designeeId: "a")) { e in
            XCTAssertEqual(e as? DistributionError, .designeeEqualsOwner)
        }
        XCTAssertNil(p.designee(for: "a"))
    }

    func testADesignationIsRecorded() throws {
        let p = DefaultInheritanceProtocol()
        try p.designate(ownerId: "a", designeeId: "b")
        XCTAssertEqual(p.designee(for: "a"), "b")
    }

    func testRecoveryIsPerOwnerAndCanBeCompleted() throws {
        let r = DefaultAccountCompromiseRecovery()
        try r.begin(ownerId: "a")
        XCTAssertTrue(r.inRecovery(ownerId: "a"))
        XCTAssertFalse(r.inRecovery(ownerId: "b"))
        r.complete(ownerId: "a")
        XCTAssertFalse(r.inRecovery(ownerId: "a"))
    }

    // A schema-stamped envelope, so an importer knows what it is reading.
    func testTheExportCarriesItsSchemaAndOwner() throws {
        let d = try DefaultDataPortabilityExport().export(ownerId: "owner-1")
        let obj = try JSONSerialization.jsonObject(with: d) as! [String: String]
        XCTAssertEqual(obj["owner_id"], "owner-1")
        XCTAssertEqual(obj["schema"], "circleai/portability/v1")
        XCTAssertNotNil(obj["exported_at"])
    }

    func testAnExportNeedsAnOwner() {
        XCTAssertThrowsError(try DefaultDataPortabilityExport().export(ownerId: " "))
    }

    func testAQuietWindowCoversItsDuration() throws {
        let q = DefaultQuietMode()
        try q.engage(reason: "funeral", duration: 3600)
        XCTAssertTrue(q.isQuiet(at: Date()))
        XCTAssertTrue(q.isQuiet(at: Date().addingTimeInterval(1800)))
        XCTAssertFalse(q.isQuiet(at: Date().addingTimeInterval(7200)))
        XCTAssertEqual(q.activeWindows.count, 1)
    }

    // A zero window would be silence that never happens.
    func testAQuietWindowNeedsARealDuration() {
        let q = DefaultQuietMode()
        XCTAssertThrowsError(try q.engage(reason: "x", duration: 0)) { e in
            XCTAssertEqual(e as? DistributionError, .nonPositiveDuration)
        }
        XCTAssertThrowsError(try q.engage(reason: " ", duration: 60))
        XCTAssertTrue(q.activeWindows.isEmpty)
    }

    func testImpairedModeEngagesAndDisengagesPerOwner() throws {
        let m = DefaultImpairedUserMode()
        try m.engage(ownerId: "a")
        XCTAssertTrue(m.isEngaged(ownerId: "a"))
        XCTAssertFalse(m.isEngaged(ownerId: "b"))
        m.disengage(ownerId: "a")
        XCTAssertFalse(m.isEngaged(ownerId: "a"))
    }

    // A relative path is not evidence anybody outside the app can follow.
    func testOnlyAnAbsoluteHttpUrlCountsAsEvidence() throws {
        let t = DefaultPublicTransparency()
        try t.linkEvidence(claim: "we are audited", evidenceUrl: "https://trust.circle.ai/audit")
        XCTAssertEqual(t.linked.count, 1)

        for bad in ["/audit", "ftp://x/y", "not a url", ""] {
            XCTAssertThrowsError(try t.linkEvidence(claim: "c", evidenceUrl: bad), bad)
        }
        XCTAssertEqual(t.linked.count, 1)
    }

    func testAClaimIsRequired() {
        XCTAssertThrowsError(try DefaultPublicTransparency()
            .linkEvidence(claim: "  ", evidenceUrl: "https://x.com/y"))
    }
}
