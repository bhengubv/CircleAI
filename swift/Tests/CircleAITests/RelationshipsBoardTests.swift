// RelationshipsBoardTests.swift
//
// Exercises the Relationships records' Codable round-trips and the deterministic
// behaviour of InMemoryRelationshipsBoard — contacts (name-asc), important dates
// this month (day-asc), touchpoints + last-contact, and not-contacted-since.
// Also checks the RelationshipsDomainContext constants. Mirrors
// CircleAI.Relationships/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class RelationshipsBoardTests: XCTestCase {

    private var utcCal: Calendar {
        var c = Calendar(identifier: .gregorian); c.timeZone = TimeZone(identifier: "UTC")!; return c
    }

    /// A date on the given day-of-month within the current UTC month/year.
    private func thisMonthDay(_ day: Int) -> Date {
        let now = utcCal.dateComponents([.year, .month], from: Date())
        return utcCal.date(from: DateComponents(year: now.year, month: now.month, day: day, hour: 12))!
    }

    func testPersonContactCodableRoundTrip() throws {
        let c = PersonContact(contactId: "c1", name: "Ayanda", relationship: "friend", notes: "loves tea")
        XCTAssertEqual(try JSONDecoder().decode(PersonContact.self, from: try JSONEncoder().encode(c)), c)
    }

    func testContactsNameOrdered() {
        let b = InMemoryRelationshipsBoard()
        b.addContact(PersonContact(contactId: "c2", name: "Zola", relationship: "cousin", notes: nil))
        b.addContact(PersonContact(contactId: "c1", name: "Ayanda", relationship: "friend", notes: nil))
        XCTAssertEqual(b.getContact("c1")?.name, "Ayanda")
        XCTAssertEqual(b.contacts.map { $0.name }, ["Ayanda", "Zola"])
    }

    func testUpcomingThisMonthDayOrdered() {
        let b = InMemoryRelationshipsBoard()
        b.addImportantDate(ImportantDate(dateId: "d2", contactId: "c1", kind: "birthday", date: thisMonthDay(20)))
        b.addImportantDate(ImportantDate(dateId: "d1", contactId: "c2", kind: "anniversary", date: thisMonthDay(5)))
        // A date in a clearly different month (six months away) must be excluded.
        let otherMonth = utcCal.date(byAdding: .month, value: 6, to: thisMonthDay(15))!
        b.addImportantDate(ImportantDate(dateId: "d3", contactId: "c3", kind: "birthday", date: otherMonth))
        XCTAssertEqual(b.upcomingThisMonth().map { $0.dateId }, ["d1", "d2"])
    }

    func testLastContactAndNotContactedSince() {
        let b = InMemoryRelationshipsBoard()
        b.addContact(PersonContact(contactId: "c1", name: "A", relationship: "f", notes: nil))
        b.addContact(PersonContact(contactId: "c2", name: "B", relationship: "f", notes: nil))
        let cutoff = Date(timeIntervalSince1970: 1000)
        b.recordTouchpoint(ContactEvent(contactId: "c1", kind: "call", atUtc: Date(timeIntervalSince1970: 500), note: nil))   // old
        b.recordTouchpoint(ContactEvent(contactId: "c1", kind: "text", atUtc: Date(timeIntervalSince1970: 900), note: nil))   // still < cutoff
        b.recordTouchpoint(ContactEvent(contactId: "c2", kind: "call", atUtc: Date(timeIntervalSince1970: 1500), note: nil))  // recent
        XCTAssertEqual(b.lastContact(contactId: "c1"), Date(timeIntervalSince1970: 900))
        XCTAssertNil(b.lastContact(contactId: "unknown"))
        // c1's last contact (900) < cutoff, so stale. c2 (1500) is fresh.
        XCTAssertEqual(b.notContactedSince(cutoff: cutoff).map { $0.contactId }, ["c1"])
    }

    func testDomainContext() {
        XCTAssertTrue(RelationshipsDomainContext.systemPromptSnippet.contains("[DOMAIN: Relationships]"))
        XCTAssertEqual(RelationshipsDomainContext.complianceFlags, ["POPIA", "Not_Therapy"])
        XCTAssertEqual(RelationshipsDomainContext.suggestedTools, ["journal", "mood_tracker", "calendar"])
    }
}
