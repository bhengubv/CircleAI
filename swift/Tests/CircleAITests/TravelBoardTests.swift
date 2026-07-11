// TravelBoardTests.swift
//
// Exercises the Travel records' Codable round-trips and the deterministic
// behaviour of InMemoryTravelBoard — flights/stays/trips, trip cost (flights +
// stay nights, min 1 night), and upcoming trips (asc). Also checks the
// TravelDomainContext constants. Mirrors CircleAI.Travel/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class TravelBoardTests: XCTestCase {

    private func day(_ y: Int, _ m: Int, _ d: Int) -> Date {
        var cal = Calendar(identifier: .gregorian); cal.timeZone = TimeZone(identifier: "UTC")!
        return cal.date(from: DateComponents(year: y, month: m, day: d))!
    }

    func testTravelTripCodableRoundTrip() throws {
        let t = TravelTrip(tripId: "t1", name: "Trip", startDate: day(2021, 6, 1), endDate: day(2021, 6, 5), flightIds: ["f1"], stayIds: ["s1"])
        XCTAssertEqual(try JSONDecoder().decode(TravelTrip.self, from: try JSONEncoder().encode(t)), t)
    }

    func testTripCostSumsFlightsAndStayNights() throws {
        let b = InMemoryTravelBoard()
        b.add(Flight(flightId: "f1", from: "JNB", to: "CPT", departUtc: day(2021, 6, 1), arriveUtc: day(2021, 6, 1), carrier: "X", cabin: "Y", price: Decimal(string: "1200")!, currency: "ZAR"))
        b.add(HotelStay(stayId: "s1", hotel: "H", city: "CPT", checkIn: day(2021, 6, 1), checkOut: day(2021, 6, 4), nightlyRate: Decimal(string: "800")!, currency: "ZAR")) // 3 nights
        b.add(HotelStay(stayId: "s2", hotel: "H2", city: "CPT", checkIn: day(2021, 6, 4), checkOut: day(2021, 6, 4), nightlyRate: Decimal(string: "500")!, currency: "ZAR")) // 0 -> min 1 night
        b.plan(TravelTrip(tripId: "t1", name: "Trip", startDate: day(2021, 6, 1), endDate: day(2021, 6, 5), flightIds: ["f1"], stayIds: ["s1", "s2"]))
        // 1200 + 800*3 + 500*1 = 4100
        XCTAssertEqual(try b.tripCost(tripId: "t1"), Decimal(string: "4100")!)
        XCTAssertThrowsError(try b.tripCost(tripId: "ghost")) { XCTAssertEqual($0 as? TravelError, .unknownTrip("ghost")) }
    }

    func testUpcomingTripsAscending() {
        let b = InMemoryTravelBoard()
        let now = day(2021, 6, 1)
        b.plan(TravelTrip(tripId: "t2", name: "later", startDate: day(2021, 8, 1), endDate: day(2021, 8, 5), flightIds: [], stayIds: []))
        b.plan(TravelTrip(tripId: "t1", name: "sooner", startDate: day(2021, 7, 1), endDate: day(2021, 7, 5), flightIds: [], stayIds: []))
        b.plan(TravelTrip(tripId: "t0", name: "past", startDate: day(2021, 5, 1), endDate: day(2021, 5, 5), flightIds: [], stayIds: []))
        XCTAssertEqual(b.upcomingTrips(now: now).map { $0.tripId }, ["t1", "t2"])
    }

    func testDomainContext() {
        XCTAssertTrue(TravelDomainContext.systemPromptSnippet.contains("[DOMAIN: Travel]"))
        XCTAssertEqual(TravelDomainContext.complianceFlags, ["POPIA", "Consumer_Protection_Act", "IATA_aware"])
        XCTAssertEqual(TravelDomainContext.suggestedTools, ["flight_search", "mapping", "currency_converter", "web_search"])
    }
}
