// RetailBoardTests.swift
//
// Exercises the Retail records' Codable round-trips and the deterministic
// behaviour of InMemoryRetailBoard — product add/get, stock set/get, sale
// recording (incl. unknown-SKU throw + stock decrement), same-day revenue, and
// top-sellers-since (quantity-descending, topK, non-positive-topK throw). Also
// checks the RetailDomainContext constants. Mirrors CircleAI.Retail/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class RetailBoardTests: XCTestCase {

    private func prod(_ sku: String, _ price: Decimal = 10) -> Product {
        Product(sku: sku, name: "P-\(sku)", price: price, currency: "ZAR", category: "cat")
    }
    // A fixed UTC instant: 2026-07-10T12:00:00Z.
    private let noonUtc = Date(timeIntervalSince1970: 1_752_148_800)

    func testProductCodableRoundTrip() throws {
        let p = prod("A", 12.5)
        XCTAssertEqual(try JSONDecoder().decode(Product.self, from: try JSONEncoder().encode(p)), p)
    }

    func testSaleAndTopSellerCodableRoundTrip() throws {
        let s = Sale(saleId: "s1", sku: "A", quantity: 3, unitPrice: 4, atUtc: Date(timeIntervalSince1970: 9))
        XCTAssertEqual(try JSONDecoder().decode(Sale.self, from: try JSONEncoder().encode(s)), s)
        let t = TopSeller(sku: "A", sold: 3)
        XCTAssertEqual(try JSONDecoder().decode(TopSeller.self, from: try JSONEncoder().encode(t)), t)
    }

    func testProductAndStock() {
        let b = InMemoryRetailBoard()
        b.addProduct(prod("A"))
        XCTAssertEqual(b.getProduct("A")?.sku, "A")
        XCTAssertNil(b.getProduct("Z"))
        XCTAssertEqual(b.stock("A"), 0)         // default 0
        b.setStock(StockLevel(sku: "A", quantity: 20))
        XCTAssertEqual(b.stock("A"), 20)
    }

    func testRecordSaleDecrementsStockAndThrowsForUnknownSku() throws {
        let b = InMemoryRetailBoard()
        b.addProduct(prod("A"))
        b.setStock(StockLevel(sku: "A", quantity: 10))
        try b.recordSale(Sale(saleId: "s1", sku: "A", quantity: 3, unitPrice: 5, atUtc: noonUtc))
        XCTAssertEqual(b.stock("A"), 7)
        XCTAssertThrowsError(try b.recordSale(Sale(saleId: "s2", sku: "ghost", quantity: 1, unitPrice: 1, atUtc: noonUtc))) { err in
            XCTAssertEqual(err as? RetailError, .unknownSku("ghost"))
        }
    }

    func testRevenueTodayCountsOnlySameUtcDay() throws {
        let b = InMemoryRetailBoard()
        b.addProduct(prod("A"))
        // Two sales same UTC day as noonUtc; one the day before.
        try b.recordSale(Sale(saleId: "s1", sku: "A", quantity: 2, unitPrice: 10, atUtc: noonUtc))
        try b.recordSale(Sale(saleId: "s2", sku: "A", quantity: 1, unitPrice: 5, atUtc: noonUtc.addingTimeInterval(3600)))
        try b.recordSale(Sale(saleId: "s3", sku: "A", quantity: 5, unitPrice: 100, atUtc: noonUtc.addingTimeInterval(-24 * 3600)))
        XCTAssertEqual(b.revenueToday(noonUtc), Decimal(25))   // 2*10 + 1*5
    }

    func testTopSellersSinceOrderedByQuantityWithTopK() throws {
        let b = InMemoryRetailBoard()
        b.addProduct(prod("A")); b.addProduct(prod("B")); b.addProduct(prod("C"))
        let t0 = noonUtc
        try b.recordSale(Sale(saleId: "1", sku: "A", quantity: 2, unitPrice: 1, atUtc: t0.addingTimeInterval(10)))
        try b.recordSale(Sale(saleId: "2", sku: "A", quantity: 3, unitPrice: 1, atUtc: t0.addingTimeInterval(20)))
        try b.recordSale(Sale(saleId: "3", sku: "B", quantity: 10, unitPrice: 1, atUtc: t0.addingTimeInterval(30)))
        try b.recordSale(Sale(saleId: "4", sku: "C", quantity: 1, unitPrice: 1, atUtc: t0.addingTimeInterval(40)))
        // Sale before the cutoff is excluded.
        try b.recordSale(Sale(saleId: "old", sku: "C", quantity: 99, unitPrice: 1, atUtc: t0.addingTimeInterval(-10)))
        let top2 = try b.topSellersSince(t0, topK: 2)
        XCTAssertEqual(top2.map { $0.sku }, ["B", "A"])       // B=10, A=5
        XCTAssertEqual(top2.map { $0.sold }, [10, 5])
    }

    func testTopSellersDefaultTopKOverloadAndNonPositiveThrows() throws {
        let b = InMemoryRetailBoard()
        b.addProduct(prod("A"))
        try b.recordSale(Sale(saleId: "1", sku: "A", quantity: 1, unitPrice: 1, atUtc: noonUtc))
        XCTAssertEqual(try b.topSellersSince(noonUtc.addingTimeInterval(-1)).count, 1)
        XCTAssertThrowsError(try b.topSellersSince(noonUtc, topK: 0)) { err in
            XCTAssertEqual(err as? RetailError, .topKOutOfRange)
        }
    }

    func testDomainContext() {
        XCTAssertTrue(RetailDomainContext.systemPromptSnippet.contains("[DOMAIN: Retail]"))
        XCTAssertEqual(RetailDomainContext.complianceFlags, ["Consumer_Protection_Act", "POPIA", "Labour_Relations_Act"])
        XCTAssertEqual(RetailDomainContext.suggestedTools, ["pos_system", "inventory", "analytics", "promotions_engine"])
    }
}
