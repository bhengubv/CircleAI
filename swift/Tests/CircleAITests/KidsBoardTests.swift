// KidsBoardTests.swift
//
// Exercises the Kids records/enum Codable round-trips and the deterministic
// behaviour of InMemoryKidsBoard — content for an age band (title-asc), daily
// limits, time used today (same UTC day), and over-limit checks for
// screen/reading/other kinds. Also checks the KidsDomainContext constants.
// Mirrors CircleAI.Kids/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class KidsBoardTests: XCTestCase {

    private func day(_ y: Int, _ m: Int, _ d: Int, _ h: Int) -> Date {
        var cal = Calendar(identifier: .gregorian); cal.timeZone = TimeZone(identifier: "UTC")!
        return cal.date(from: DateComponents(year: y, month: m, day: d, hour: h))!
    }

    func testAgeAppropriatenessCodableRoundTrip() throws {
        for band in AgeAppropriateness.allCases {
            XCTAssertEqual(try JSONDecoder().decode(AgeAppropriateness.self, from: try JSONEncoder().encode(band)), band)
        }
        XCTAssertEqual(AgeAppropriateness.earlyPrimary.rawValue, "EarlyPrimary")
        XCTAssertEqual(AgeAppropriateness.preTeen.rawValue, "PreTeen")
    }

    func testKidsContentCodableRoundTrip() throws {
        let c = KidsContent(contentId: "c1", title: "ABC", ageBand: .toddler, kind: "video", tags: ["letters"])
        XCTAssertEqual(try JSONDecoder().decode(KidsContent.self, from: try JSONEncoder().encode(c)), c)
    }

    func testContentForBandTitleOrdered() {
        let b = InMemoryKidsBoard()
        b.addContent(KidsContent(contentId: "c1", title: "Zebra", ageBand: .preschool, kind: "video", tags: []))
        b.addContent(KidsContent(contentId: "c2", title: "Apple", ageBand: .preschool, kind: "video", tags: []))
        b.addContent(KidsContent(contentId: "c3", title: "Algebra", ageBand: .teen, kind: "quiz", tags: []))
        XCTAssertEqual(b.contentFor(band: .preschool).map { $0.title }, ["Apple", "Zebra"])
    }

    func testUsedTodayAndOverLimit() {
        let b = InMemoryKidsBoard()
        let now = day(2021, 1, 8, 18)
        b.recordTime(TimeLog(kidName: "Lwandle", kind: "screen", duration: 1800, atUtc: day(2021, 1, 8, 9)))  // 30 min today
        b.recordTime(TimeLog(kidName: "Lwandle", kind: "screen", duration: 1800, atUtc: day(2021, 1, 8, 14))) // +30 min today
        b.recordTime(TimeLog(kidName: "Lwandle", kind: "screen", duration: 9999, atUtc: day(2021, 1, 7, 14))) // yesterday, ignored
        b.recordTime(TimeLog(kidName: "Lwandle", kind: "reading", duration: 600, atUtc: day(2021, 1, 8, 10)))
        XCTAssertEqual(b.usedToday(kidName: "Lwandle", kind: "screen", now: now), 3600, accuracy: 1e-9)
        // No limits set -> never over limit.
        XCTAssertFalse(b.overLimit(kidName: "Lwandle", kind: "screen", now: now))
        b.setLimits(DailyTime(kidName: "Lwandle", screenLimit: 3000, readingLimit: 1200)) // 50 min screen, 20 min reading
        XCTAssertEqual(b.limitsFor(kidName: "Lwandle")?.screenLimit, 3000)
        XCTAssertTrue(b.overLimit(kidName: "Lwandle", kind: "screen", now: now))   // 3600 > 3000
        XCTAssertFalse(b.overLimit(kidName: "Lwandle", kind: "reading", now: now)) // 600 <= 1200
        XCTAssertFalse(b.overLimit(kidName: "Lwandle", kind: "gaming", now: now))  // unknown kind -> unbounded
    }

    func testDomainContext() {
        XCTAssertTrue(KidsDomainContext.systemPromptSnippet.contains("[DOMAIN: Kids]"))
        XCTAssertEqual(KidsDomainContext.complianceFlags, ["POPIA_Childrens_Data", "COPPA_principles", "Childrens_Act", "CAPS_curriculum"])
        XCTAssertEqual(KidsDomainContext.suggestedTools, ["educational_content", "story_tools", "quiz_tools"])
    }
}
