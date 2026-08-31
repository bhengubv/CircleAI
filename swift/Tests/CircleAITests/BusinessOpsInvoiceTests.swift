import XCTest
@testable import CircleAI

/// Invoice arithmetic and the state machine around it.
final class BusinessOpsInvoiceTests: XCTestCase {

    private func zar(_ d: Decimal) -> Money { Money(d, "ZAR")! }

    private func dec(_ s: String) -> Decimal { Decimal(string: s)! }

    private func service(_ at: Date = Date(timeIntervalSince1970: 1_782_896_400))
        -> (InvoiceService, InMemoryBusinessStore) {
        let store = InMemoryBusinessStore()
        return (InvoiceService(store: store,
                               numbers: SequentialInvoiceNumberGenerator(year: 2026),
                               clock: FixedBusinessClock(at)), store)
    }

    private var vatLines: [BusinessInvoiceLine] {
        [
            BusinessInvoiceLine(description: "Logo suite", quantity: 1,
                                unitPrice: zar(8500), taxRate: dec("0.15")),
            BusinessInvoiceLine(description: "Business cards", quantity: 2,
                                unitPrice: zar(750), taxRate: dec("0.15")),
        ]
    }

    // MARK: - Arithmetic

    func testALineTotalsQuantityTimesPricePlusTax() {
        let l = BusinessInvoiceLine(description: "x", quantity: 2,
                                    unitPrice: zar(750), taxRate: dec("0.15"))
        XCTAssertEqual(l.lineSubtotal.amount, 1500)
        XCTAssertEqual(l.lineTax.amount, 225)
        XCTAssertEqual(l.lineTotal.amount, 1725)
    }

    func testTheInvoiceSumsItsLines() {
        let inv = BusinessInvoice(invoiceId: "i", clientId: "c", currency: "ZAR", lines: vatLines)
        XCTAssertEqual(inv.subtotal.amount, 10000)
        XCTAssertEqual(inv.taxTotal.amount, 1500)
        XCTAssertEqual(inv.total.amount, 11500)
    }

    // Rounding at the LINE, then summing - so the total matches what the
    // customer gets adding the printed lines by hand.
    func testEachLineRoundsBeforeTheLinesAreSummed() {
        let lines = (0..<3).map { _ in
            BusinessInvoiceLine(description: "third", quantity: 1,
                                unitPrice: Money(dec("0.335"), "ZAR")!, taxRate: 0)
        }
        let inv = BusinessInvoice(invoiceId: "i", clientId: "c", currency: "ZAR", lines: lines)
        // 0.335 rounds to 0.34 per line, so 1.02 - not 1.005 rounded to 1.01.
        XCTAssertEqual(inv.subtotal.amount, dec("1.02"))
    }

    func testAnEmptyInvoiceTotalsZeroInItsOwnCurrency() {
        let inv = BusinessInvoice(invoiceId: "i", clientId: "c", currency: "NGN")
        XCTAssertEqual(inv.total.amount, 0)
        XCTAssertEqual(inv.total.currency, "NGN")
        XCTAssertTrue(inv.isSettled)
    }

    func testBalanceDueSubtractsWhatWasPaid() {
        let inv = BusinessInvoice(invoiceId: "i", clientId: "c", currency: "ZAR",
                                  lines: vatLines, amountPaid: zar(1500))
        XCTAssertEqual(inv.balanceDue.amount, 10000)
        XCTAssertFalse(inv.isSettled)
    }

    // MARK: - Overdue

    func testAnUnpaidSentInvoicePastItsDueDateIsOverdue() {
        let inv = BusinessInvoice(invoiceId: "i", clientId: "c", currency: "ZAR", lines: vatLines,
                                  status: .sent, issueDate: CalendarDate(2026, 7, 1),
                                  dueDate: CalendarDate(2026, 7, 31))
        XCTAssertTrue(inv.isOverdue(asOf: CalendarDate(2026, 8, 1)))
        XCTAssertFalse(inv.isOverdue(asOf: CalendarDate(2026, 7, 31)))
    }

    // A draft was never sent, so it cannot be late.
    func testADraftIsNeverOverdue() {
        let inv = BusinessInvoice(invoiceId: "i", clientId: "c", currency: "ZAR", lines: vatLines,
                                  status: .draft, dueDate: CalendarDate(2026, 7, 31))
        XCTAssertFalse(inv.isOverdue(asOf: CalendarDate(2027, 1, 1)))
    }

    func testACancelledInvoiceIsNeverOverdue() {
        let inv = BusinessInvoice(invoiceId: "i", clientId: "c", currency: "ZAR", lines: vatLines,
                                  status: .cancelled, dueDate: CalendarDate(2026, 7, 31))
        XCTAssertFalse(inv.isOverdue(asOf: CalendarDate(2027, 1, 1)))
    }

    func testASettledInvoiceIsNeverOverdue() {
        let inv = BusinessInvoice(invoiceId: "i", clientId: "c", currency: "ZAR", lines: vatLines,
                                  status: .sent, dueDate: CalendarDate(2026, 7, 31),
                                  amountPaid: zar(11500))
        XCTAssertFalse(inv.isOverdue(asOf: CalendarDate(2027, 1, 1)))
    }

    // MARK: - Creating

    func testADraftTakesItsDueDateFromTheClientTerms() async throws {
        let (svc, store) = service()
        try await store.clients.upsert(Client(clientId: "c", name: "Thabo", paymentTermsDays: 14))

        let inv = try await svc.createDraft(clientId: "c", currency: "ZAR", lines: vatLines,
                                            issueDate: CalendarDate(2026, 7, 1))
        XCTAssertEqual(inv.dueDate, CalendarDate(2026, 7, 15))
        XCTAssertEqual(inv.status, .draft)
        XCTAssertNil(inv.number)
    }

    func testExplicitTermsBeatTheClientTerms() async throws {
        let (svc, store) = service()
        try await store.clients.upsert(Client(clientId: "c", name: "Thabo", paymentTermsDays: 14))
        let inv = try await svc.createDraft(clientId: "c", currency: "ZAR", lines: vatLines,
                                            issueDate: CalendarDate(2026, 7, 1),
                                            paymentTermsDays: 60, notes: nil)
        XCTAssertEqual(inv.dueDate, CalendarDate(2026, 8, 30))
    }

    func testAnUnknownClientFallsBackToThirtyDays() async throws {
        let (svc, _) = service()
        let inv = try await svc.createDraft(clientId: "nobody", currency: "ZAR", lines: [],
                                            issueDate: CalendarDate(2026, 7, 1))
        XCTAssertEqual(inv.dueDate, CalendarDate(2026, 7, 31))
    }

    // Caught at creation. Caught later it is a total that means nothing.
    func testALineInAnotherCurrencyIsRefusedAtCreation() async {
        let (svc, _) = service()
        let mixed = [BusinessInvoiceLine(description: "Lagos work", quantity: 1,
                                         unitPrice: Money(1000, "NGN")!, taxRate: 0)]
        do {
            _ = try await svc.createDraft(clientId: "c", currency: "ZAR", lines: mixed,
                                          issueDate: CalendarDate(2026, 7, 1))
            XCTFail("expected a currency mismatch")
        } catch let e as BusinessOpsError {
            XCTAssertEqual(e, .lineCurrencyMismatch(line: "Lagos work",
                                                    lineCurrency: "NGN", invoiceCurrency: "ZAR"))
        } catch { XCTFail("wrong error: \(error)") }
    }
}
