// CommerceAccountingBoardTests.swift
//
// Exercises the accounting records' Codable round-trips and the deterministic
// behaviour of InMemoryAccountingBoard — posting (incl. negative-amount throw),
// tax-rate definition, signed balances, period-scoped sums / listings, and net
// profit. Dates are constructed in UTC to match the board's UTC calendar.
// Mirrors CircleAI.Commerce.Accounting/AccountingPrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class CommerceAccountingBoardTests: XCTestCase {

    /// Builds a UTC date at the given year/month/day (matching the board's
    /// UTC/en_US_POSIX Gregorian calendar).
    private func utc(_ year: Int, _ month: Int, _ day: Int = 15) -> Date {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        var comps = DateComponents()
        comps.year = year; comps.month = month; comps.day = day; comps.hour = 12
        return c.date(from: comps)!
    }

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testEntryCodableRoundTrip() throws {
        let e = AccountingEntry(entryId: "e1", atUtc: utc(2026, 7), accountCode: "4000",
                                debitAmount: 0, creditAmount: 100, memo: "sale")
        XCTAssertEqual(try JSONDecoder().decode(AccountingEntry.self, from: try JSONEncoder().encode(e)), e)
    }

    func testTaxRateAndPeriodCodableRoundTrip() throws {
        let t = TaxRate(code: "VAT", percentage: 15)
        XCTAssertEqual(try JSONDecoder().decode(TaxRate.self, from: try JSONEncoder().encode(t)), t)
        let p = Period(year: 2026, month: 7)
        XCTAssertEqual(try JSONDecoder().decode(Period.self, from: try JSONEncoder().encode(p)), p)
    }

    // ── Posting ──────────────────────────────────────────────────────────────

    func testPostRejectsNegativeAmounts() {
        let b = InMemoryAccountingBoard()
        XCTAssertThrowsError(try b.post(AccountingEntry(entryId: "e", atUtc: utc(2026, 1),
                                                        accountCode: "4000", debitAmount: -1,
                                                        creditAmount: 0, memo: "")) ) { error in
            XCTAssertEqual(error as? AccountingError, .negativeAmount)
        }
        XCTAssertThrowsError(try b.post(AccountingEntry(entryId: "e", atUtc: utc(2026, 1),
                                                        accountCode: "4000", debitAmount: 0,
                                                        creditAmount: -1, memo: "")) ) { error in
            XCTAssertEqual(error as? AccountingError, .negativeAmount)
        }
    }

    // ── Tax ──────────────────────────────────────────────────────────────────

    func testDefineAndGetTax() {
        let b = InMemoryAccountingBoard()
        b.defineTax(TaxRate(code: "VAT", percentage: 15))
        XCTAssertEqual(b.getTax("VAT")?.percentage, 15)
        XCTAssertNil(b.getTax("NONE"))
    }

    // ── Balances & sums ──────────────────────────────────────────────────────

    func testAccountBalanceIsSignedDebitMinusCredit() throws {
        let b = InMemoryAccountingBoard()
        try b.post(AccountingEntry(entryId: "e1", atUtc: utc(2026, 7), accountCode: "1000",
                                   debitAmount: 100, creditAmount: 0, memo: ""))
        try b.post(AccountingEntry(entryId: "e2", atUtc: utc(2026, 7), accountCode: "1000",
                                   debitAmount: 0, creditAmount: 30, memo: ""))
        XCTAssertEqual(b.accountBalance("1000"), 70)
    }

    func testSumAndForAccountRespectPeriodAndOrder() throws {
        let b = InMemoryAccountingBoard()
        try b.post(AccountingEntry(entryId: "jul-b", atUtc: utc(2026, 7, 20), accountCode: "4000",
                                   debitAmount: 0, creditAmount: 50, memo: "later"))
        try b.post(AccountingEntry(entryId: "jul-a", atUtc: utc(2026, 7, 5), accountCode: "4000",
                                   debitAmount: 0, creditAmount: 20, memo: "earlier"))
        try b.post(AccountingEntry(entryId: "aug", atUtc: utc(2026, 8, 1), accountCode: "4000",
                                   debitAmount: 0, creditAmount: 999, memo: "other-month"))
        let july = Period(year: 2026, month: 7)
        // Signed sum for July = -(20+50) = -70 (both credits).
        XCTAssertEqual(b.sum("4000", july), -70)
        // Ordered ascending by time, other-month excluded.
        XCTAssertEqual(b.forAccount("4000", july).map { $0.entryId }, ["jul-a", "jul-b"])
    }

    func testNetProfit() throws {
        let b = InMemoryAccountingBoard()
        let p = Period(year: 2026, month: 7)
        // Revenue posted as debits so the signed sum is positive.
        try b.post(AccountingEntry(entryId: "rev", atUtc: utc(2026, 7), accountCode: "REV",
                                   debitAmount: 1000, creditAmount: 0, memo: ""))
        try b.post(AccountingEntry(entryId: "exp", atUtc: utc(2026, 7), accountCode: "EXP",
                                   debitAmount: 400, creditAmount: 0, memo: ""))
        XCTAssertEqual(b.netProfit(p, revenueAccount: "REV", expenseAccount: "EXP"), 600)
    }

    // ── Domain context ───────────────────────────────────────────────────────

    func testDomainContextConstants() {
        XCTAssertTrue(CommerceAccountingDomainContext.systemPromptSnippet.hasPrefix("[DOMAIN: Commerce.Accounting]"))
        XCTAssertTrue(CommerceAccountingDomainContext.complianceFlags.contains("SARS"))
    }
}
