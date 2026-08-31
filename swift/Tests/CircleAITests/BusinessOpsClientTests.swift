import XCTest
@testable import CircleAI

/// The client book, the CRM bridge and the sample data.
final class BusinessOpsClientTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)

    private func book() -> (ClientBook, InMemoryBusinessStore) {
        let store = InMemoryBusinessStore()
        return (ClientBook(store: store, clock: FixedBusinessClock(now)), store)
    }

    func testAClientIsStampedOnFirstSaveAndNotAgain() async throws {
        let (b, _) = book()
        let first = try await b.upsert(Client(clientId: "c1", name: "Nandi"))
        XCTAssertEqual(first.createdAtUtc, now)

        let later = ClientBook(store: InMemoryBusinessStore(),
                               clock: FixedBusinessClock(now.addingTimeInterval(9999)))
        let again = try await later.upsert(first)
        XCTAssertEqual(again.createdAtUtc, now)
    }

    func testAClientNeedsAnId() async {
        let (b, _) = book()
        do {
            _ = try await b.upsert(Client(clientId: "  ", name: "Nobody"))
            XCTFail("expected a refusal")
        } catch let e as BusinessOpsError { XCTAssertEqual(e, .missingField("clientId")) }
        catch { XCTFail("wrong error") }
    }

    // Name, email or phone - the three things somebody actually remembers.
    func testSearchMatchesNameEmailOrPhoneCaseInsensitively() async throws {
        let (b, _) = book()
        _ = try await b.upsert(Client(clientId: "c1", name: "Nandi Dlamini Design",
                                      email: "nandi@example.co.za", phone: "+27 82 555 0142"))
        _ = try await b.upsert(Client(clientId: "c2", name: "Thabo Trading CC",
                                      email: "accounts@thabo.example", phone: "+27 71 555 0199"))

        let v0 = try await b.search("dlamini").count
        XCTAssertEqual(v0, 1)
        let v1 = try await b.search("NANDI@").count
        XCTAssertEqual(v1, 1)
        let v2 = try await b.search("555").count
        XCTAssertEqual(v2, 2)
        let v3 = try await b.search("nobody").count
        XCTAssertEqual(v3, 0)
    }

    func testSearchRespectsTopK() async throws {
        let (b, _) = book()
        for i in 1...5 { _ = try await b.upsert(Client(clientId: "c\(i)", name: "Client \(i)")) }
        let v4 = try await b.search("Client", topK: 2).count
        XCTAssertEqual(v4, 2)
        let v5 = try await b.search("Client", topK: 0).count
        XCTAssertEqual(v5, 0)
    }

    func testListingIsByNameNotInsertionOrder() async throws {
        let (b, _) = book()
        _ = try await b.upsert(Client(clientId: "c1", name: "Zanele"))
        _ = try await b.upsert(Client(clientId: "c2", name: "Amara"))
        let names = try await b.list().map(\.name)
        XCTAssertEqual(names, ["Amara", "Zanele"])
    }

    func testRemovingReportsWhetherAnythingWasThere() async throws {
        let (b, _) = book()
        _ = try await b.upsert(Client(clientId: "c1", name: "Nandi"))
        let removed = try await b.remove("c1")
        let again = try await b.remove("c1")
        XCTAssertTrue(removed)
        XCTAssertFalse(again)
    }

    // MARK: - CRM bridge

    func testAClientBecomesAContactAndBack() {
        let client = Client(clientId: "c1", name: "Nandi Dlamini", email: "n@example.com",
                            phone: "+27 82 555 0142", defaultCurrency: "ZAR", paymentTermsDays: 14)
        let contact = client.toContact(companyId: "co-1")
        XCTAssertEqual(contact.contactId, "c1")
        XCTAssertEqual(contact.fullName, "Nandi Dlamini")
        XCTAssertEqual(contact.companyId, "co-1")

        let back = contact.toClient(defaultCurrency: "NGN", paymentTermsDays: 7)
        XCTAssertEqual(back.clientId, "c1")
        XCTAssertEqual(back.email, "n@example.com")
        XCTAssertEqual(back.defaultCurrency, "NGN")
        XCTAssertEqual(back.paymentTermsDays, 7)
    }

    func testAReminderBecomesAnActivityOnTheContact() {
        let r = Reminder(reminderId: "r1", title: "Chase INV-1",
                         dueAtUtc: now, kind: .invoiceDue)
        let a = r.toActivity(contactId: "c1")
        XCTAssertEqual(a.activityId, "r1")
        XCTAssertEqual(a.contactId, "c1")
        XCTAssertEqual(a.kind, "InvoiceDue")
        XCTAssertEqual(a.body, "Chase INV-1")
        XCTAssertEqual(a.atUtc, now)
    }

    // MARK: - Sample data

    func testTheSampleDataIsInternallyConsistent() {
        let inv = BusinessOpsSampleData.sampleInvoice()
        XCTAssertEqual(inv.subtotal.amount, 10000)
        XCTAssertEqual(inv.taxTotal.amount, 1500)
        XCTAssertEqual(inv.total.amount, 11500)
        XCTAssertEqual(inv.issueDate, CalendarDate(2026, 7, 1))
        XCTAssertEqual(inv.dueDate, CalendarDate(2026, 7, 31))
        XCTAssertFalse(inv.isSettled)
    }

    func testTheSampleClientsCoverMoreThanOneCurrency() {
        let currencies = Set(BusinessOpsSampleData.clients().map(\.defaultCurrency))
        XCTAssertTrue(currencies.contains("ZAR"))
        XCTAssertTrue(currencies.contains("NGN"))
    }

    func testTheSampleRemindersIncludeARecurringOne() {
        XCTAssertTrue(BusinessOpsSampleData.reminders().contains { $0.repeatRule.isRecurring })
    }

    func testTheNullPdfRendererRefusesRatherThanReturningABlankPage() async {
        do {
            _ = try await NullInvoicePdfRenderer.instance.render(
                BusinessOpsSampleData.sampleInvoice(), client: nil)
            XCTFail("expected a refusal")
        } catch let e as BusinessOpsError { XCTAssertEqual(e, .noPdfRenderer) }
        catch { XCTFail("wrong error") }
    }
}
