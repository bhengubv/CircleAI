// TourismBoardTests.swift
//
// Exercises the Tourism records' Codable round-trips and the deterministic
// behaviour of InMemoryTourismBoard — attractions (in-city / by-tag, name-asc,
// validation), itineraries, and bookings snapshot. Also checks the
// TourismDomainContext constants. Mirrors CircleAI.Tourism/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class TourismBoardTests: XCTestCase {

    func testItineraryCodableRoundTrip() throws {
        let it = Itinerary(itineraryId: "i1", title: "Cape Trip", items: [
            ItineraryItem(dayIndex: 0, startLocal: 32400, endLocal: 39600, attractionId: "a1", note: "morning"),
            ItineraryItem(dayIndex: 0, startLocal: 46800, endLocal: 54000, attractionId: "a2", note: nil)
        ])
        XCTAssertEqual(try JSONDecoder().decode(Itinerary.self, from: try JSONEncoder().encode(it)), it)
    }

    func testAttractionsInCityAndByTagNameOrderedAndValidated() throws {
        let b = InMemoryTourismBoard()
        b.add(Attraction(attractionId: "a1", name: "Table Mountain", city: "Cape Town", country: "ZA", lat: -33.9, lon: 18.4, tags: ["nature", "hiking"]))
        b.add(Attraction(attractionId: "a2", name: "Boulders Beach", city: "cape town", country: "ZA", lat: -34.1, lon: 18.4, tags: ["nature", "penguins"]))
        b.add(Attraction(attractionId: "a3", name: "Union Buildings", city: "Pretoria", country: "ZA", lat: -25.7, lon: 28.2, tags: ["history"]))
        XCTAssertEqual(try b.attractionsInCity("CAPE TOWN").map { $0.name }, ["Boulders Beach", "Table Mountain"])
        XCTAssertEqual(try b.byTag("NATURE").map { $0.name }, ["Boulders Beach", "Table Mountain"])
        XCTAssertThrowsError(try b.attractionsInCity(" ")) { XCTAssertEqual($0 as? TourismError, .cityRequired) }
        XCTAssertThrowsError(try b.byTag("")) { XCTAssertEqual($0 as? TourismError, .tagRequired) }
    }

    func testPlanAndBookings() {
        let b = InMemoryTourismBoard()
        b.plan(Itinerary(itineraryId: "i1", title: "Trip", items: []))
        XCTAssertEqual(b.getItinerary("i1")?.title, "Trip")
        XCTAssertTrue(b.bookings.isEmpty)
        b.book(TourismBooking(bookingId: "b1", itineraryId: "i1", startDate: Date(timeIntervalSince1970: 1), travelers: 2, totalPrice: 5000, currency: "ZAR"))
        b.book(TourismBooking(bookingId: "b2", itineraryId: "i1", startDate: Date(timeIntervalSince1970: 2), travelers: 4, totalPrice: 9000, currency: "ZAR"))
        XCTAssertEqual(b.bookings.map { $0.bookingId }, ["b1", "b2"])
    }

    func testDomainContext() {
        XCTAssertTrue(TourismDomainContext.systemPromptSnippet.contains("[DOMAIN: Tourism]"))
        XCTAssertEqual(TourismDomainContext.complianceFlags, ["Tourism_Act_3_2014", "SABS_Tour_Ops", "SATSA", "POPIA"])
        XCTAssertEqual(TourismDomainContext.suggestedTools, ["mapping", "booking_system", "document_editor", "weather_api"])
    }
}
