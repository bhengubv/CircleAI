// PersonalFinanceBoardTests.swift
//
// Exercises the personal-finance records' Codable round-trips and the
// deterministic behaviour of InMemoryPersonalFinanceBoard — account upsert,
// balance-adjusting transactions (incl. unknown-account throw), month filtering,
// case-insensitive budgets (ordered by category), and the monthly summary
// (totalIn / totalOut / byCategory). Dates are UTC to match the board's UTC
// calendar. Mirrors CircleAI.Personal.Finance/PersonalFinancePrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class PersonalFinanceBoardTests: XCTestCase {

    private func utc(_ year: Int, _ month: Int, _ day: Int = 10) -> Date {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        var comps = DateComponents()
        comps.year = year; comps.month = month; comps.day = day; comps.hour = 12
        return c.date(from: comps)!
    }

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testAccountAndTransactionCodableRoundTrip() throws {
        let a = FinanceAccount(accountId: "a1", name: "Cheque", balance: 500, currency: "ZAR")
        XCTAssertEqual(try JSONDecoder().decode(FinanceAccount.self, from: try JSONEncoder().encode(a)), a)
        let t = FinanceTransaction(txId: "t1", accountId: "a1", amount: -50, category: "food",
                                   note: "lunch", atUtc: utc(2026, 7))
        XCTAssertEqual(try JSONDecoder().decode(FinanceTransaction.self, from: try JSONEncoder().encode(t)), t)
    }

    func testBudgetAndSummaryCodableRoundTrip() throws {
        let b = BudgetLine(category: "food", monthlyLimit: 2000)
        XCTAssertEqual(try JSONDecoder().decode(BudgetLine.self, from: try JSONEncoder().encode(b)), b)
        let s = MonthSummary(year: 2026, month: 7, totalIn: 100, totalOut: 50, byCategory: ["food": -50, "pay": 100])
        XCTAssertEqual(try JSONDecoder().decode(MonthSummary.self, from: try JSONEncoder().encode(s)), s)
    }

    // ── Transactions ─────────────────────────────────────────────────────────

    func testRecordAdjustsBalance() throws {
        let b = InMemoryPersonalFinanceBoard()
        b.upsert(FinanceAccount(accountId: "a1", name: "Cheque", balance: 100, currency: "ZAR"))
        try b.record(FinanceTransaction(txId: "t1", accountId: "a1", amount: -30, category: "food",
                                        note: nil, atUtc: utc(2026, 7)))
        try b.record(FinanceTransaction(txId: "t2", accountId: "a1", amount: 200, category: "pay",
                                        note: nil, atUtc: utc(2026, 7)))
        XCTAssertEqual(b.getAccount("a1")?.balance, 270)
    }

    func testRecordThrowsForUnknownAccount() {
        let b = InMemoryPersonalFinanceBoard()
        XCTAssertThrowsError(try b.record(FinanceTransaction(txId: "t", accountId: "ghost", amount: 1,
                                                             category: "x", note: nil, atUtc: utc(2026, 7)))) { error in
            XCTAssertEqual(error as? PersonalFinanceError, .unknownAccount("ghost"))
        }
    }

    func testListForMonthFiltersByAccountAndMonth() throws {
        let b = InMemoryPersonalFinanceBoard()
        b.upsert(FinanceAccount(accountId: "a1", name: "C", balance: 0, currency: "ZAR"))
        try b.record(FinanceTransaction(txId: "jul", accountId: "a1", amount: 1, category: "x", note: nil, atUtc: utc(2026, 7)))
        try b.record(FinanceTransaction(txId: "aug", accountId: "a1", amount: 1, category: "x", note: nil, atUtc: utc(2026, 8)))
        XCTAssertEqual(b.listForMonth(accountId: "a1", year: 2026, month: 7).map { $0.txId }, ["jul"])
    }

    // ── Budgets ──────────────────────────────────────────────────────────────

    func testBudgetsCaseInsensitiveReplaceAndOrdered() {
        let b = InMemoryPersonalFinanceBoard()
        b.setBudget(BudgetLine(category: "Food", monthlyLimit: 1000))
        b.setBudget(BudgetLine(category: "food", monthlyLimit: 2000)) // replaces "Food"
        b.setBudget(BudgetLine(category: "airtime", monthlyLimit: 300))
        let budgets = b.budgets
        XCTAssertEqual(budgets.count, 2)
        XCTAssertEqual(budgets.map { $0.category }, ["airtime", "food"]) // ordered ascending
        XCTAssertEqual(budgets.first { $0.category == "food" }?.monthlyLimit, 2000)
    }

    // ── Summary ──────────────────────────────────────────────────────────────

    func testSummariseComputesInOutAndByCategory() throws {
        let b = InMemoryPersonalFinanceBoard()
        b.upsert(FinanceAccount(accountId: "a1", name: "C", balance: 0, currency: "ZAR"))
        try b.record(FinanceTransaction(txId: "t1", accountId: "a1", amount: 1000, category: "salary", note: nil, atUtc: utc(2026, 7)))
        try b.record(FinanceTransaction(txId: "t2", accountId: "a1", amount: -200, category: "food", note: nil, atUtc: utc(2026, 7)))
        try b.record(FinanceTransaction(txId: "t3", accountId: "a1", amount: -100, category: "food", note: nil, atUtc: utc(2026, 7)))
        let s = b.summarise(accountId: "a1", year: 2026, month: 7)
        XCTAssertEqual(s.totalIn, 1000)
        XCTAssertEqual(s.totalOut, 300)
        XCTAssertEqual(s.byCategory["salary"], 1000)
        XCTAssertEqual(s.byCategory["food"], -300)
    }

    // ── Domain context ───────────────────────────────────────────────────────

    func testDomainContextConstants() {
        XCTAssertTrue(PersonalFinanceDomainContext.systemPromptSnippet.hasPrefix("[DOMAIN: Personal.Finance]"))
        XCTAssertTrue(PersonalFinanceDomainContext.complianceFlags.contains("Not_Financial_Advice"))
    }
}
