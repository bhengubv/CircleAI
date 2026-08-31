import XCTest
@testable import CircleAI

/// Payment, settlement and the overdue sweep.
final class BusinessOpsPaymentTests: XCTestCase {

    private let stamp = Date(timeIntervalSince1970: 1_782_896_400)

    private func zar(_ d: Decimal) -> Money { Money(d, "ZAR")! }

    private func setup() -> InvoiceService {
        InvoiceService(store: InMemoryBusinessStore(),
                       numbers: SequentialInvoiceNumberGenerator(year: 2026),
                       clock: FixedBusinessClock(stamp))
    }

    private var lines: [BusinessInvoiceLine] {
        [BusinessInvoiceLine(description: "Work", quantity: 1, unitPrice: zar(1000), taxRate: 0)]
    }

    private func draft(_ svc: InvoiceService,
                       issued on: CalendarDate = CalendarDate(2026, 7, 1)) async throws -> BusinessInvoice {
        try await svc.createDraft(clientId: "c", currency: "ZAR", lines: lines, issueDate: on)
    }

    func testAPartialPaymentLeavesABalance() async throws {
        let svc = setup()
        let d = try await draft(svc)
        let paid = try await svc.recordPayment(d.invoiceId, amount: zar(400))
        XCTAssertEqual(paid.status, .partiallyPaid)
        XCTAssertEqual(paid.balanceDue.amount, 600)
    }

    func testPaymentsAccumulateUntilTheInvoiceIsSettled() async throws {
        let svc = setup()
        let d = try await draft(svc)
        _ = try await svc.recordPayment(d.invoiceId, amount: zar(400))
        let done = try await svc.recordPayment(d.invoiceId, amount: zar(600))
        XCTAssertEqual(done.status, .paid)
        XCTAssertTrue(done.isSettled)
    }

    func testOverpaymentStillSettlesRatherThanBreaking() async throws {
        let svc = setup()
        let d = try await draft(svc)
        let over = try await svc.recordPayment(d.invoiceId, amount: zar(1500))
        XCTAssertEqual(over.status, .paid)
        XCTAssertEqual(over.balanceDue.amount, -500)
    }

    func testAPaymentInAnotherCurrencyIsRefused() async throws {
        let svc = setup()
        let d = try await draft(svc)
        do {
            _ = try await svc.recordPayment(d.invoiceId, amount: Money(1000, "NGN")!)
            XCTFail("expected a currency refusal")
        } catch let e as BusinessOpsError {
            XCTAssertEqual(e, .paymentCurrencyMismatch(payment: "NGN", invoice: "ZAR"))
        }
    }

    func testAZeroPaymentIsRefused() async throws {
        let svc = setup()
        let d = try await draft(svc)
        do {
            _ = try await svc.recordPayment(d.invoiceId, amount: zar(0))
            XCTFail("expected a refusal")
        } catch let e as BusinessOpsError {
            XCTAssertEqual(e, .paymentMustBePositive)
        }
    }

    // markPaid on a settled invoice must not try to pay zero, which is refused.
    func testMarkingAnAlreadySettledInvoicePaidIsIdempotent() async throws {
        let svc = setup()
        let d = try await draft(svc)
        _ = try await svc.recordPayment(d.invoiceId, amount: zar(1000))
        let again = try await svc.markPaid(d.invoiceId)
        XCTAssertEqual(again.status, .paid)
    }

    func testMarkPaidSettlesTheRemainingBalance() async throws {
        let svc = setup()
        let d = try await draft(svc)
        _ = try await svc.recordPayment(d.invoiceId, amount: zar(250))
        let settled = try await svc.markPaid(d.invoiceId)
        XCTAssertEqual(settled.paidToDate.amount, 1000)
        XCTAssertTrue(settled.isSettled)
    }

    func testRefreshMarksWhatIsPastDueAndCountsItOnce() async throws {
        let svc = setup()
        let d = try await draft(svc)
        _ = try await svc.issue(d.invoiceId)

        let v0 = try await svc.refreshOverdue(asOf: CalendarDate(2026, 9, 1))
        XCTAssertEqual(v0, 1)
        let after = try await svc.get(d.invoiceId)
        XCTAssertEqual(after?.status, .overdue)
        let v1 = try await svc.refreshOverdue(asOf: CalendarDate(2026, 9, 1))
        XCTAssertEqual(v1, 0)
    }

    func testOverdueListingIsOldestDueFirst() async throws {
        let svc = setup()
        let a = try await draft(svc, issued: CalendarDate(2026, 5, 1))
        let b = try await draft(svc, issued: CalendarDate(2026, 6, 1))
        _ = try await svc.issue(a.invoiceId)
        _ = try await svc.issue(b.invoiceId)

        let overdue = try await svc.listOverdue(asOf: CalendarDate(2026, 9, 1))
        XCTAssertEqual(overdue.count, 2)
        XCTAssertEqual(overdue.first?.invoiceId, a.invoiceId)
    }

    func testListingFiltersByStatus() async throws {
        let svc = setup()
        let a = try await draft(svc)
        _ = try await draft(svc)
        _ = try await svc.issue(a.invoiceId)

        let v2 = try await svc.list(status: .draft).count
        XCTAssertEqual(v2, 1)
        let v3 = try await svc.list(status: .sent).count
        XCTAssertEqual(v3, 1)
        let v4 = try await svc.list().count
        XCTAssertEqual(v4, 2)
    }

    func testListingByClientOnlyReturnsThatClient() async throws {
        let svc = setup()
        _ = try await svc.createDraft(clientId: "one", currency: "ZAR", lines: lines,
                                      issueDate: CalendarDate(2026, 7, 1))
        _ = try await svc.createDraft(clientId: "two", currency: "ZAR", lines: lines,
                                      issueDate: CalendarDate(2026, 7, 1))
        let v5 = try await svc.listByClient("one").count
        XCTAssertEqual(v5, 1)
    }
}
