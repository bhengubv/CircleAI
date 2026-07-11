// BeautyBoardTests.swift
//
// Exercises the Beauty records' Codable round-trips and the deterministic
// behaviour of InMemoryBeautyBoard — treatments, appointments (between, asc),
// skin profiles, and recommendations (name contains a concern). Also checks the
// BeautyDomainContext constants. Mirrors CircleAI.Beauty/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class BeautyBoardTests: XCTestCase {

    func testTreatmentCodableRoundTrip() throws {
        let t = Treatment(treatmentId: "t1", name: "Acne Facial", durationMinutes: 60, price: Decimal(string: "450.00")!, currency: "ZAR")
        XCTAssertEqual(try JSONDecoder().decode(Treatment.self, from: try JSONEncoder().encode(t)), t)
    }

    func testAppointmentsBetweenAscending() {
        let b = InMemoryBeautyBoard()
        let base = Date(timeIntervalSince1970: 1000)
        b.book(Appointment(apptId: "a1", clientName: "Zaan", treatmentId: "t1", atUtc: base.addingTimeInterval(30), notes: nil))
        b.book(Appointment(apptId: "a2", clientName: "Zaan", treatmentId: "t1", atUtc: base.addingTimeInterval(10), notes: "prep"))
        b.book(Appointment(apptId: "a3", clientName: "Zaan", treatmentId: "t1", atUtc: base.addingTimeInterval(999), notes: nil)) // after window
        let res = b.appointmentsBetween(start: base, end: base.addingTimeInterval(50))
        XCTAssertEqual(res.map { $0.apptId }, ["a2", "a1"])
    }

    func testRecommendForMatchesConcerns() {
        let b = InMemoryBeautyBoard()
        b.addTreatment(Treatment(treatmentId: "t1", name: "Acne Clearing Facial", durationMinutes: 60, price: 1, currency: "ZAR"))
        b.addTreatment(Treatment(treatmentId: "t2", name: "Hydration Boost", durationMinutes: 45, price: 1, currency: "ZAR"))
        b.addTreatment(Treatment(treatmentId: "t3", name: "Relaxing Massage", durationMinutes: 90, price: 1, currency: "ZAR"))
        // No profile -> empty.
        XCTAssertTrue(b.recommendFor(clientName: "Zaan").isEmpty)
        b.saveProfile(SkinProfile(clientName: "Zaan", skinType: "combination", concerns: ["Acne", "hydration"]))
        XCTAssertEqual(b.getProfile(clientName: "Zaan")?.skinType, "combination")
        XCTAssertEqual(Set(b.recommendFor(clientName: "Zaan").map { $0.treatmentId }), ["t1", "t2"])
    }

    func testDomainContext() {
        XCTAssertTrue(BeautyDomainContext.systemPromptSnippet.contains("[DOMAIN: Beauty]"))
        XCTAssertEqual(BeautyDomainContext.complianceFlags, ["POPIA", "Medicines_Act_cosmetic_claims"])
        XCTAssertEqual(BeautyDomainContext.suggestedTools, ["product_db", "ingredient_checker", "web_search"])
    }
}
