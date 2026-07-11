// SportsBoardTests.swift
//
// Exercises the Sports records' Codable round-trips and the deterministic
// behaviour of InMemorySportsBoard — activity logging + history (desc, limited),
// weekly volume, personal best (fastest over distance), and training sessions
// (schedule/complete/upcoming). Also checks the SportsDomainContext constants.
// Mirrors CircleAI.Sports/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class SportsBoardTests: XCTestCase {

    private func at(_ t: TimeInterval) -> Date { Date(timeIntervalSince1970: t) }

    func testDistanceKindCodableRoundTrip() throws {
        for k in DistanceKind.allCases {
            XCTAssertEqual(try JSONDecoder().decode(DistanceKind.self, from: try JSONEncoder().encode(k)), k)
        }
        // Raw values match the C# member names.
        XCTAssertEqual(DistanceKind.run.rawValue, "Run")
        XCTAssertEqual(DistanceKind.row.rawValue, "Row")
    }

    func testActivityCodableRoundTrip() throws {
        let a = SportActivity(activityId: "a1", userId: "u1", kind: .run, distanceKm: 5, duration: 1500, atUtc: at(10))
        XCTAssertEqual(try JSONDecoder().decode(SportActivity.self, from: try JSONEncoder().encode(a)), a)
    }

    func testHistoryDescendingAndLimited() throws {
        let b = InMemorySportsBoard()
        b.log(SportActivity(activityId: "a1", userId: "u1", kind: .run, distanceKm: 5, duration: 1500, atUtc: at(10)))
        b.log(SportActivity(activityId: "a2", userId: "u1", kind: .run, distanceKm: 6, duration: 1600, atUtc: at(30)))
        b.log(SportActivity(activityId: "a3", userId: "u1", kind: .run, distanceKm: 7, duration: 1700, atUtc: at(20)))
        b.log(SportActivity(activityId: "a4", userId: "other", kind: .run, distanceKm: 8, duration: 1000, atUtc: at(99)))
        XCTAssertEqual(try b.history(userId: "u1").map { $0.activityId }, ["a2", "a3", "a1"])
        XCTAssertEqual(try b.history(userId: "u1", limit: 2).map { $0.activityId }, ["a2", "a3"])
        XCTAssertThrowsError(try b.history(userId: "u1", limit: 0)) { XCTAssertEqual($0 as? SportsError, .invalidLimit) }
    }

    func testTotalKmThisWeek() {
        let b = InMemorySportsBoard()
        // now = 2021-01-08 (Fri). Week start (Sun) = 2021-01-03 00:00 UTC.
        var cal = Calendar(identifier: .gregorian); cal.timeZone = TimeZone(identifier: "UTC")!
        let now = cal.date(from: DateComponents(year: 2021, month: 1, day: 8, hour: 12))!
        let inWeek = cal.date(from: DateComponents(year: 2021, month: 1, day: 4, hour: 9))!
        let before = cal.date(from: DateComponents(year: 2021, month: 1, day: 2, hour: 9))!
        b.log(SportActivity(activityId: "a1", userId: "u1", kind: .run, distanceKm: 5, duration: 1, atUtc: inWeek))
        b.log(SportActivity(activityId: "a2", userId: "u1", kind: .run, distanceKm: 3, duration: 1, atUtc: now))
        b.log(SportActivity(activityId: "a3", userId: "u1", kind: .run, distanceKm: 9, duration: 1, atUtc: before)) // last week
        b.log(SportActivity(activityId: "a4", userId: "u1", kind: .bike, distanceKm: 40, duration: 1, atUtc: now))   // other kind
        XCTAssertEqual(b.totalKmThisWeek(userId: "u1", kind: .run, now: now), 8, accuracy: 1e-9)
    }

    func testBestReturnsFastestOverDistance() {
        let b = InMemorySportsBoard()
        b.log(SportActivity(activityId: "a1", userId: "u1", kind: .run, distanceKm: 10, duration: 3000, atUtc: at(1)))
        b.log(SportActivity(activityId: "a2", userId: "u1", kind: .run, distanceKm: 12, duration: 2500, atUtc: at(2)))
        b.log(SportActivity(activityId: "a3", userId: "u1", kind: .run, distanceKm: 5, duration: 900, atUtc: at(3))) // too short
        let best = b.best(userId: "u1", kind: .run, distanceKm: 10)
        XCTAssertEqual(best?.time, 2500)
        XCTAssertEqual(best?.achievedUtc, at(2))
        XCTAssertNil(b.best(userId: "u1", kind: .swim, distanceKm: 1))
    }

    func testSessionsScheduleCompleteUpcoming() throws {
        let b = InMemorySportsBoard()
        let future = Date().addingTimeInterval(3600)
        b.schedule(TrainingSession(sessionId: "s1", userId: "u1", plan: "intervals", scheduledUtc: future, completed: false))
        b.schedule(TrainingSession(sessionId: "s2", userId: "u1", plan: "long run", scheduledUtc: future.addingTimeInterval(60), completed: false))
        b.schedule(TrainingSession(sessionId: "s3", userId: "u1", plan: "past", scheduledUtc: Date().addingTimeInterval(-3600), completed: false))
        XCTAssertEqual(b.upcoming(userId: "u1").map { $0.sessionId }, ["s1", "s2"])
        try b.complete(sessionId: "s1")
        XCTAssertEqual(b.upcoming(userId: "u1").map { $0.sessionId }, ["s2"])
        XCTAssertThrowsError(try b.complete(sessionId: "ghost")) { XCTAssertEqual($0 as? SportsError, .unknownSession("ghost")) }
    }

    func testDomainContext() {
        XCTAssertTrue(SportsDomainContext.systemPromptSnippet.contains("[DOMAIN: Sports]"))
        XCTAssertEqual(SportsDomainContext.complianceFlags, ["WADA", "SASCOC", "Sport_Recreation_SA", "POPIA"])
        XCTAssertEqual(SportsDomainContext.suggestedTools, ["performance_tracker", "analytics", "schedule_manager", "document_editor"])
    }
}
