// CrmBoardTests.swift
//
// Exercises the CRM records' Codable round-trips and the deterministic
// behaviour of the in-memory + null backends — contact upsert/get/search
// (case-insensitive substring on name/email, name-ordered, topK), deal
// pipeline (stage-filtered, value-descending), and the per-contact activity
// log (newest-first, limit), plus the argument guards. Mirrors CircleAI.CRM/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class CrmBoardTests: XCTestCase {

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testContactCodableRoundTrip() throws {
        let c = Contact(contactId: "c1", fullName: "Ada Lovelace", email: "ada@x.io", phone: nil, companyId: "co1")
        XCTAssertEqual(try JSONDecoder().decode(Contact.self, from: try JSONEncoder().encode(c)), c)
    }

    func testDealCodableRoundTrip() throws {
        let d = Deal(dealId: "d1", companyId: "co1", name: "Big", value: 1234.5, currency: "ZAR", stage: "Won")
        XCTAssertEqual(try JSONDecoder().decode(Deal.self, from: try JSONEncoder().encode(d)), d)
    }

    func testActivityCodableRoundTrip() throws {
        let a = Activity(activityId: "a1", contactId: "c1", kind: "call", body: "hi", atUtc: Date(timeIntervalSince1970: 7))
        XCTAssertEqual(try JSONDecoder().decode(Activity.self, from: try JSONEncoder().encode(a)), a)
    }

    // ── Contact store ────────────────────────────────────────────────────────

    func testContactUpsertGetAndBackendId() async throws {
        let store = InMemoryContactStore()
        XCTAssertEqual(store.backendId, "in-memory")
        try await store.upsert(Contact(contactId: "c1", fullName: "Ada", email: nil, phone: nil, companyId: nil))
        let got = try await store.get("c1")
        XCTAssertEqual(got?.fullName, "Ada")
        let miss = try await store.get("nope")
        XCTAssertNil(miss)
    }

    func testContactSearchByNameAndEmailCaseInsensitiveNameOrdered() async throws {
        let store = InMemoryContactStore()
        try await store.upsert(Contact(contactId: "1", fullName: "Charlie Brown", email: "cb@x.io", phone: nil, companyId: nil))
        try await store.upsert(Contact(contactId: "2", fullName: "alice smith", email: "alice@ACME.io", phone: nil, companyId: nil))
        try await store.upsert(Contact(contactId: "3", fullName: "Bob Jones", email: nil, phone: nil, companyId: nil))
        // Match by email substring (case-insensitive) picks alice; name match picks nobody else for "acme".
        let byEmail = try await store.search("acme")
        XCTAssertEqual(byEmail.map { $0.contactId }, ["2"])
        // Broad query matching several, ordered ascending by full name (case-insensitive).
        let broad = try await store.search("o")   // Charlie brOwn, bOb jOnes
        XCTAssertEqual(broad.map { $0.fullName }, ["Bob Jones", "Charlie Brown"])
    }

    func testContactSearchHonoursTopK() async throws {
        let store = InMemoryContactStore()
        for i in 0..<5 { try await store.upsert(Contact(contactId: "\(i)", fullName: "Name\(i)", email: nil, phone: nil, companyId: nil)) }
        let hits = try await store.search("Name", topK: 2)
        XCTAssertEqual(hits.count, 2)
        XCTAssertEqual(hits.map { $0.fullName }, ["Name0", "Name1"])
    }

    func testContactSearchDefaultTopKOverload() async throws {
        let store = InMemoryContactStore()
        try await store.upsert(Contact(contactId: "1", fullName: "Zed", email: nil, phone: nil, companyId: nil))
        let hits = try await store.search("Zed")
        XCTAssertEqual(hits.count, 1)
    }

    func testContactUpsertBlankIdThrows() async {
        let store = InMemoryContactStore()
        do {
            try await store.upsert(Contact(contactId: "  ", fullName: "X", email: nil, phone: nil, companyId: nil))
            XCTFail("expected throw")
        } catch { XCTAssertEqual(error as? CrmError, .contactIdRequired) }
    }

    func testContactSearchNonPositiveTopKThrows() async {
        let store = InMemoryContactStore()
        do { _ = try await store.search("x", topK: 0); XCTFail("expected throw") }
        catch { XCTAssertEqual(error as? CrmError, .topKOutOfRange) }
    }

    // ── Deal pipeline ────────────────────────────────────────────────────────

    func testDealListByStageIsCaseInsensitiveAndValueDescending() async throws {
        let p = InMemoryDealPipeline()
        try await p.upsert(Deal(dealId: "d1", companyId: "c", name: "small", value: 10, currency: "ZAR", stage: "Open"))
        try await p.upsert(Deal(dealId: "d2", companyId: "c", name: "big", value: 100, currency: "ZAR", stage: "open"))
        try await p.upsert(Deal(dealId: "d3", companyId: "c", name: "won", value: 50, currency: "ZAR", stage: "Won"))
        let open = try await p.listByStage("OPEN")
        XCTAssertEqual(open.map { $0.dealId }, ["d2", "d1"])
        let d3 = await p.get("d3")
        XCTAssertEqual(d3?.stage, "Won")
    }

    func testDealUpsertBlankIdThrowsAndStageBlankThrows() async {
        let p = InMemoryDealPipeline()
        do { try await p.upsert(Deal(dealId: "", companyId: "c", name: "n", value: 1, currency: "ZAR", stage: "Open")); XCTFail() }
        catch { XCTAssertEqual(error as? CrmError, .dealIdRequired) }
        do { _ = try await p.listByStage("  "); XCTFail() }
        catch { XCTAssertEqual(error as? CrmError, .stageRequired) }
    }

    // ── Activity log ─────────────────────────────────────────────────────────

    func testActivityLogNewestFirstWithLimit() async throws {
        let log = InMemoryActivityLog()
        for i in 0..<4 {
            try await log.append(Activity(activityId: "a\(i)", contactId: "c1", kind: "note", body: "b",
                                          atUtc: Date(timeIntervalSince1970: TimeInterval(i))))
        }
        // Different contact should not leak in.
        try await log.append(Activity(activityId: "x", contactId: "c2", kind: "note", body: "b", atUtc: Date()))
        let recent = try await log.readForContact("c1", limit: 2)
        XCTAssertEqual(recent.map { $0.activityId }, ["a3", "a2"])
        let none = try await log.readForContact("ghost")
        XCTAssertTrue(none.isEmpty)
    }

    func testActivityLogAppendBlankContactThrows() async {
        let log = InMemoryActivityLog()
        do {
            try await log.append(Activity(activityId: "a", contactId: " ", kind: "n", body: "b", atUtc: Date()))
            XCTFail("expected throw")
        } catch { XCTAssertEqual(error as? CrmError, .contactIdRequired) }
    }

    // ── Null backends ────────────────────────────────────────────────────────

    func testNullBackendsFailClosed() async throws {
        XCTAssertEqual(NullContactStore.instance.backendId, "null")
        try await NullContactStore.instance.upsert(Contact(contactId: "c", fullName: "n", email: nil, phone: nil, companyId: nil))
        XCTAssertNil(try await NullContactStore.instance.get("c"))
        XCTAssertTrue(try await NullContactStore.instance.search("x").isEmpty)

        XCTAssertEqual(NullDealPipeline.instance.backendId, "null")
        try await NullDealPipeline.instance.upsert(Deal(dealId: "d", companyId: "c", name: "n", value: 1, currency: "ZAR", stage: "Open"))
        XCTAssertNil(await NullDealPipeline.instance.get("d"))
        XCTAssertTrue(try await NullDealPipeline.instance.listByStage("Open").isEmpty)

        XCTAssertEqual(NullActivityLog.instance.backendId, "null")
        try await NullActivityLog.instance.append(Activity(activityId: "a", contactId: "c", kind: "n", body: "b", atUtc: Date()))
        XCTAssertTrue(try await NullActivityLog.instance.readForContact("c").isEmpty)
    }
}
