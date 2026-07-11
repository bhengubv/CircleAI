// CommerceBoardTests.swift
//
// Exercises the commerce records' Codable round-trips and the deterministic
// behaviour of InMemoryCommerceBoard — customers, orders (newest-first),
// insertion-ordered line items, status updates (incl. unknown-order throw), and
// lifetime value. Mirrors CircleAI.Commerce/CommercePrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class CommerceBoardTests: XCTestCase {

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testCustomerCodableRoundTripWithAndWithoutEmail() throws {
        let c1 = CommerceCustomer(customerId: "c1", name: "A", email: "a@x.io",
                                  createdUtc: Date(timeIntervalSince1970: 1))
        XCTAssertEqual(try JSONDecoder().decode(CommerceCustomer.self, from: try JSONEncoder().encode(c1)), c1)
        let c2 = CommerceCustomer(customerId: "c2", name: "B", email: nil,
                                  createdUtc: Date(timeIntervalSince1970: 1))
        XCTAssertEqual(try JSONDecoder().decode(CommerceCustomer.self, from: try JSONEncoder().encode(c2)), c2)
    }

    func testOrderAndLineCodableRoundTrip() throws {
        let o = CommerceOrder(orderId: "o1", customerId: "c1", total: 199, currency: "ZAR",
                              status: "pending", atUtc: Date(timeIntervalSince1970: 2))
        XCTAssertEqual(try JSONDecoder().decode(CommerceOrder.self, from: try JSONEncoder().encode(o)), o)
        let l = CommerceLineItem(lineId: "l1", orderId: "o1", sku: "SKU", quantity: 2, unitPrice: 99.5)
        XCTAssertEqual(try JSONDecoder().decode(CommerceLineItem.self, from: try JSONEncoder().encode(l)), l)
    }

    // ── Customers ────────────────────────────────────────────────────────────

    func testAddAndGetCustomer() {
        let b = InMemoryCommerceBoard()
        b.addCustomer(CommerceCustomer(customerId: "c1", name: "Ann", email: nil, createdUtc: Date()))
        XCTAssertEqual(b.getCustomer("c1")?.name, "Ann")
        XCTAssertNil(b.getCustomer("missing"))
    }

    // ── Orders ───────────────────────────────────────────────────────────────

    func testOrdersForNewestFirst() {
        let b = InMemoryCommerceBoard()
        b.place(CommerceOrder(orderId: "o1", customerId: "c1", total: 10, currency: "ZAR",
                              status: "s", atUtc: Date(timeIntervalSince1970: 100)))
        b.place(CommerceOrder(orderId: "o2", customerId: "c1", total: 20, currency: "ZAR",
                              status: "s", atUtc: Date(timeIntervalSince1970: 300)))
        b.place(CommerceOrder(orderId: "o3", customerId: "other", total: 30, currency: "ZAR",
                              status: "s", atUtc: Date(timeIntervalSince1970: 400)))
        XCTAssertEqual(b.ordersFor(customerId: "c1").map { $0.orderId }, ["o2", "o1"])
    }

    func testUpdateStatusThrowsForUnknownOrder() {
        let b = InMemoryCommerceBoard()
        XCTAssertThrowsError(try b.updateStatus(orderId: "ghost", status: "shipped")) { error in
            XCTAssertEqual(error as? CommerceError, .unknownOrder("ghost"))
        }
    }

    func testUpdateStatusMutates() throws {
        let b = InMemoryCommerceBoard()
        b.place(CommerceOrder(orderId: "o1", customerId: "c1", total: 10, currency: "ZAR",
                              status: "pending", atUtc: Date(timeIntervalSince1970: 1)))
        try b.updateStatus(orderId: "o1", status: "shipped")
        XCTAssertEqual(b.ordersFor(customerId: "c1").first?.status, "shipped")
    }

    // ── Line items ───────────────────────────────────────────────────────────

    func testLinesForPreservesInsertionOrder() {
        let b = InMemoryCommerceBoard()
        b.addLine(CommerceLineItem(lineId: "l1", orderId: "o1", sku: "A", quantity: 1, unitPrice: 1))
        b.addLine(CommerceLineItem(lineId: "l2", orderId: "o2", sku: "B", quantity: 1, unitPrice: 1))
        b.addLine(CommerceLineItem(lineId: "l3", orderId: "o1", sku: "C", quantity: 1, unitPrice: 1))
        XCTAssertEqual(b.linesFor(orderId: "o1").map { $0.lineId }, ["l1", "l3"])
    }

    // ── Lifetime value ───────────────────────────────────────────────────────

    func testLifetimeValueSumsOrderTotals() {
        let b = InMemoryCommerceBoard()
        b.place(CommerceOrder(orderId: "o1", customerId: "c1", total: 100, currency: "ZAR",
                              status: "s", atUtc: Date(timeIntervalSince1970: 1)))
        b.place(CommerceOrder(orderId: "o2", customerId: "c1", total: 250, currency: "ZAR",
                              status: "s", atUtc: Date(timeIntervalSince1970: 2)))
        XCTAssertEqual(b.lifetimeValue(customerId: "c1"), 350)
        XCTAssertEqual(b.lifetimeValue(customerId: "nobody"), 0)
    }

    // ── Domain context ───────────────────────────────────────────────────────

    func testDomainContextConstants() {
        XCTAssertTrue(CommerceDomainContext.systemPromptSnippet.hasPrefix("[DOMAIN: Commerce]"))
        XCTAssertTrue(CommerceDomainContext.suggestedTools.contains("pricing_engine"))
    }
}
