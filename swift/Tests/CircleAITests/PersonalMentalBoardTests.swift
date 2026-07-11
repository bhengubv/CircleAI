// PersonalMentalBoardTests.swift
//
// Exercises the mental-health records' Codable round-trips (incl. the Int-backed
// Mood ordinals) and the deterministic behaviour of InMemoryMentalHealthBoard —
// mood logging + 7-day window, journal entries (newest-first, blank-id throw),
// case-insensitive coping-strategy tagging (blank-tag throw), and the 7-day mood
// average (incl. the empty=NaN case). Recent timestamps are used so the 7-day
// window applies. Mirrors CircleAI.Personal.Mental/PersonalMentalPrimitives.cs.

import XCTest
import Foundation
@testable import CircleAI

final class PersonalMentalBoardTests: XCTestCase {

    private func ago(_ days: Double) -> Date { Date().addingTimeInterval(-days * 24 * 60 * 60) }

    // ── Enum ordinals ────────────────────────────────────────────────────────

    func testMoodOrdinalsMatchCSharp() {
        XCTAssertEqual(Mood.veryLow.rawValue, 0)
        XCTAssertEqual(Mood.low.rawValue, 1)
        XCTAssertEqual(Mood.neutral.rawValue, 2)
        XCTAssertEqual(Mood.good.rawValue, 3)
        XCTAssertEqual(Mood.great.rawValue, 4)
        XCTAssertEqual(Mood.allCases.count, 5)
    }

    // ── DTO Codable round-trips ──────────────────────────────────────────────

    func testMoodLogCodableRoundTrip() throws {
        let m = MoodLog(mood: .good, atUtc: Date(timeIntervalSince1970: 10), note: "ok day")
        XCTAssertEqual(try JSONDecoder().decode(MoodLog.self, from: try JSONEncoder().encode(m)), m)
    }

    func testJournalAndStrategyCodableRoundTrip() throws {
        let j = JournalEntry(entryId: "e1", title: "T", body: "B", atUtc: Date(timeIntervalSince1970: 5))
        XCTAssertEqual(try JSONDecoder().decode(JournalEntry.self, from: try JSONEncoder().encode(j)), j)
        let s = CopingStrategy(strategyId: "s1", title: "Box breathing", description: "…", tags: ["anxiety", "calm"])
        XCTAssertEqual(try JSONDecoder().decode(CopingStrategy.self, from: try JSONEncoder().encode(s)), s)
    }

    // ── Mood window ──────────────────────────────────────────────────────────

    func testLast7DaysFiltersAndOrdersAscending() {
        let b = InMemoryMentalHealthBoard()
        b.logMood(MoodLog(mood: .low, atUtc: ago(1), note: nil))
        b.logMood(MoodLog(mood: .great, atUtc: ago(3), note: nil))
        b.logMood(MoodLog(mood: .veryLow, atUtc: ago(10), note: nil)) // outside window
        let recent = b.last7Days()
        XCTAssertEqual(recent.count, 2)
        // ascending by time → 3-days-ago first, then 1-day-ago.
        XCTAssertEqual(recent.map { $0.mood }, [.great, .low])
    }

    // ── Journal ──────────────────────────────────────────────────────────────

    func testEntriesNewestFirstAndReplaceById() throws {
        let b = InMemoryMentalHealthBoard()
        try b.addEntry(JournalEntry(entryId: "e1", title: "old", body: "b", atUtc: Date(timeIntervalSince1970: 100)))
        try b.addEntry(JournalEntry(entryId: "e2", title: "new", body: "b", atUtc: Date(timeIntervalSince1970: 300)))
        try b.addEntry(JournalEntry(entryId: "e1", title: "replaced", body: "b", atUtc: Date(timeIntervalSince1970: 200)))
        XCTAssertEqual(b.entries.map { $0.title }, ["new", "replaced"]) // by time desc; e1 now at 200
    }

    func testAddEntryThrowsOnBlankId() {
        let b = InMemoryMentalHealthBoard()
        XCTAssertThrowsError(try b.addEntry(JournalEntry(entryId: "  ", title: "t", body: "b", atUtc: Date()))) { error in
            XCTAssertEqual(error as? MentalHealthError, .entryIdRequired)
        }
    }

    // ── Coping strategies ────────────────────────────────────────────────────

    func testStrategiesByTagCaseInsensitiveAndBlankThrows() throws {
        let b = InMemoryMentalHealthBoard()
        b.registerStrategy(CopingStrategy(strategyId: "s1", title: "A", description: "…", tags: ["Anxiety", "Calm"]))
        b.registerStrategy(CopingStrategy(strategyId: "s2", title: "B", description: "…", tags: ["sleep"]))
        XCTAssertEqual(try b.strategiesByTag("anxiety").map { $0.strategyId }, ["s1"])
        XCTAssertThrowsError(try b.strategiesByTag("")) { error in
            XCTAssertEqual(error as? MentalHealthError, .tagRequired)
        }
    }

    // ── 7-day average ────────────────────────────────────────────────────────

    func testAvgMood7DayComputesMean() {
        let b = InMemoryMentalHealthBoard()
        b.logMood(MoodLog(mood: .low, atUtc: ago(1), note: nil))     // 1
        b.logMood(MoodLog(mood: .great, atUtc: ago(2), note: nil))   // 4
        XCTAssertEqual(b.avgMood7Day(), 2.5, accuracy: 1e-9)
    }

    func testAvgMood7DayEmptyIsNaN() {
        XCTAssertTrue(InMemoryMentalHealthBoard().avgMood7Day().isNaN)
    }

    // ── Domain context ───────────────────────────────────────────────────────

    func testDomainContextConstants() {
        XCTAssertTrue(PersonalMentalDomainContext.systemPromptSnippet.hasPrefix("[DOMAIN: Personal.Mental]"))
        XCTAssertTrue(PersonalMentalDomainContext.complianceFlags.contains("Crisis_Protocol"))
    }
}
