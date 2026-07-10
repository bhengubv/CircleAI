// ProactiveSchedulerTests.swift
//
// Verifies the proactive scheduling substrate ported in Proactive.swift:
// in-memory source upsert/remove, null runner fail-closed, delegate runner,
// scheduler refresh + next-run + cron tick firing + de-dup, event dispatch, and
// manual run-by-id. Mirrors the behaviour of the C# ProactiveScheduler.

import XCTest
@testable import CircleAI

final class ProactiveSchedulerTests: XCTestCase {

    private var utc: Calendar = {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        return c
    }()
    private func date(_ y: Int, _ mo: Int, _ d: Int, _ h: Int, _ mi: Int) -> Date {
        utc.date(from: DateComponents(year: y, month: mo, day: d, hour: h, minute: mi, second: 0))!
    }

    /// A runner that records which task ids it ran.
    final class RecordingRunner: IProactiveTaskRunner, @unchecked Sendable {
        private let lock = NSLock()
        private(set) var ran: [String] = []
        var backendId: String { "recording" }
        func run(task: ProactiveTask, variables: [String: String]?) async throws -> ProactiveTaskRunResult {
            lock.lock(); ran.append(task.id); lock.unlock()
            return ProactiveTaskRunResult(taskId: task.id, success: true)
        }
    }

