// ModelAlignmentTests.swift
//
// Exercises the model-alignment surface in CircleAI.ModelAlignment: DTO Codable
// round-trips, the reversible-only apply policy of InMemoryAlignmentToolkit,
// revert semantics, the RefuseAlignedPublishAuditor gate, and the fail-closed
// Null* defaults. Mirrors the C# reference.

import XCTest
import Foundation
@testable import CircleAI

final class ModelAlignmentTests: XCTestCase {

    private func reversibleProfile(_ id: String = "p1") -> AlignmentProfile {
        AlignmentProfile(
            profileId: id,
            description: "remove self-harm refusals",
            refusalCategoriesRemoved: ["self-harm"],
            createdAtUtc: Date(timeIntervalSince1970: 1_700_000_000),
            isReversible: true)
    }

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testAlignmentProfileCodableRoundTrip() throws {
        let p = reversibleProfile()
        let data = try JSONEncoder().encode(p)
        let back = try JSONDecoder().decode(AlignmentProfile.self, from: data)
        XCTAssertEqual(back, p)
    }

    func testAlignmentResultCodableRoundTrip() throws {
        let r = AlignmentResult(profileId: "p1", success: false, failureReason: "nope")
        let data = try JSONEncoder().encode(r)
        let back = try JSONDecoder().decode(AlignmentResult.self, from: data)
        XCTAssertEqual(back, r)
        // nil failureReason also round-trips.
        let ok = AlignmentResult(profileId: "p1", success: true, failureReason: nil)
        let okBack = try JSONDecoder().decode(AlignmentResult.self, from: try JSONEncoder().encode(ok))
        XCTAssertEqual(okBack, ok)
    }

    // ── InMemoryAlignmentToolkit.apply ───────────────────────────────────────

    func testApplyReversibleSucceeds() async throws {
        let kit = InMemoryAlignmentToolkit()
        let result = try await kit.apply(modelId: "m1", profile: reversibleProfile())
        XCTAssertTrue(result.success)
        XCTAssertNil(result.failureReason)
        XCTAssertEqual(result.profileId, "p1")

        let applied = try await kit.listApplied(modelId: "m1")
        XCTAssertEqual(applied.count, 1)
        XCTAssertEqual(applied.first?.profileId, "p1")
    }

    func testApplyNonReversibleIsRefused() async throws {
        let kit = InMemoryAlignmentToolkit()
        let nonrev = AlignmentProfile(
            profileId: "perm", description: "permanent", refusalCategoriesRemoved: [],
            createdAtUtc: Date(), isReversible: false)
        let result = try await kit.apply(modelId: "m1", profile: nonrev)
        XCTAssertFalse(result.success)
        XCTAssertEqual(result.failureReason, "Non-reversible alignment refused by InMemoryAlignmentToolkit")
        // Nothing was recorded.
        let applied = try await kit.listApplied(modelId: "m1")
        XCTAssertTrue(applied.isEmpty)
    }

    func testApplyBlankModelIdThrows() async {
        let kit = InMemoryAlignmentToolkit()
        do {
            _ = try await kit.apply(modelId: "   ", profile: reversibleProfile())
            XCTFail("expected ModelAlignmentError.argument")
        } catch let e as ModelAlignmentError {
            XCTAssertEqual(e, .argument("modelId required"))
        } catch {
            XCTFail("unexpected error: \(error)")
        }
    }

    // ── InMemoryAlignmentToolkit.revert ──────────────────────────────────────

    func testRevertAppliedProfileSucceeds() async throws {
        let kit = InMemoryAlignmentToolkit()
        _ = try await kit.apply(modelId: "m1", profile: reversibleProfile("p1"))
        let result = try await kit.revert(modelId: "m1", profileId: "p1")
        XCTAssertTrue(result.success)
        let applied = try await kit.listApplied(modelId: "m1")
        XCTAssertTrue(applied.isEmpty)
    }

