// HostingSchedulingTests.swift
//
// Verifies CronScheduleParser next-occurrence math, CronJob copy semantics,
// InMemoryScheduledTaskStore CRUD + due filtering, and ScheduledAIService
// single-job execution + completion events. Mirrors the C# behaviour.

import XCTest
@testable import CircleAI

final class HostingSchedulingTests: XCTestCase {

    private var utc: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        return c
    }()
    private func date(_ y: Int, _ mo: Int, _ d: Int, _ h: Int, _ mi: Int) -> Date {
        utc.date(from: DateComponents(year: y, month: mo, day: d, hour: h, minute: mi, second: 0))!
    }

    // ── CronScheduleParser ──────────────────────────────────────────────────

    func testEveryMinuteAdvancesOneMinute() throws {
        let next = try CronScheduleParser.getNextOccurrence("* * * * *", after: date(2026, 7, 8, 6, 0))
        XCTAssertEqual(next, date(2026, 7, 8, 6, 1))
    }

    func testFixedTimeSameDay() throws {
        let next = try CronScheduleParser.getNextOccurrence("30 6 * * *", after: date(2026, 7, 8, 6, 0))
        XCTAssertEqual(next, date(2026, 7, 8, 6, 30))
    }

    func testFixedTimeRollsToNextDay() throws {
        // 06:30 daily, asked at 07:00 → next is tomorrow 06:30.
        let next = try CronScheduleParser.getNextOccurrence("30 6 * * *", after: date(2026, 7, 8, 7, 0))
        XCTAssertEqual(next, date(2026, 7, 9, 6, 30))
    }

    func testStepMinutes() throws {
        // */15 → next quarter-hour strictly after.
        let next = try CronScheduleParser.getNextOccurrence("*/15 * * * *", after: date(2026, 7, 8, 6, 5))
        XCTAssertEqual(next, date(2026, 7, 8, 6, 15))
    }

    func testRangeAndList() throws {
        // Minute 0, hours 9 or 17 → at 10:00 next is 17:00.
        let next = try CronScheduleParser.getNextOccurrence("0 9,17 * * *", after: date(2026, 7, 8, 10, 0))
        XCTAssertEqual(next, date(2026, 7, 8, 17, 0))
    }

    func testDayOfWeekMatch() throws {
        // 2026-07-08 is a Wednesday (dow 3). "0 0 * * 5" = Friday → 2026-07-10 00:00.
        let next = try CronScheduleParser.getNextOccurrence("0 0 * * 5", after: date(2026, 7, 8, 12, 0))
        XCTAssertEqual(next, date(2026, 7, 10, 0, 0))
    }

    func testMonthAdvance() throws {
        // "0 0 1 1 *" = Jan 1 midnight → from mid-2026 the next is 2027-01-01.
        let next = try CronScheduleParser.getNextOccurrence("0 0 1 1 *", after: date(2026, 7, 8, 0, 0))
        XCTAssertEqual(next, date(2027, 1, 1, 0, 0))
    }

    func testInvalidFieldCountThrows() {
        XCTAssertThrowsError(try CronScheduleParser.getNextOccurrence("* * *", after: Date()))
    }

    func testImpossibleExpressionThrows() {
        // Feb 31 never exists.
        XCTAssertThrowsError(try CronScheduleParser.getNextOccurrence("0 0 31 2 *", after: Date()))
    }

    // ── CronJob copy ────────────────────────────────────────────────────────

    func testCronJobCopyPreservesUntouchedFields() {
        let job = CronJob(id: "a", name: "n", prompt: "p", cronExpression: "* * * * *", delivery: .local)
        let updated = job.copy(state: .succeeded)
        XCTAssertEqual(updated.id, "a")
        XCTAssertEqual(updated.state, .succeeded)
        XCTAssertEqual(updated.prompt, "p")
        XCTAssertTrue(updated.isEnabled)
    }

    // ── InMemoryScheduledTaskStore ──────────────────────────────────────────

    func testStoreUpsertGetDelete() async throws {
        let store = InMemoryScheduledTaskStore()
        let job = CronJob(id: "j1", name: "n", prompt: "p", cronExpression: "* * * * *", delivery: .local)
        _ = try await store.upsert(job)
        let got = try await store.get(id: "j1")
        XCTAssertEqual(got?.id, "j1")
        let listCount = try await store.list().count
        XCTAssertEqual(listCount, 1)
        try await store.delete(id: "j1")
        let afterDelete = try await store.get(id: "j1")
        XCTAssertNil(afterDelete)
    }

    func testDueJobsFiltersByEnabledAndNextRun() async throws {
        let store = InMemoryScheduledTaskStore()
        let past = Date().addingTimeInterval(-60)
        let future = Date().addingTimeInterval(3600)
        _ = try await store.upsert(CronJob(id: "due", name: "n", prompt: "p", cronExpression: "* * * * *", delivery: .local, nextRunUtc: past))
        _ = try await store.upsert(CronJob(id: "future", name: "n", prompt: "p", cronExpression: "* * * * *", delivery: .local, nextRunUtc: future))
        _ = try await store.upsert(CronJob(id: "disabled", name: "n", prompt: "p", cronExpression: "* * * * *", delivery: .local, nextRunUtc: past, isEnabled: false))
        _ = try await store.upsert(CronJob(id: "norun", name: "n", prompt: "p", cronExpression: "* * * * *", delivery: .local))
        let due = try await store.getDueJobs()
        XCTAssertEqual(due.map { $0.id }, ["due"])
    }

    // ── ScheduledAIService ──────────────────────────────────────────────────

    func testExecuteJobSucceedsAndFiresEvent() async throws {
        let butler = FakeButler(reply: "answer")
        let store = InMemoryScheduledTaskStore()
        let job = CronJob(id: "j", name: "n", prompt: "hello", cronExpression: "* * * * *", delivery: .local, nextRunUtc: Date().addingTimeInterval(-60))
        _ = try await store.upsert(job)

        let sched = ScheduledAIService(butler: butler, store: store)
        let box = EventBox()
        sched.onJobCompleted { args in box.record(args) }

        await sched.executeJob(job)

        let events = box.snapshot()
        XCTAssertEqual(events.count, 1)
        XCTAssertEqual(events[0].response, "answer")
        XCTAssertNil(events[0].error)
        XCTAssertEqual(events[0].job.state, .succeeded)
        XCTAssertNotNil(events[0].job.nextRunUtc)
        XCTAssertEqual(butler.asked, ["hello"])

        // Store now reflects the succeeded state.
        let stored = try await store.get(id: "j")
        XCTAssertEqual(stored?.state, .succeeded)
    }

    func testExecuteJobFailureSetsFailedState() async throws {
        let butler = FakeButler(reply: nil, throwsError: true)
        let store = InMemoryScheduledTaskStore()
        let job = CronJob(id: "j", name: "n", prompt: "hello", cronExpression: "* * * * *", delivery: .local)
        _ = try await store.upsert(job)

        let sched = ScheduledAIService(butler: butler, store: store)
        let box = EventBox()
        sched.onJobCompleted { args in box.record(args) }

        await sched.executeJob(job)
        let events = box.snapshot()
        XCTAssertEqual(events.count, 1)
        XCTAssertNotNil(events[0].error)
        XCTAssertEqual(events[0].job.state, .failed)
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    final class EventBox: @unchecked Sendable {
        private let lock = NSLock()
        private var events: [JobCompletedEventArgs] = []
        func record(_ e: JobCompletedEventArgs) { lock.lock(); events.append(e); lock.unlock() }
        func snapshot() -> [JobCompletedEventArgs] { lock.lock(); defer { lock.unlock() }; return events }
    }
}
