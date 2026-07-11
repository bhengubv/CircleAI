// FaithBoardTests.swift
//
// Exercises the Faith records' Codable round-trips and the deterministic
// behaviour of InMemoryFaithBoard — services (between, asc), prayers (recent,
// desc, limited), and scripture (lookup, by-tradition). Also checks the
// FaithDomainContext constants. Mirrors CircleAI.Faith/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class FaithBoardTests: XCTestCase {

    func testScriptureReferenceCodableRoundTrip() throws {
        let r = ScriptureReference(referenceId: "ref1", tradition: "Christian", book: "John", chapter: 3, verse: 16, text: "For God...")
        XCTAssertEqual(try JSONDecoder().decode(ScriptureReference.self, from: try JSONEncoder().encode(r)), r)
    }

    func testServicesBetweenAscending() {
        let b = InMemoryFaithBoard()
        let base = Date(timeIntervalSince1970: 1000)
        b.schedule(FaithService(serviceId: "s1", communityName: "Grace", title: "Morning", startUtc: base.addingTimeInterval(30), location: "Hall"))
        b.schedule(FaithService(serviceId: "s2", communityName: "Grace", title: "Evening", startUtc: base.addingTimeInterval(10), location: "Hall"))
        b.schedule(FaithService(serviceId: "s3", communityName: "Grace", title: "Late", startUtc: base.addingTimeInterval(999), location: "Hall"))
        XCTAssertEqual(b.servicesBetween(start: base, end: base.addingTimeInterval(50)).map { $0.serviceId }, ["s2", "s1"])
    }

    func testRecentPrayersDescendingLimited() {
        let b = InMemoryFaithBoard()
        b.submitPrayer(PrayerRequest(requestId: "p1", author: "A", body: "x", submittedUtc: Date(timeIntervalSince1970: 10), isAnonymous: false))
        b.submitPrayer(PrayerRequest(requestId: "p2", author: "B", body: "y", submittedUtc: Date(timeIntervalSince1970: 30), isAnonymous: true))
        b.submitPrayer(PrayerRequest(requestId: "p3", author: "C", body: "z", submittedUtc: Date(timeIntervalSince1970: 20), isAnonymous: false))
        XCTAssertEqual(b.recentPrayers().map { $0.requestId }, ["p2", "p3", "p1"])
        XCTAssertEqual(b.recentPrayers(limit: 2).map { $0.requestId }, ["p2", "p3"])
    }

    func testScriptureLookupAndByTradition() {
        let b = InMemoryFaithBoard()
        b.addScripture(ScriptureReference(referenceId: "r1", tradition: "Christian", book: "John", chapter: 3, verse: 16, text: "a"))
        b.addScripture(ScriptureReference(referenceId: "r2", tradition: "christian", book: "Psalms", chapter: 23, verse: 1, text: "b"))
        b.addScripture(ScriptureReference(referenceId: "r3", tradition: "Buddhist", book: "Dhammapada", chapter: 1, verse: 1, text: "c"))
        XCTAssertEqual(b.lookup(tradition: "Christian", book: "John", chapter: 3, verse: 16)?.referenceId, "r1")
        XCTAssertNil(b.lookup(tradition: "Christian", book: "John", chapter: 9, verse: 9))
        XCTAssertEqual(Set(b.byTradition("CHRISTIAN").map { $0.referenceId }), ["r1", "r2"])
    }

    func testDomainContext() {
        XCTAssertTrue(FaithDomainContext.systemPromptSnippet.contains("[DOMAIN: Faith]"))
        XCTAssertEqual(FaithDomainContext.complianceFlags, ["POPIA", "Non_Denominational_Respect"])
        XCTAssertEqual(FaithDomainContext.suggestedTools, ["scripture_tools", "document_editor", "calendar"])
    }
}
