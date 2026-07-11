// FamilyBoardTests.swift
//
// Exercises the Family records' Codable round-trips and the deterministic
// behaviour of InMemoryFamilyBoard — members (name-ordered), shared events
// (per-member, time-ordered), and shared expenses (total-paid-by and
// spend-by-category since a cutoff, case-insensitive category). Also checks the
// FamilyDomainContext constants. Mirrors CircleAI.Family/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class FamilyBoardTests: XCTestCase {

    private func member(_ id: String, _ name: String) -> FamilyMember {
        FamilyMember(memberId: id, name: name, role: "parent", dateOfBirth: Date(timeIntervalSince1970: 0))
    }

    func testFamilyEventCodableRoundTrip() throws {
        let e = FamilyEvent(eventId: "e1", title: "Dinner", atUtc: Date(timeIntervalSince1970: 5), memberIds: ["m1", "m2"])
        XCTAssertEqual(try JSONDecoder().decode(FamilyEvent.self, from: try JSONEncoder().encode(e)), e)
    }

    func testSharedExpenseCodableRoundTrip() throws {
        let x = SharedExpense(expenseId: "x1", paidById: "m1", amount: 42.5, currency: "ZAR", category: "Food", atUtc: Date(timeIntervalSince1970: 7))
        XCTAssertEqual(try JSONDecoder().decode(SharedExpense.self, from: try JSONEncoder().encode(x)), x)
    }

    func testMembersNameOrdered() {
        let b = InMemoryFamilyBoard()
        b.add(member("m2", "Zara"))
        b.add(member("m1", "Ann"))
        XCTAssertEqual(b.getMember("m1")?.name, "Ann")
        XCTAssertEqual(b.members.map { $0.name }, ["Ann", "Zara"])
    }

    func testEventsForMemberTimeOrdered() {
        let b = InMemoryFamilyBoard()
        b.schedule(FamilyEvent(eventId: "e1", title: "Late", atUtc: Date(timeIntervalSince1970: 30), memberIds: ["m1"]))
        b.schedule(FamilyEvent(eventId: "e2", title: "Early", atUtc: Date(timeIntervalSince1970: 10), memberIds: ["m1", "m2"]))
        b.schedule(FamilyEvent(eventId: "e3", title: "Other", atUtc: Date(timeIntervalSince1970: 20), memberIds: ["m2"]))
        XCTAssertEqual(b.eventsForMember("m1").map { $0.eventId }, ["e2", "e1"])
        XCTAssertEqual(b.eventsForMember("m2").map { $0.eventId }, ["e2", "e3"])
    }

    func testTotalPaidByAndSpendByCategorySinceCutoff() {
        let b = InMemoryFamilyBoard()
        let cutoff = Date(timeIntervalSince1970: 100)
        b.record(SharedExpense(expenseId: "x1", paidById: "m1", amount: 10, currency: "ZAR", category: "Food", atUtc: cutoff.addingTimeInterval(10)))
        b.record(SharedExpense(expenseId: "x2", paidById: "m1", amount: 20, currency: "ZAR", category: "food", atUtc: cutoff.addingTimeInterval(20)))
        b.record(SharedExpense(expenseId: "x3", paidById: "m2", amount: 30, currency: "ZAR", category: "Fuel", atUtc: cutoff.addingTimeInterval(30)))
        // Before the cutoff → excluded.
        b.record(SharedExpense(expenseId: "old", paidById: "m1", amount: 999, currency: "ZAR", category: "Food", atUtc: cutoff.addingTimeInterval(-10)))
        XCTAssertEqual(b.totalPaidBy("m1", since: cutoff), Decimal(30))            // 10 + 20
        XCTAssertEqual(b.spendByCategory("FOOD", since: cutoff), Decimal(30))      // case-insensitive
        XCTAssertEqual(b.spendByCategory("Fuel", since: cutoff), Decimal(30))
    }

    func testDomainContext() {
        XCTAssertTrue(FamilyDomainContext.systemPromptSnippet.contains("[DOMAIN: Family]"))
        XCTAssertEqual(FamilyDomainContext.complianceFlags, ["POPIA", "Childrens_Act_38_2005"])
        XCTAssertEqual(FamilyDomainContext.suggestedTools, ["shared_calendar", "family_budget", "document_editor", "task_manager"])
    }
}
