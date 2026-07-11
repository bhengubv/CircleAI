// RealEstateBoardTests.swift
//
// Exercises the RealEstate records/enum's Codable round-trips and the
// deterministic behaviour of InMemoryRealEstateBoard — property/listing
// registration, close (incl. unknown-listing throw), active-in-suburb
// (case-insensitive, listed-descending), suburb average (nil when empty), and
// blank-suburb throws. Also checks the RealEstateDomainContext constants.
// Mirrors CircleAI.RealEstate/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class RealEstateBoardTests: XCTestCase {

    private func prop(_ id: String, suburb: String) -> Property {
        Property(propertyId: id, suburb: suburb, kind: .house, beds: 3, baths: 2, floorAreaM2: 120)
    }
    private func listing(_ id: String, prop: String, price: Decimal, listedAt: TimeInterval, active: Bool = true) -> Listing {
        Listing(listingId: id, propertyId: prop, askingPrice: price, currency: "ZAR",
                listedUtc: Date(timeIntervalSince1970: listedAt), isActive: active)
    }

    func testPropertyKindCodableRoundTrip() throws {
        XCTAssertEqual(try JSONDecoder().decode(PropertyKind.self, from: try JSONEncoder().encode(PropertyKind.townhouse)), .townhouse)
    }

    func testPropertyAndListingCodableRoundTrip() throws {
        let p = prop("p1", suburb: "Rosebank")
        XCTAssertEqual(try JSONDecoder().decode(Property.self, from: try JSONEncoder().encode(p)), p)
        let l = listing("l1", prop: "p1", price: 100, listedAt: 5)
        XCTAssertEqual(try JSONDecoder().decode(Listing.self, from: try JSONEncoder().encode(l)), l)
    }

    func testActiveInSuburbCaseInsensitiveAndListedDescending() throws {
        let b = InMemoryRealEstateBoard()
        b.registerProperty(prop("p1", suburb: "Rosebank"))
        b.registerProperty(prop("p2", suburb: "rosebank"))
        b.registerProperty(prop("p3", suburb: "Sandton"))
        b.list(listing("l1", prop: "p1", price: 1_000_000, listedAt: 10))
        b.list(listing("l2", prop: "p2", price: 2_000_000, listedAt: 20))
        b.list(listing("l3", prop: "p3", price: 9_000_000, listedAt: 30))
        b.list(listing("l4", prop: "p1", price: 500_000, listedAt: 5, active: false))   // inactive
        let rose = try b.activeInSuburb("ROSEBANK")
        XCTAssertEqual(rose.map { $0.listingId }, ["l2", "l1"])   // descending by listedUtc
    }

    func testCloseMakesListingInactiveAndUnknownThrows() throws {
        let b = InMemoryRealEstateBoard()
        b.registerProperty(prop("p1", suburb: "Rosebank"))
        b.list(listing("l1", prop: "p1", price: 100, listedAt: 1))
        try b.close(listingId: "l1")
        XCTAssertTrue(try b.activeInSuburb("Rosebank").isEmpty)
        XCTAssertThrowsError(try b.close(listingId: "ghost")) { XCTAssertEqual($0 as? RealEstateError, .unknownListing("ghost")) }
    }

    func testSuburbAverageNilWhenEmptyElseMean() throws {
        let b = InMemoryRealEstateBoard()
        b.registerProperty(prop("p1", suburb: "Rosebank"))
        b.registerProperty(prop("p2", suburb: "Rosebank"))
        XCTAssertNil(try b.suburbAverage("Rosebank"))
        b.list(listing("l1", prop: "p1", price: 100, listedAt: 1))
        b.list(listing("l2", prop: "p2", price: 300, listedAt: 2))
        XCTAssertEqual(try b.suburbAverage("Rosebank"), Decimal(200))
    }

    func testBlankSuburbThrows() {
        let b = InMemoryRealEstateBoard()
        XCTAssertThrowsError(try b.activeInSuburb(" ")) { XCTAssertEqual($0 as? RealEstateError, .suburbRequired) }
        XCTAssertThrowsError(try b.suburbAverage("")) { XCTAssertEqual($0 as? RealEstateError, .suburbRequired) }
    }

    func testValuationAndViewingDoNotThrow() {
        let b = InMemoryRealEstateBoard()
        b.value(Valuation(propertyId: "p1", estimatedValue: 1000, source: "avm", atUtc: Date()))
        b.scheduleViewing(Viewing(viewingId: "vw1", listingId: "l1", attendeeName: "Ada", atUtc: Date()))
        // No throw / no crash is the assertion.
    }

    func testDomainContext() {
        XCTAssertTrue(RealEstateDomainContext.systemPromptSnippet.contains("[DOMAIN: RealEstate]"))
        XCTAssertEqual(RealEstateDomainContext.complianceFlags, ["Alienation_of_Land_Act", "Rental_Housing_Act", "PPRA", "FICA", "POPIA"])
    }
}
