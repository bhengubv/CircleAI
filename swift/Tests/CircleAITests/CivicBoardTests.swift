// CivicBoardTests.swift
//
// Exercises the Civic records' Codable round-trips and the deterministic
// behaviour of InMemoryCivicBoard — issues (report/resolve/open), reps by
// district, and upcoming events (future-only, asc). Also checks the
// CivicDomainContext constants. Mirrors CircleAI.Civic/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class CivicBoardTests: XCTestCase {

    func testCivicIssueCodableRoundTrip() throws {
        let i = CivicIssue(issueId: "i1", category: "Water", description: "Burst pipe", lat: -26, lon: 28, reportedUtc: Date(timeIntervalSince1970: 5), status: "Open")
        XCTAssertEqual(try JSONDecoder().decode(CivicIssue.self, from: try JSONEncoder().encode(i)), i)
    }

    func testReportResolveOpenIssues() throws {
        let b = InMemoryCivicBoard()
        b.report(CivicIssue(issueId: "i1", category: "Water", description: "a", lat: 0, lon: 0, reportedUtc: Date(timeIntervalSince1970: 1), status: "Open"))
        b.report(CivicIssue(issueId: "i2", category: "Roads", description: "b", lat: 0, lon: 0, reportedUtc: Date(timeIntervalSince1970: 2), status: "Open"))
        XCTAssertEqual(Set(b.openIssues().map { $0.issueId }), ["i1", "i2"])
        try b.resolve(issueId: "i1", status: "Resolved")
        XCTAssertEqual(b.openIssues().map { $0.issueId }, ["i2"])
        XCTAssertThrowsError(try b.resolve(issueId: "ghost", status: "Resolved")) { XCTAssertEqual($0 as? CivicError, .unknownIssue("ghost")) }
    }

    func testRepsForDistrictCaseInsensitive() {
        let b = InMemoryCivicBoard()
        b.addRep(Representative(repId: "r1", name: "Thabo", office: "Ward 3", contactEmail: "t@x.co", district: "North"))
        b.addRep(Representative(repId: "r2", name: "Lerato", office: "Ward 4", contactEmail: "l@x.co", district: "north"))
        b.addRep(Representative(repId: "r3", name: "Nomsa", office: "Ward 5", contactEmail: "n@x.co", district: nil))
        XCTAssertEqual(Set(b.repsForDistrict("NORTH").map { $0.repId }), ["r1", "r2"])
    }

    func testUpcomingEventsFutureOnlyAscending() {
        let b = InMemoryCivicBoard()
        b.schedule(CivicEvent(eventId: "e2", title: "Later", atUtc: Date.distantFuture, location: "Hall", audience: "All"))
        b.schedule(CivicEvent(eventId: "e1", title: "Soon", atUtc: Date().addingTimeInterval(3600), location: "Hall", audience: "All"))
        b.schedule(CivicEvent(eventId: "e0", title: "Past", atUtc: Date.distantPast, location: "Hall", audience: "All"))
        XCTAssertEqual(b.upcomingEvents().map { $0.eventId }, ["e1", "e2"])
    }

    func testDomainContext() {
        XCTAssertTrue(CivicDomainContext.systemPromptSnippet.contains("[DOMAIN: Civic]"))
        XCTAssertEqual(CivicDomainContext.complianceFlags, ["PAJA", "PAIA", "Constitution_RSA", "Municipal_Systems_Act", "POPIA"])
        XCTAssertEqual(CivicDomainContext.suggestedTools, ["government_portals", "document_editor", "map", "web_search"])
    }
}
