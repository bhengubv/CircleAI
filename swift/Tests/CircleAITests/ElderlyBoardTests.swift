// ElderlyBoardTests.swift
//
// Exercises the Elderly records' Codable round-trips and the deterministic
// behaviour of InMemoryElderlyCareBoard — care plans (per-resident), medication
// reminders (add, deactivate incl. unknown throw, active-for-resident), and
// check-ins (record, latest, missed-since). Note the C# `CheckIn` record is
// `ElderlyCheckIn` here to avoid colliding with Safety.Child's `CheckIn`. Also
// checks the ElderlyDomainContext constants. Mirrors CircleAI.Elderly/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class ElderlyBoardTests: XCTestCase {

    func testCarePlanCodableRoundTrip() throws {
        let p = CarePlan(planId: "cp1", residentName: "Gran", medicalConditions: ["diabetes"], allergies: ["penicillin"], carerNotes: "gentle")
        XCTAssertEqual(try JSONDecoder().decode(CarePlan.self, from: try JSONEncoder().encode(p)), p)
    }

    func testMedReminderCodableRoundTrip() throws {
        let r = MedReminder(reminderId: "r1", residentName: "Gran", medication: "Metformin", dailyAt: 8 * 3600, active: true)
        XCTAssertEqual(try JSONDecoder().decode(MedReminder.self, from: try JSONEncoder().encode(r)), r)
    }

    func testCheckInCodableRoundTrip() throws {
        let c = ElderlyCheckIn(checkInId: "ci1", residentName: "Gran", atUtc: Date(timeIntervalSince1970: 5), status: "OK", note: nil)
        XCTAssertEqual(try JSONDecoder().decode(ElderlyCheckIn.self, from: try JSONEncoder().encode(c)), c)
    }

    func testSetAndGetPlan() {
        let b = InMemoryElderlyCareBoard()
        b.setPlan(CarePlan(planId: "cp1", residentName: "Gran", medicalConditions: [], allergies: [], carerNotes: "n"))
        XCTAssertEqual(b.getPlan("Gran")?.planId, "cp1")
        XCTAssertNil(b.getPlan("Nobody"))
    }

    func testActiveRemindersAndDeactivate() throws {
        let b = InMemoryElderlyCareBoard()
        b.addReminder(MedReminder(reminderId: "r1", residentName: "Gran", medication: "A", dailyAt: 3600, active: true))
        b.addReminder(MedReminder(reminderId: "r2", residentName: "Gran", medication: "B", dailyAt: 7200, active: true))
        b.addReminder(MedReminder(reminderId: "r3", residentName: "Gramps", medication: "C", dailyAt: 3600, active: true))
        XCTAssertEqual(Set(b.activeRemindersFor("Gran").map { $0.reminderId }), ["r1", "r2"])
        try b.deactivateReminder(reminderId: "r1")
        XCTAssertEqual(b.activeRemindersFor("Gran").map { $0.reminderId }, ["r2"])
    }

    func testDeactivateUnknownReminderThrows() {
        let b = InMemoryElderlyCareBoard()
        XCTAssertThrowsError(try b.deactivateReminder(reminderId: "ghost")) { err in
            XCTAssertEqual(err as? ElderlyError, .unknownReminder("ghost"))
        }
    }

    func testLatestCheckInAndMissedSince() {
        let b = InMemoryElderlyCareBoard()
        // No check-in yet → latest nil, missed true.
        XCTAssertNil(b.latestCheckIn("Gran"))
        XCTAssertTrue(b.missedCheckIn(resident: "Gran", since: Date(timeIntervalSince1970: 100)))

        b.recordCheckIn(ElderlyCheckIn(checkInId: "c1", residentName: "Gran", atUtc: Date(timeIntervalSince1970: 50), status: "OK", note: nil))
        b.recordCheckIn(ElderlyCheckIn(checkInId: "c2", residentName: "Gran", atUtc: Date(timeIntervalSince1970: 150), status: "OK", note: nil))
        b.recordCheckIn(ElderlyCheckIn(checkInId: "other", residentName: "Gramps", atUtc: Date(timeIntervalSince1970: 999), status: "OK", note: nil))
        XCTAssertEqual(b.latestCheckIn("Gran")?.checkInId, "c2")
        // Latest (150) >= since (100) → not missed.
        XCTAssertFalse(b.missedCheckIn(resident: "Gran", since: Date(timeIntervalSince1970: 100)))
        // Latest (150) < since (200) → missed.
        XCTAssertTrue(b.missedCheckIn(resident: "Gran", since: Date(timeIntervalSince1970: 200)))
    }

    func testDomainContext() {
        XCTAssertTrue(ElderlyDomainContext.systemPromptSnippet.contains("[DOMAIN: Elderly]"))
        XCTAssertEqual(ElderlyDomainContext.complianceFlags, ["Older_Persons_Act_13_2006", "Social_Assistance_Act", "POPIA"])
        XCTAssertEqual(ElderlyDomainContext.suggestedTools, ["medication_reminder", "calendar", "web_search", "document_editor"])
    }
}