    func testRevertUnknownModel() async throws {
        let kit = InMemoryAlignmentToolkit()
        let result = try await kit.revert(modelId: "ghost", profileId: "p1")
        XCTAssertFalse(result.success)
        XCTAssertEqual(result.failureReason, "Unknown model")
    }

    func testRevertProfileNotApplied() async throws {
        let kit = InMemoryAlignmentToolkit()
        _ = try await kit.apply(modelId: "m1", profile: reversibleProfile("p1"))
        let result = try await kit.revert(modelId: "m1", profileId: "other")
        XCTAssertFalse(result.success)
        XCTAssertEqual(result.failureReason, "Profile not applied to this model")
    }

    func testRevertBlankProfileIdThrows() async throws {
        let kit = InMemoryAlignmentToolkit()
        do {
            _ = try await kit.revert(modelId: "m1", profileId: " ")
            XCTFail("expected ModelAlignmentError.argument")
        } catch let e as ModelAlignmentError {
            XCTAssertEqual(e, .argument("profileId required"))
        }
    }

    func testListAppliedUnknownModelIsEmpty() async throws {
        let kit = InMemoryAlignmentToolkit()
        let applied = try await kit.listApplied(modelId: "ghost")
        XCTAssertTrue(applied.isEmpty)
    }

    func testBackendId() {
        XCTAssertEqual(InMemoryAlignmentToolkit().backendId, "in-memory")
    }

    // ── RefuseAlignedPublishAuditor ──────────────────────────────────────────

    func testAuditorAllowsPublishWhenClean() async throws {
        let kit = InMemoryAlignmentToolkit()
        let auditor = RefuseAlignedPublishAuditor(toolkit: kit)
        // No profiles applied → must not throw.
        try await auditor.assertOkToPublish(modelId: "m1")
        XCTAssertEqual(auditor.backendId, "refuse-aligned")
    }

    func testAuditorRefusesPublishWhenAligned() async throws {
        let kit = InMemoryAlignmentToolkit()
        _ = try await kit.apply(modelId: "m1", profile: reversibleProfile("p1"))
        let auditor = RefuseAlignedPublishAuditor(toolkit: kit)
        do {
            try await auditor.assertOkToPublish(modelId: "m1")
            XCTFail("expected ModelAlignmentError.invalidOperation")
        } catch let e as ModelAlignmentError {
            guard case .invalidOperation(let msg) = e else {
                return XCTFail("wrong case: \(e)")
            }
            XCTAssertTrue(msg.contains("Cannot publish 'm1'"))
            XCTAssertTrue(msg.contains("1 alignment profile(s) applied"))
        }
    }

    func testAuditorBlankModelIdThrows() async throws {
        let auditor = RefuseAlignedPublishAuditor(toolkit: InMemoryAlignmentToolkit())
        do {
            try await auditor.assertOkToPublish(modelId: "")
            XCTFail("expected ModelAlignmentError.argument")
        } catch let e as ModelAlignmentError {
            XCTAssertEqual(e, .argument("modelId required"))
        }
    }

    // ── Null implementations (fail-closed) ───────────────────────────────────

    func testNullToolkitRefusesApplyAndRevert() async throws {
        let kit = NullAlignmentToolkit.instance
        let apply = try await kit.apply(modelId: "m", profile: reversibleProfile())
        XCTAssertFalse(apply.success)
        XCTAssertEqual(apply.failureReason, "NullAlignmentToolkit: no real backend wired.")
        let revert = try await kit.revert(modelId: "m", profileId: "p1")
        XCTAssertFalse(revert.success)
        XCTAssertEqual(revert.failureReason, "NullAlignmentToolkit: nothing to revert.")
        let applied = try await kit.listApplied(modelId: "m")
        XCTAssertTrue(applied.isEmpty)
        XCTAssertEqual(kit.backendId, "null")
    }

    func testNullAuditorAlwaysAllows() async throws {
        // Never throws, regardless of model.
        try await NullAlignmentAuditor.instance.assertOkToPublish(modelId: "anything")
        XCTAssertEqual(NullAlignmentAuditor.instance.backendId, "null")
    }
}
