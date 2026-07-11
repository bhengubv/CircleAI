// LegalBoardTests.swift
//
// Exercises the legal records' Codable round-trips and the deterministic
// behaviour of InMemoryLegalBoard — matter open/close (incl. unknown-matter
// throw) + active ordering, contract expiry filtering + ordering, deadline
// filtering + ordering, and case-insensitive clause tagging (incl. blank-tag
// throw). Mirrors CircleAI.Legal/LegalPrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class LegalBoardTests: XCTestCase {

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testMatterCodableRoundTrip() throws {
        let m = Matter(matterId: "m1", title: "T", jurisdiction: "ZA", client: "C",
                       openedAtUtc: Date(timeIntervalSince1970: 1), open: true)
        XCTAssertEqual(try JSONDecoder().decode(Matter.self, from: try JSONEncoder().encode(m)), m)
    }

    func testContractCodableRoundTripWithAndWithoutExpiry() throws {
        let c1 = Contract(contractId: "c1", matterId: "m1", title: "T",
                          effectiveDate: Date(timeIntervalSince1970: 1),
                          expiryDate: Date(timeIntervalSince1970: 100), counterparties: ["A", "B"])
        XCTAssertEqual(try JSONDecoder().decode(Contract.self, from: try JSONEncoder().encode(c1)), c1)
        let c2 = Contract(contractId: "c2", matterId: "m1", title: "T",
                          effectiveDate: Date(timeIntervalSince1970: 1), expiryDate: nil, counterparties: [])
        XCTAssertEqual(try JSONDecoder().decode(Contract.self, from: try JSONEncoder().encode(c2)), c2)
    }

    func testDeadlineAndClauseCodableRoundTrip() throws {
        let d = LegalDeadline(deadlineId: "d1", matterId: "m1", description: "file", dueOn: Date(timeIntervalSince1970: 5))
        XCTAssertEqual(try JSONDecoder().decode(LegalDeadline.self, from: try JSONEncoder().encode(d)), d)
        let cl = Clause(clauseId: "cl1", title: "Indemnity", body: "…", tags: ["risk", "liability"])
        XCTAssertEqual(try JSONDecoder().decode(Clause.self, from: try JSONEncoder().encode(cl)), cl)
    }

    // ── Matters ──────────────────────────────────────────────────────────────

    func testActiveMattersExcludesClosedAndOrdersNewestFirst() throws {
        let b = InMemoryLegalBoard()
        b.open(Matter(matterId: "m1", title: "old", jurisdiction: "ZA", client: "C",
                      openedAtUtc: Date(timeIntervalSince1970: 100), open: true))
        b.open(Matter(matterId: "m2", title: "new", jurisdiction: "ZA", client: "C",
                      openedAtUtc: Date(timeIntervalSince1970: 300), open: true))
        b.open(Matter(matterId: "m3", title: "mid", jurisdiction: "ZA", client: "C",
                      openedAtUtc: Date(timeIntervalSince1970: 200), open: true))
        try b.close(matterId: "m3")
        XCTAssertEqual(b.activeMatters.map { $0.matterId }, ["m2", "m1"])
        XCTAssertEqual(b.getMatter("m3")?.open, false)
    }

    func testCloseThrowsForUnknownMatter() {
        let b = InMemoryLegalBoard()
        XCTAssertThrowsError(try b.close(matterId: "ghost")) { error in
            XCTAssertEqual(error as? LegalError, .unknownMatter("ghost"))
        }
    }

    // ── Contracts ────────────────────────────────────────────────────────────

    func testContractsExpiringBeforeFiltersAndOrders() {
        let b = InMemoryLegalBoard()
        b.addContract(Contract(contractId: "c1", matterId: "m", title: "a",
                               effectiveDate: Date(timeIntervalSince1970: 0),
                               expiryDate: Date(timeIntervalSince1970: 300), counterparties: []))
        b.addContract(Contract(contractId: "c2", matterId: "m", title: "b",
                               effectiveDate: Date(timeIntervalSince1970: 0),
                               expiryDate: Date(timeIntervalSince1970: 100), counterparties: []))
        b.addContract(Contract(contractId: "c3", matterId: "m", title: "no-expiry",
                               effectiveDate: Date(timeIntervalSince1970: 0),
                               expiryDate: nil, counterparties: []))
        // Cutoff at 250 → c2 (100) only; ordered ascending by expiry.
        let res = b.contractsExpiringBefore(Date(timeIntervalSince1970: 250))
        XCTAssertEqual(res.map { $0.contractId }, ["c2"])
        // Cutoff at 400 → c2 then c1.
        XCTAssertEqual(b.contractsExpiringBefore(Date(timeIntervalSince1970: 400)).map { $0.contractId }, ["c2", "c1"])
    }

    // ── Deadlines ────────────────────────────────────────────────────────────

    func testUpcomingDeadlinesFiltersPastAndOrdersAscending() {
        let b = InMemoryLegalBoard()
        b.add(LegalDeadline(deadlineId: "d1", matterId: "m", description: "past", dueOn: Date(timeIntervalSince1970: 50)))
        b.add(LegalDeadline(deadlineId: "d2", matterId: "m", description: "soon", dueOn: Date(timeIntervalSince1970: 150)))
        b.add(LegalDeadline(deadlineId: "d3", matterId: "m", description: "later", dueOn: Date(timeIntervalSince1970: 250)))
        let res = b.upcomingDeadlines(Date(timeIntervalSince1970: 100))
        XCTAssertEqual(res.map { $0.deadlineId }, ["d2", "d3"])
    }

    // ── Clauses ──────────────────────────────────────────────────────────────

    func testClausesByTagCaseInsensitive() throws {
        let b = InMemoryLegalBoard()
        b.addClause(Clause(clauseId: "c1", title: "A", body: "…", tags: ["Risk", "Liability"]))
        b.addClause(Clause(clauseId: "c2", title: "B", body: "…", tags: ["boilerplate"]))
        let hits = try b.clausesByTag("risk")
        XCTAssertEqual(hits.map { $0.clauseId }, ["c1"])
    }

    func testClausesByTagThrowsOnBlank() {
        let b = InMemoryLegalBoard()
        XCTAssertThrowsError(try b.clausesByTag("   ")) { error in
            XCTAssertEqual(error as? LegalError, .tagRequired)
        }
    }

    // ── Domain context ───────────────────────────────────────────────────────

    func testDomainContextConstants() {
        XCTAssertTrue(LegalDomainContext.systemPromptSnippet.hasPrefix("[DOMAIN: Legal]"))
        XCTAssertTrue(LegalDomainContext.complianceFlags.contains("POPIA"))
    }
}