    // ── Source ────────────────────────────────────────────────────────────
    func testInMemorySourceUpsertRemove() async throws {
        let src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask(id: "t1", trigger: ProactiveTrigger(cron: "* * * * *"), payload: "p"))
        var tasks = try await src.getTasks()
        XCTAssertEqual(tasks.count, 1)
        XCTAssertTrue(src.remove(id: "t1"))
        tasks = try await src.getTasks()
        XCTAssertTrue(tasks.isEmpty)
    }

    func testInMemorySourceMultiTenantKeying() async throws {
        let src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask(id: "t", trigger: ProactiveTrigger(manual: true), payload: "a", sourceContext: "tenant1"))
        src.upsert(ProactiveTask(id: "t", trigger: ProactiveTrigger(manual: true), payload: "b", sourceContext: "tenant2"))
        let tasks = try await src.getTasks()
        XCTAssertEqual(tasks.count, 2, "same id in two contexts must not collide")
    }

    func testNullRunnerFailsClosed() async throws {
        let r = NullProactiveTaskRunner.instance
        let result = try await r.run(task: ProactiveTask(id: "x", trigger: ProactiveTrigger(manual: true), payload: 0), variables: nil)
        XCTAssertFalse(result.success)
        XCTAssertNotNil(result.failureMessage)
    }

    // ── Scheduler refresh + next-run ──────────────────────────────────────
    func testSchedulerRefreshPopulatesTasks() async throws {
        let src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask(id: "t1", trigger: ProactiveTrigger(cron: "0 6 * * *"), payload: "p"))
        let sched = ProactiveScheduler(source: src, runner: RecordingRunner())
        try await sched.refresh()
        XCTAssertEqual(sched.tasks.count, 1)
        XCTAssertEqual(sched.backendId, "default")
    }

    func testGetNextRunForCron() async throws {
        let src = InMemoryProactiveTaskSource()
        let task = ProactiveTask(id: "t1", trigger: ProactiveTrigger(cron: "30 6 * * *"), payload: "p")
        src.upsert(task)
        let sched = ProactiveScheduler(source: src, runner: RecordingRunner())
        let next = sched.getNextRun(task: task, after: date(2026, 7, 8, 6, 0))
        XCTAssertEqual(next, date(2026, 7, 8, 6, 30))
    }

    func testGetNextRunNilForNonCron() async throws {
        let sched = ProactiveScheduler(source: NullProactiveTaskSource.instance, runner: RecordingRunner())
        let manual = ProactiveTask(id: "m", trigger: ProactiveTrigger(manual: true), payload: 0)
        XCTAssertNil(sched.getNextRun(task: manual, after: Date()))
    }

    // ── Tick firing ───────────────────────────────────────────────────────
    func testTickFiresDueCronTask() async throws {
        let src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask(id: "t1", trigger: ProactiveTrigger(cron: "* * * * *"), payload: "p"))
        let runner = RecordingRunner()
        let sched = ProactiveScheduler(source: src, runner: runner)
        try await sched.refresh()
        // Every-minute task: at any minute, next-run from (now-1min) is <= now.
        try await sched.tick(now: date(2026, 7, 8, 6, 0))
        XCTAssertEqual(runner.ran, ["t1"])
    }

    func testTickDeDupesWithinSameMinute() async throws {
        let src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask(id: "t1", trigger: ProactiveTrigger(cron: "* * * * *"), payload: "p"))
        let runner = RecordingRunner()
        let sched = ProactiveScheduler(source: src, runner: runner)
        try await sched.refresh()
        let now = date(2026, 7, 8, 6, 0)
        try await sched.tick(now: now)
        try await sched.tick(now: now) // same minute → should NOT fire again
        XCTAssertEqual(runner.ran, ["t1"], "task should fire once per due minute")
    }

    func testTickFiresAgainNextMinute() async throws {
        let src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask(id: "t1", trigger: ProactiveTrigger(cron: "* * * * *"), payload: "p"))
        let runner = RecordingRunner()
        let sched = ProactiveScheduler(source: src, runner: runner)
        try await sched.refresh()
        try await sched.tick(now: date(2026, 7, 8, 6, 0))
        try await sched.tick(now: date(2026, 7, 8, 6, 1))
        XCTAssertEqual(runner.ran, ["t1", "t1"])
    }

    func testTickIgnoresNonCronTasks() async throws {
        let src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask(id: "m", trigger: ProactiveTrigger(manual: true), payload: 0))
        let runner = RecordingRunner()
        let sched = ProactiveScheduler(source: src, runner: runner)
        try await sched.refresh()
        try await sched.tick(now: Date())
        XCTAssertTrue(runner.ran.isEmpty)
    }

    // ── Event dispatch ────────────────────────────────────────────────────
    func testDispatchEventFiresMatching() async throws {
        let src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask(id: "e1", trigger: ProactiveTrigger(onEvent: "note-saved"), payload: "p"))
        src.upsert(ProactiveTask(id: "e2", trigger: ProactiveTrigger(onEvent: "other"), payload: "p"))
        let runner = RecordingRunner()
        let sched = ProactiveScheduler(source: src, runner: runner)
        try await sched.refresh()
        try await sched.dispatchEvent(eventName: "note-saved", variables: nil)
        XCTAssertEqual(runner.ran, ["e1"])
    }

    func testDispatchEventCaseInsensitive() async throws {
        let src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask(id: "e1", trigger: ProactiveTrigger(onEvent: "Note-Saved"), payload: "p"))
        let runner = RecordingRunner()
        let sched = ProactiveScheduler(source: src, runner: runner)
        try await sched.refresh()
        try await sched.dispatchEvent(eventName: "note-saved", variables: nil)
        XCTAssertEqual(runner.ran, ["e1"])
    }

    // ── Run by id ─────────────────────────────────────────────────────────
    func testRunByIdSuccess() async throws {
        let src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask(id: "m1", trigger: ProactiveTrigger(manual: true), payload: "p"))
        let runner = RecordingRunner()
        let sched = ProactiveScheduler(source: src, runner: runner)
        try await sched.refresh()
        let result = try await sched.runById(id: "m1", variables: ["k": "v"])
        XCTAssertTrue(result.success)
        XCTAssertEqual(runner.ran, ["m1"])
    }

    func testRunByIdUnknown() async throws {
        let sched = ProactiveScheduler(source: NullProactiveTaskSource.instance, runner: RecordingRunner())
        try await sched.refresh()
        let result = try await sched.runById(id: "nope", variables: nil)
        XCTAssertFalse(result.success)
        XCTAssertEqual(result.failureMessage, "No task with id 'nope'.")
    }

    // ── Delegate runner ───────────────────────────────────────────────────
    func testDelegateRunner() async throws {
        let runner = DelegateProactiveTaskRunner { task, _ in
            ProactiveTaskRunResult(taskId: task.id, success: true, failureMessage: "handled")
        }
        let r = try await runner.run(task: ProactiveTask(id: "z", trigger: ProactiveTrigger(manual: true), payload: 0), variables: nil)
        XCTAssertTrue(r.success)
        XCTAssertEqual(r.failureMessage, "handled")
        XCTAssertEqual(runner.backendId, "delegate")
    }

    // ── Refresh drops stale last-run state (no crash / no leak) ────────────
    func testRefreshDropsRemovedTasks() async throws {
        let src = InMemoryProactiveTaskSource()
        src.upsert(ProactiveTask(id: "t1", trigger: ProactiveTrigger(cron: "* * * * *"), payload: "p"))
        let runner = RecordingRunner()
        let sched = ProactiveScheduler(source: src, runner: runner)
        try await sched.refresh()
        try await sched.tick(now: date(2026, 7, 8, 6, 0))
        // Remove the task, refresh, and re-add: last-run state was dropped so it
        // fires again on the next tick for the same minute.
        _ = src.remove(id: "t1")
        try await sched.refresh()
        src.upsert(ProactiveTask(id: "t1", trigger: ProactiveTrigger(cron: "* * * * *"), payload: "p"))
        try await sched.refresh()
        try await sched.tick(now: date(2026, 7, 8, 6, 0))
        XCTAssertEqual(runner.ran, ["t1", "t1"])
    }
}
