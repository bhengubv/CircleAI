// CronExpressionTests.swift
//
// Verifies the 5-field cron parser ported in Proactive.swift: wildcards,
// integers, ranges, lists, steps, next-occurrence, matching, day-of-week
// (0=Sunday), and AND semantics between day-of-month and day-of-week. Values
// cross-checked against the C# CronExpression reference.

import XCTest
@testable import CircleAI

final class CronExpressionTests: XCTestCase {

    private var utc: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        return c
    }()

    private func date(_ y: Int, _ mo: Int, _ d: Int, _ h: Int, _ mi: Int) -> Date {
        utc.date(from: DateComponents(year: y, month: mo, day: d, hour: h, minute: mi, second: 0))!
    }

    func testRejectsWrongFieldCount() {
        XCTAssertThrowsError(try CronExpression.parse("* * *"))
        XCTAssertThrowsError(try CronExpression.parse("* * * * * *"))
    }

    func testEveryMinuteMatchesAlways() throws {
        let cron = try CronExpression.parse("* * * * *")
        XCTAssertTrue(cron.matches(date(2026, 7, 8, 12, 34)))
    }

    func testSpecificMinuteHour() throws {
        // 30 6 * * *  → 06:30 every day.
        let cron = try CronExpression.parse("30 6 * * *")
        XCTAssertTrue(cron.matches(date(2026, 7, 8, 6, 30)))
        XCTAssertFalse(cron.matches(date(2026, 7, 8, 6, 31)))
        XCTAssertFalse(cron.matches(date(2026, 7, 8, 7, 30)))
    }

    func testRange() throws {
        // 0 9-17 * * *  → top of the hour, 9am–5pm.
        let cron = try CronExpression.parse("0 9-17 * * *")
        XCTAssertTrue(cron.matches(date(2026, 7, 8, 9, 0)))
        XCTAssertTrue(cron.matches(date(2026, 7, 8, 17, 0)))
        XCTAssertFalse(cron.matches(date(2026, 7, 8, 8, 0)))
        XCTAssertFalse(cron.matches(date(2026, 7, 8, 18, 0)))
    }

    func testList() throws {
        // 0,15,30,45 * * * *  → quarter hours.
        let cron = try CronExpression.parse("0,15,30,45 * * * *")
        XCTAssertTrue(cron.matches(date(2026, 7, 8, 10, 15)))
        XCTAssertTrue(cron.matches(date(2026, 7, 8, 10, 45)))
        XCTAssertFalse(cron.matches(date(2026, 7, 8, 10, 20)))
    }

    func testStep() throws {
        // */15 * * * *  → every 15 minutes (0,15,30,45).
        let cron = try CronExpression.parse("*/15 * * * *")
        XCTAssertTrue(cron.matches(date(2026, 7, 8, 0, 0)))
        XCTAssertTrue(cron.matches(date(2026, 7, 8, 0, 30)))
        XCTAssertFalse(cron.matches(date(2026, 7, 8, 0, 7)))
    }

    func testDayOfWeekSundayIsZero() throws {
        // 0 12 * * 0  → noon on Sunday. 2026-07-12 is a Sunday.
        let cron = try CronExpression.parse("0 12 * * 0")
        XCTAssertTrue(cron.matches(date(2026, 7, 12, 12, 0)), "2026-07-12 should be Sunday")
        // 2026-07-08 is a Wednesday.
        XCTAssertFalse(cron.matches(date(2026, 7, 8, 12, 0)))
    }

    func testDayOfMonthAndDayOfWeekAreAnded() throws {
        // 0 0 8 * 3  → midnight on the 8th AND a Wednesday. 2026-07-08 is Wed the 8th.
        let cron = try CronExpression.parse("0 0 8 * 3")
        XCTAssertTrue(cron.matches(date(2026, 7, 8, 0, 0)))
        // The 8th of a month that is NOT a Wednesday should fail (AND).
        // 2026-08-08 is a Saturday → day-of-month matches, day-of-week doesn't.
        XCTAssertFalse(cron.matches(date(2026, 8, 8, 0, 0)))
    }

    func testGetNextOccurrence() throws {
        // 30 6 * * *  → next 06:30 strictly after the given moment.
        let cron = try CronExpression.parse("30 6 * * *")
        let after = date(2026, 7, 8, 6, 0)
        let next = try cron.getNextOccurrence(after: after)
        XCTAssertEqual(next, date(2026, 7, 8, 6, 30))

        // If already past 06:30 → next day.
        let after2 = date(2026, 7, 8, 7, 0)
        let next2 = try cron.getNextOccurrence(after: after2)
        XCTAssertEqual(next2, date(2026, 7, 9, 6, 30))
    }

    func testGetNextOccurrenceIsStrictlyAfter() throws {
        let cron = try CronExpression.parse("* * * * *")
        let after = date(2026, 7, 8, 6, 0)
        let next = try cron.getNextOccurrence(after: after)
        // Every-minute: the next match is exactly one minute later.
        XCTAssertEqual(next, date(2026, 7, 8, 6, 1))
    }

    func testInvalidRangeThrows() {
        XCTAssertThrowsError(try CronExpression.parse("99 * * * *"))   // minute > 59
        XCTAssertThrowsError(try CronExpression.parse("* 25 * * *"))   // hour > 23
        XCTAssertThrowsError(try CronExpression.parse("* * * * 9"))    // dow > 6
        XCTAssertThrowsError(try CronExpression.parse("*/0 * * * *"))  // step 0
    }
}
