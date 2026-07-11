// HospitalityBoardTests.swift
//
// Exercises the Hospitality records' Codable round-trips and the deterministic
// behaviour of InMemoryHospitalityBoard — rooms, availability on a date,
// reservations + check-out (with cleaning flag), and front-desk notes (desc).
// Also checks the HospitalityDomainContext constants. Mirrors
// CircleAI.Hospitality/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class HospitalityBoardTests: XCTestCase {

    private func day(_ y: Int, _ m: Int, _ d: Int) -> Date {
        var cal = Calendar(identifier: .gregorian); cal.timeZone = TimeZone(identifier: "UTC")!
        return cal.date(from: DateComponents(year: y, month: m, day: d))!
    }

    func testGuestReservationCodableRoundTrip() throws {
        let r = GuestReservation(reservationId: "r1", guestName: "Sipho", roomId: "101", checkIn: day(2021, 1, 1), checkOut: day(2021, 1, 3))
        XCTAssertEqual(try JSONDecoder().decode(GuestReservation.self, from: try JSONEncoder().encode(r)), r)
    }

    func testAvailableOnExcludesBookedAndDirty() {
        let b = InMemoryHospitalityBoard()
        b.addRoom(HotelRoom(roomId: "101", type: "std", nightlyRate: 100, currency: "ZAR", isClean: true))
        b.addRoom(HotelRoom(roomId: "102", type: "std", nightlyRate: 100, currency: "ZAR", isClean: true))
        b.addRoom(HotelRoom(roomId: "103", type: "std", nightlyRate: 100, currency: "ZAR", isClean: false)) // dirty
        b.reserve(GuestReservation(reservationId: "r1", guestName: "A", roomId: "101", checkIn: day(2021, 1, 1), checkOut: day(2021, 1, 5)))
        // On Jan 2: 101 booked, 103 dirty -> only 102 free.
        XCTAssertEqual(b.availableOn(date: day(2021, 1, 2)).map { $0.roomId }, ["102"])
        // On Jan 5 (== checkOut, exclusive): 101 free again.
        XCTAssertEqual(Set(b.availableOn(date: day(2021, 1, 5)).map { $0.roomId }), ["101", "102"])
    }

    func testCheckOutFlagsCleaningAndUnknownThrows() throws {
        let b = InMemoryHospitalityBoard()
        b.addRoom(HotelRoom(roomId: "101", type: "std", nightlyRate: 100, currency: "ZAR", isClean: true))
        b.reserve(GuestReservation(reservationId: "r1", guestName: "A", roomId: "101", checkIn: day(2021, 1, 1), checkOut: day(2021, 1, 3)))
        try b.checkOut(reservationId: "r1", roomNeedsCleaning: true)
        XCTAssertEqual(b.getRoom("101")?.isClean, false)
        XCTAssertThrowsError(try b.checkOut(reservationId: "ghost", roomNeedsCleaning: false)) {
            XCTAssertEqual($0 as? HospitalityError, .unknownReservation("ghost"))
        }
    }

    func testNotesDescending() {
        let b = InMemoryHospitalityBoard()
        b.addNote(FrontDeskNote(noteId: "n1", reservationId: "r1", body: "early", atUtc: Date(timeIntervalSince1970: 10)))
        b.addNote(FrontDeskNote(noteId: "n2", reservationId: "r1", body: "late", atUtc: Date(timeIntervalSince1970: 30)))
        b.addNote(FrontDeskNote(noteId: "n3", reservationId: "r2", body: "other", atUtc: Date(timeIntervalSince1970: 99)))
        XCTAssertEqual(b.notesFor(reservationId: "r1").map { $0.noteId }, ["n2", "n1"])
    }

    func testDomainContext() {
        XCTAssertTrue(HospitalityDomainContext.systemPromptSnippet.contains("[DOMAIN: Hospitality]"))
        XCTAssertEqual(HospitalityDomainContext.complianceFlags, ["Tourism_Act", "CATHSSETA", "Liquor_Act", "Health_Regs", "POPIA"])
        XCTAssertEqual(HospitalityDomainContext.suggestedTools, ["pms_system", "analytics", "document_editor", "reservation_engine"])
    }
}
