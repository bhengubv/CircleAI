import XCTest
@testable import CircleAI

/// Issuing and numbering.
final class BusinessOpsServiceTests: XCTestCase {

    let stamp = Date(timeIntervalSince1970: 1_782_896_400)   // 2026-07-01T09:00:00Z

    func zar(_ d: Decimal) -> Money { Money(d, "ZAR")! }

    func setup() -> (InvoiceService, InMemoryBusinessStore) {
        let store = InMemoryBusinessStore()
        return (InvoiceService(store: store,
                               numbers: SequentialInvoiceNumberGenerator(year: 2026),
                               clock: FixedBusinessClock(stamp)), store)
    }

    var lines: [BusinessInvoiceLine] {
        [BusinessInvoiceLine(description: "Work", quantity: 1, unitPrice: zar(1000), taxRate: 0)]
    }

    func draft(_ svc: InvoiceService) async throws -> BusinessInvoice {
        try await svc.createDraft(clientId: "c", currency: "ZAR", lines: lines,
                                  issueDate: CalendarDate(2026, 7, 1))
    }

    func testIssuingNumbersTheInvoiceAndMarksItSent() async throws {
        let (svc, _) = setup()
        let d = try await draft(svc)
        let issued = try await svc.issue(d.invoiceId)
        XCTAssertEqual(issued.status, .sent)
        XCTAssertEqual(issued.number, "INV-2026-0001")
    }

    // The customer already has the old number. Re-issuing must not renumber.
    func testReIssuingKeepsTheOriginalNumber() async throws {
        let (svc, _) = setup()
        let d = try await draft(svc)
        let first = try await svc.issue(d.invoiceId)
        let second = try await svc.issue(d.invoiceId)
        XCTAssertEqual(second.number, first.number)
    }

    func testNumbersAreSequentialAndZeroPadded() {
        let g = SequentialInvoiceNumberGenerator(year: 2026)
        XCTAssertEqual(g.next(), "INV-2026-0001")
        XCTAssertEqual(g.next(), "INV-2026-0002")
        let seeded = SequentialInvoiceNumberGenerator(prefix: "AC/", year: 2027, seed: 41)
        XCTAssertEqual(seeded.next(), "AC/2027-0042")
    }

    func testACancelledInvoiceCannotBeIssued() async throws {
        let (svc, _) = setup()
        let d = try await draft(svc)
        _ = try await svc.cancel(d.invoiceId)
        do {
            _ = try await svc.issue(d.invoiceId)
            XCTFail("expected a refusal")
        } catch let e as BusinessOpsError {
            XCTAssertEqual(e, .cancelledCannotBeIssued)
        }
    }

    func testAMissingInvoiceIsReportedById() async {
        let (svc, _) = setup()
        do {
            _ = try await svc.issue("nope")
            XCTFail("expected not found")
        } catch let e as BusinessOpsError {
            XCTAssertEqual(e, .invoiceNotFound("nope"))
        } catch { XCTFail("wrong error: \(error)") }
    }

    func testAPaidInvoiceCannotBeCancelled() async throws {
        let (svc, _) = setup()
        let d = try await draft(svc)
        _ = try await svc.markPaid(d.invoiceId)
        do {
            _ = try await svc.cancel(d.invoiceId)
            XCTFail("expected a refusal")
        } catch let e as BusinessOpsError {
            XCTAssertEqual(e, .paidCannotBeCancelled)
        }
    }
}
