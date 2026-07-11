// ParentingBoardTests.swift
//
// Exercises the Parenting records/enum's Codable round-trips and the
// deterministic behaviour of InMemoryParentingBoard — children (name-ordered),
// milestones (per-child, newest-first, blank-child throw), routines (keyed by
// child + day-of-week), and age-as-of (seconds; unknown-child throw). Also
// checks the DayOfWeek numbering and ParentingDomainContext constants.
// Mirrors CircleAI.Parenting/*.cs.

import XCTest
import Foundation
@testable import CircleAI

final class ParentingBoardTests: XCTestCase {

    private func child(_ id: String, _ name: String, dob: TimeInterval = 0) -> Child {
        Child(childId: id, name: name, dateOfBirth: Date(timeIntervalSince1970: dob), gender: nil)
    }

    func testDayOfWeekMatchesDotNetNumbering() {
        XCTAssertEqual(DayOfWeek.sunday.rawValue, 0)
        XCTAssertEqual(DayOfWeek.monday.rawValue, 1)
        XCTAssertEqual(DayOfWeek.saturday.rawValue, 6)
    }

    func testMilestoneAndRoutineCodableRoundTrip() throws {
        let m = Milestone(milestoneId: "m1", childId: "c1", category: "motor", description: "walks", achievedAtUtc: Date(timeIntervalSince1970: 5))
        XCTAssertEqual(try JSONDecoder().decode(Milestone.self, from: try JSONEncoder().encode(m)), m)
        let r = Routine(childId: "c1", dayOfWeek: .wednesday, entries: [RoutineEntry(time: "07:00", activity: "wake")])
        XCTAssertEqual(try JSONDecoder().decode(Routine.self, from: try JSONEncoder().encode(r)), r)
    }

    func testChildrenNameOrdered() {
        let b = InMemoryParentingBoard()
        b.addChild(child("c2", "Zed"))
        b.addChild(child("c1", "Amy"))
        XCTAssertEqual(b.getChild("c1")?.name, "Amy")
        XCTAssertEqual(b.children.map { $0.name }, ["Amy", "Zed"])
    }

    func testMilestonesNewestFirstAndUnknownChildEmpty() throws {
        let b = InMemoryParentingBoard()
        b.addChild(child("c1", "Amy"))
        try b.recordMilestone(Milestone(milestoneId: "m1", childId: "c1", category: "a", description: "d1", achievedAtUtc: Date(timeIntervalSince1970: 1)))
        try b.recordMilestone(Milestone(milestoneId: "m2", childId: "c1", category: "a", description: "d2", achievedAtUtc: Date(timeIntervalSince1970: 3)))
        try b.recordMilestone(Milestone(milestoneId: "m3", childId: "c1", category: "a", description: "d3", achievedAtUtc: Date(timeIntervalSince1970: 2)))
        XCTAssertEqual(b.milestonesFor("c1").map { $0.milestoneId }, ["m2", "m3", "m1"])
        XCTAssertTrue(b.milestonesFor("ghost").isEmpty)
    }

    func testRecordMilestoneBlankChildThrows() {
        let b = InMemoryParentingBoard()
        XCTAssertThrowsError(try b.recordMilestone(Milestone(milestoneId: "m", childId: "  ", category: "a", description: "d", achievedAtUtc: Date()))) { err in
            XCTAssertEqual(err as? ParentingError, .childIdRequired)
        }
    }

    func testRoutineKeyedByChildAndDay() {
        let b = InMemoryParentingBoard()
        let mon = Routine(childId: "c1", dayOfWeek: .monday, entries: [RoutineEntry(time: "07:00", activity: "school")])
        let tue = Routine(childId: "c1", dayOfWeek: .tuesday, entries: [RoutineEntry(time: "08:00", activity: "sport")])
        b.setRoutine(mon)
        b.setRoutine(tue)
        XCTAssertEqual(b.getRoutine(childId: "c1", dow: .monday)?.entries.first?.activity, "school")
        XCTAssertEqual(b.getRoutine(childId: "c1", dow: .tuesday)?.entries.first?.activity, "sport")
        XCTAssertNil(b.getRoutine(childId: "c1", dow: .friday))
    }

    func testAgeAsOfComputesSecondsAndUnknownThrows() throws {
        let b = InMemoryParentingBoard()
        b.addChild(child("c1", "Amy", dob: 0))
        let at = Date(timeIntervalSince1970: 86_400)   // 1 day later
        XCTAssertEqual(try b.ageAsOf(childId: "c1", at: at), 86_400, accuracy: 1e-6)
        XCTAssertThrowsError(try b.ageAsOf(childId: "ghost", at: at)) { XCTAssertEqual($0 as? ParentingError, .unknownChild("ghost")) }
    }

    func testDomainContext() {
        XCTAssertTrue(ParentingDomainContext.systemPromptSnippet.contains("[DOMAIN: Parenting]"))
        XCTAssertEqual(ParentingDomainContext.complianceFlags, ["Childrens_Act_38_2005", "POPIA"])
    }
}
