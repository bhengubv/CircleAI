// DistributionBoardTests.swift
//
// Exercises the Distribution port (the DISTRIBUTION-section named types):
// the in-memory app-store submitter (records + accepts), the signed-delta
// updater (records, signature-gated), and the default OEM / carrier preload
// catalogues. Mirrors CircleAI.Distribution.Ubiquity (DISTRIBUTION section).

import XCTest
import Foundation
@testable import CircleAI

final class DistributionBoardTests: XCTestCase {

    // ── DTO ─────────────────────────────────────────────────────────────────

    func testAppStorePackageCodableRoundTrip() throws {
        let p = AppStorePackage(storeName: "Play", packagePath: "/x.aab", version: "1.0",
                                metadata: ["track": "beta"])
        XCTAssertEqual(try JSONDecoder().decode(AppStorePackage.self, from: try JSONEncoder().encode(p)), p)
    }

    // ── App-store submitter ────────────────────────────────────────────────────

    func testAppStoreSubmitterRecordsAndAccepts() async {
        let sub = InMemoryAppStoreSubmitter()
        let pkg = AppStorePackage(storeName: "Play", packagePath: "/app.aab", version: "2.0", metadata: [:])
        let ok = await sub.submit(pkg)
        XCTAssertTrue(ok)
        XCTAssertEqual(sub.allSubmissions, [pkg])
    }

    // ── Signed delta updater ────────────────────────────────────────────────────

    func testSignedDeltaUpdaterAcceptsSignedRejectsUnsigned() async {
        let updater = InMemorySignedDeltaUpdater()  // default: accept non-empty signature
        let signed = DeltaUpdate(channel: "stable", fromVersion: "1", toVersion: "2",
                                 payload: Data([1]), signature: Data([9]))
        let appliedSigned = await updater.apply(signed)
        XCTAssertTrue(appliedSigned)
        let unsigned = DeltaUpdate(channel: "stable", fromVersion: "2", toVersion: "3",
                                   payload: Data([1]), signature: Data())
        let appliedUnsigned = await updater.apply(unsigned)
        XCTAssertFalse(appliedUnsigned)
        // Only the signed one was recorded.
        XCTAssertEqual(updater.allApplied, [signed])
    }

    func testSignedDeltaUpdaterCustomValidator() async {
        // Reject everything.
        let updater = InMemorySignedDeltaUpdater { _ in false }
        let u = DeltaUpdate(channel: "c", fromVersion: "1", toVersion: "2", payload: Data([1]), signature: Data([9]))
        let applied = await updater.apply(u)
        XCTAssertFalse(applied)
        XCTAssertTrue(updater.allApplied.isEmpty)
    }

    // ── Preload catalogues ──────────────────────────────────────────────────────

    func testDefaultOemCatalogue() {
        let c = DefaultOemPreloadCatalog()
        XCTAssertEqual(c.partners, ["Tecno", "Itel", "Samsung mid-tier", "Xiaomi", "Huawei"])
    }

    func testDefaultCarrierCatalogue() {
        let c = DefaultCarrierPreloadCatalog()
        XCTAssertEqual(c.carriers, ["MTN", "Vodacom", "Cell C", "Telkom", "Safaricom", "Airtel"])
    }
}
