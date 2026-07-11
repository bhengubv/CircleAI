// CommerceFinanceBoardTests.swift
//
// Exercises the finance records' Codable round-trips and the deterministic
// behaviour of InMemoryInvoiceBoard — issue/get, tax-inclusive remaining
// balance, payments, overdue marking (case-insensitive "Paid" skip), overdue
// listing, and total outstanding. Mirrors
// CircleAI.Commerce.Finance/FinancePrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class CommerceFinanceBoardTests: XCTestCase {

    private func invoice(_ id: String, due: Date, status: String, _ lines: [InvoiceLine]) -> Invoice {
        Invoice(invoiceId: id, customerId: "c1", issueDate: Date(timeIntervalSince1970: 0),
                dueDate: due, lines: lines, currency: "ZAR", status: status)
    }

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testLineAndInvoiceCodableRoundTrip() throws {
        let line = InvoiceLine(description: "Widget", amount: 100, taxPct: 15)
        XCTAssertEqual(try JSONDecoder().decode(InvoiceLine.self, from: try JSONEncoder().encode(line)), line)
        let inv = invoice("i1", due: Date(timeIntervalSince1970: 100), status: "Issued", [line])
        XCTAssertEqual(try JSONDecoder().decode(Invoice.self, from: try JSONEncoder().encode(inv)), inv)
    }

    func testPaymentCodableRoundTrip() throws {
        let p = FinancePayment(paymentId: "p1", invoiceId: "i1", amount: 50, atUtc: Date(timeIntervalSince1970: 3))
        XCTAssertEqual(try JSONDecoder().decode(FinancePayment.self, from: try JSONEncoder().encode(p)), p)
    }

    // ── Remaining balance (tax-inclusive) ────────────────────────────────────

    func testRemainingOnAppliesTaxThenSubtractsPayments() {
        let b = InMemoryInvoiceBoard()
        // 100 @ 15% = 115, 50 @ 0% = 50 → billed 165.
        b.issue(invoice("i1", due: Date(timeIntervalSince1970: 100), status: "Issued", [
            InvoiceLine(description: "A", amount: 100, taxPct: 15),
            InvoiceLine(description: "B", amount: 50, taxPct: 0)
        ]))
        XCTAssertEqual(b.remainingOn("i1"), 165)
        b.recordPayment(FinancePayment(paymentId: "p1", invoiceId: "i1", amount: 65, atUtc: Date()))
        XCTAssertEqual(b.remainingOn("i1"), 100)
    }

    func testRemainingOnUnknownInvoiceIsZero() {
        XCTAssertEqual(InMemoryInvoiceBoard().remainingOn("ghost"), 0)
    }

    // ── Overdue ──────────────────────────────────────────────────────────────

    func testMarkOverdueFlipsPastDueUnpaidAndSkipsPaid() {
        let b = InMemoryInvoiceBoard()
        b.issue(invoice("past", due: Date(timeIntervalSince1970: 100), status: "Issued", []))
        b.issue(invoice("future", due: Date(timeIntervalSince1970: 500), status: "Issued", []))
        b.issue(invoice("paid", due: Date(timeIntervalSince1970: 100), status: "paid", [])) // case-insensitive
        b.markOverdue(Date(timeIntervalSince1970: 200))
        XCTAssertEqual(b.get("past")?.status, "Overdue")
        XCTAssertEqual(b.get("future")?.status, "Issued")
        XCTAssertEqual(b.get("paid")?.status, "paid")
        XCTAssertEqual(b.overdue().map { $0.invoiceId }, ["past"])
    }

    // ── Total outstanding ────────────────────────────────────────────────────

    func testTotalOutstandingSumsRemaining() {
        let b = InMemoryInvoiceBoard()
        b.issue(invoice("i1", due: Date(timeIntervalSince1970: 100), status: "Issued", [
            InvoiceLine(description: "A", amount: 100, taxPct: 0)
        ]))
        b.issue(invoice("i2", due: Date(timeIntervalSince1970: 100), status: "Issued", [
            InvoiceLine(description: "B", amount: 200, taxPct: 0)
        ]))
        b.recordPayment(FinancePayment(paymentId: "p1", invoiceId: "i1", amount: 40, atUtc: Date()))
        // (100-40) + 200 = 260.
        XCTAssertEqual(b.totalOutstanding(), 260)
    }

    // ── Domain context ───────────────────────────────────────────────────────

    func testDomainContextConstants() {
        XCTAssertTrue(CommerceFinanceDomainContext.systemPromptSnippet.hasPrefix("[DOMAIN: Commerce.Finance]"))
        XCTAssertTrue(CommerceFinanceDomainContext.complianceFlags.contains("NCA_34_2005"))
    }
}
