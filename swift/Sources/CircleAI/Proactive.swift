// Proactive.swift
//
// Port of the CircleAI.Companion.Proactive project — the proactive scheduling
// substrate. C# reference:
//   - Primitives.cs                        (ProactiveTask / Trigger / results)
//   - Contracts.cs                         (IProactiveTaskSource / Runner / Scheduler)
//   - CronExpression.cs                    (5-field cron parser)
//   - ProactiveScheduler.cs                (the cron tick loop + last-run tracking)
//   - NullImplementations.cs               (null / in-memory source + null/delegate runner)
//   - ProactiveSchedulerBackgroundService  (the once-a-minute tick loop)
//
// Three interfaces split cleanly so consumers can replace one without touching
// the others:
//   IProactiveTaskSource — where do tasks come from?
//   IProactiveTaskRunner — how do we execute one?
//   IProactiveScheduler  — when do they fire?
//
// In-memory + deterministic. The background loop uses Swift concurrency instead
// of a hosted service.

import Foundation

// =====================================================================
// Primitives
// =====================================================================

/// How a task fires. Exactly one of `cron`, `onEvent`, or `manual` is set.
/// - `cron`: 5-field cron expression — see `CronExpression`.
/// - `onEvent`: event name (e.g. "note-saved", "task-created").
/// - `manual`: true if the task only fires when explicitly invoked.
public struct ProactiveTrigger: Sendable, Equatable {
    public let cron: String?
    public let onEvent: String?
    public let manual: Bool

    public init(cron: String? = nil, onEvent: String? = nil, manual: Bool = false) {
        self.cron = cron
        self.onEvent = onEvent
        self.manual = manual
    }
}

/// One scheduled task. Opaque from the substrate's perspective — the host's
/// `IProactiveTaskRunner` reads the `payload` and executes it.
/// - `id`: unique task id within its source. Used for last-run tracking.
/// - `trigger`: cron / event / manual trigger.
/// - `payload`: consumer-owned object. Substrate never inspects it.
/// - `sourceContext`: optional context tag (vault path, tenant id, …) so
///   multi-tenant sources keep per-context last-run state separate.
///
/// C# models `Payload` as `object`; Swift uses an existential `Any`. Because
/// `Any` is not `Sendable`/`Equatable`, `ProactiveTask` identity for
/// bookkeeping is by (`sourceContext`, `id`) — the payload is never compared.
public struct ProactiveTask: @unchecked Sendable {
    public let id: String
    public let trigger: ProactiveTrigger
    public let payload: Any
    public let sourceContext: String?

    public init(id: String, trigger: ProactiveTrigger, payload: Any, sourceContext: String? = nil) {
        self.id = id
        self.trigger = trigger
        self.payload = payload
        self.sourceContext = sourceContext
    }
}

/// One run outcome — success or failure with a message.
public struct ProactiveTaskRunResult: Sendable, Equatable {
    public let taskId: String
    public let success: Bool
    public let failureMessage: String?

    public init(taskId: String, success: Bool, failureMessage: String? = nil) {
        self.taskId = taskId
        self.success = success
        self.failureMessage = failureMessage
    }
}

/// One parse failure surfaced through the source.
public struct ProactiveTaskLoadError: Sendable, Equatable {
    public let taskId: String
    public let message: String
    public let sourceContext: String?

    public init(taskId: String, message: String, sourceContext: String? = nil) {
        self.taskId = taskId
        self.message = message
        self.sourceContext = sourceContext
    }
}

// =====================================================================
// CronExpression
// =====================================================================

/// Errors raised while parsing a cron expression. Mirrors the C#
/// `FormatException` / `InvalidOperationException` cases.
public enum CronError: Error, Equatable {
    case format(String)
    case noMatch(String)
}

/// Five-field cron expression parser: `minute hour day-of-month month
/// day-of-week`. Supports `*`, integers, ranges (`1-5`), lists (`1,15,30`),
/// and step values (`*/15`). Day-of-week uses 0=Sunday … 6=Saturday.
///
/// Ported from CircleAI.Companion.Proactive.CronExpression. Day-of-month AND
/// day-of-week must both match (the C# reference settles on AND for
/// predictability).
public final class CronExpression: Sendable {
    private let minutes: Set<Int>
    private let hours: Set<Int>
    private let daysOfMonth: Set<Int>
    private let months: Set<Int>
    private let daysOfWeek: Set<Int>

    private init(minutes: Set<Int>, hours: Set<Int>, daysOfMonth: Set<Int>,
                 months: Set<Int>, daysOfWeek: Set<Int>) {
        self.minutes = minutes
        self.hours = hours
        self.daysOfMonth = daysOfMonth
        self.months = months
        self.daysOfWeek = daysOfWeek
    }

    public static func parse(_ expression: String) throws -> CronExpression {
        let fields = expression.split(whereSeparator: { $0 == " " })
            .map { String($0).trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
        if fields.count != 5 {
            throw CronError.format("Cron expression must have 5 fields, got \(fields.count): '\(expression)'")
        }
        return CronExpression(
            minutes: try parseField(fields[0], min: 0, max: 59),
            hours: try parseField(fields[1], min: 0, max: 23),
            daysOfMonth: try parseField(fields[2], min: 1, max: 31),
            months: try parseField(fields[3], min: 1, max: 12),
            daysOfWeek: try parseField(fields[4], min: 0, max: 6))
    }

    /// Next UTC time at or after `after` when the expression matches. Hard upper
    /// bound of one year forward — if nothing matches in 365 days the expression
    /// is effectively dead and we throw rather than spin.
    public func getNextOccurrence(after: Date) throws -> Date {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!

        // t = after + 1 minute, truncated to the minute.
        var t = after.addingTimeInterval(60)
        t = Self.truncateToMinute(t, cal: cal)
        guard let limit = cal.date(byAdding: .year, value: 1, to: t) else {
            throw CronError.noMatch("Cron expression does not match any time in the next year.")
        }
        while t <= limit {
            if matches(t) { return t }
            t = t.addingTimeInterval(60)
        }
        throw CronError.noMatch("Cron expression does not match any time in the next year.")
    }

    public func matches(_ moment: Date) -> Bool {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        let c = cal.dateComponents([.minute, .hour, .day, .month, .weekday], from: moment)
        guard let minute = c.minute, minutes.contains(minute) else { return false }
        guard let hour = c.hour, hours.contains(hour) else { return false }
        guard let day = c.day, daysOfMonth.contains(day) else { return false }
        guard let month = c.month, months.contains(month) else { return false }
        // Swift weekday: 1=Sunday … 7=Saturday → .NET DayOfWeek 0…6.
        let dow = (c.weekday ?? 1) - 1
        if !daysOfWeek.contains(dow) { return false }
        return true
    }

    private static func truncateToMinute(_ date: Date, cal: Calendar) -> Date {
        let c = cal.dateComponents([.year, .month, .day, .hour, .minute], from: date)
        return cal.date(from: c) ?? date
    }

    private static func parseField(_ field: String, min: Int, max: Int) throws -> Set<Int> {
        var values = Set<Int>()
        for part in field.split(separator: ",", omittingEmptySubsequences: false) {
            try expandPart(String(part).trimmingCharacters(in: .whitespaces), min: min, max: max, sink: &values)
        }
        if values.isEmpty {
            throw CronError.format("Cron field '\(field)' resolved to no values.")
        }
        return values
    }

    private static func expandPart(_ part: String, min: Int, max: Int, sink: inout Set<Int>) throws {
        var part = part
        var step = 1
        if let slashIdx = part.firstIndex(of: "/") {
            let stepStr = String(part[part.index(after: slashIdx)...])
            guard let s = Int(stepStr), s > 0 else {
                throw CronError.format("Cron step '\(part)' is not a positive integer.")
            }
            step = s
            part = String(part[..<slashIdx])
        }

        let rangeStart: Int
        let rangeEnd: Int
        if part == "*" {
            rangeStart = min
            rangeEnd = max
        } else if part.contains("-") {
            let dashIdx = part.firstIndex(of: "-")!
            guard let a = Int(part[..<dashIdx]),
                  let b = Int(part[part.index(after: dashIdx)...]) else {
                throw CronError.format("Cron part '\(part)' out of range [\(min),\(max)].")
            }
            rangeStart = a
            rangeEnd = b
        } else {
            guard let v = Int(part) else {
                throw CronError.format("Cron part '\(part)' out of range [\(min),\(max)].")
            }
            rangeStart = v
            rangeEnd = v
        }

        if rangeStart < min || rangeEnd > max || rangeStart > rangeEnd {
            throw CronError.format("Cron part '\(part)' out of range [\(min),\(max)].")
        }

        var v = rangeStart
        while v <= rangeEnd {
            sink.insert(v)
            v += step
        }
    }
}

// =====================================================================
// Contracts
// =====================================================================

/// Where the active set of tasks comes from. Refreshed via `getTasks` on every
/// scheduler refresh / tick.
public protocol IProactiveTaskSource: AnyObject, Sendable {
    /// Backend self-identification — "vault-fs", "in-memory", "null".
    var backendId: String { get }
    /// Snapshot the current set of tasks.
    func getTasks() async throws -> [ProactiveTask]
    /// Any parse / load failures surfaced from the last refresh.
    func getErrors() async throws -> [ProactiveTaskLoadError]
}

/// Executes one task. The substrate hands the task back; the consumer reads
/// `ProactiveTask.payload` and runs it.
public protocol IProactiveTaskRunner: AnyObject, Sendable {
    /// Backend self-identification — "workflow-engine", "delegate", "null".
    var backendId: String { get }
    /// Execute one task. `variables` carry trigger-time context the runner can
    /// substitute into prompts or pass through.
    func run(task: ProactiveTask, variables: [String: String]?) async throws -> ProactiveTaskRunResult
}

/// The scheduling loop. Owns cron parsing + last-run tracking + event dispatch.
public protocol IProactiveScheduler: AnyObject, Sendable {
    /// Backend self-identification.
    var backendId: String { get }
    /// Current snapshot — populated by `refresh`.
    var tasks: [ProactiveTask] { get }
    /// Any load errors from the source.
    var loadErrors: [ProactiveTaskLoadError] { get }
    /// Next cron firing for a task. Returns nil for non-cron triggers or
    /// unparseable expressions.
    func getNextRun(task: ProactiveTask, after: Date) -> Date?
    /// Re-snapshot tasks from the source. Drops state for tasks the source no
    /// longer reports; leaves last-run state for surviving tasks intact.
    func refresh() async throws
    /// Tick. Run every task whose cron next-run is at-or-before `now` and that
    /// hasn't already fired for the matching minute.
    func tick(now: Date) async throws
    /// Fire every event-triggered task matching the event name.
    func dispatchEvent(eventName: String, variables: [String: String]?) async throws
    /// One-shot manual run by task id.
    func runById(id: String, variables: [String: String]?) async throws -> ProactiveTaskRunResult
}

// =====================================================================
// ProactiveScheduler
// =====================================================================

/// Default `IProactiveScheduler`. Owns cron parsing, last-run tracking,
/// refresh, and event dispatch. Calls into a host-supplied source (what tasks
/// exist) and runner (how to execute one). Per-context (`sourceContext`)
/// last-run tracking is preserved so multi-tenant hosts keep tenants' schedules
/// separate. Ported from `ProactiveScheduler`.
public final class ProactiveScheduler: IProactiveScheduler, @unchecked Sendable {
    private let source: IProactiveTaskSource
    private let runner: IProactiveTaskRunner

    private let gate = NSLock()
    private var taskList: [ProactiveTask] = []
    private var errorList: [ProactiveTaskLoadError] = []
    // Per-(context, taskId) last-run map. Context = sourceContext or "".
    private var lastRuns: [String: [String: Date]] = [:]

    public init(source: IProactiveTaskSource, runner: IProactiveTaskRunner) {
        self.source = source
        self.runner = runner
    }

    public var backendId: String { "default" }

    public var tasks: [ProactiveTask] {
        gate.lock(); defer { gate.unlock() }
        return taskList
    }

    public var loadErrors: [ProactiveTaskLoadError] {
        gate.lock(); defer { gate.unlock() }
        return errorList
    }

    public func getNextRun(task: ProactiveTask, after: Date) -> Date? {
        guard let cron = task.trigger.cron else { return nil }
        do {
            let expr = try CronExpression.parse(cron)
            return try expr.getNextOccurrence(after: after)
        } catch {
            return nil
        }
    }

    public func refresh() async throws {
        let snapshot = try await source.getTasks()
        let errors = try await source.getErrors()

        gate.lock(); defer { gate.unlock() }
        taskList = snapshot
        errorList = errors

        // Drop last-run state for (context, taskId) pairs the source no longer
        // reports — prevents memory growth when tasks come and go.
        var live = Set<String>()
        for t in taskList { live.insert(Self.liveKey(ctx: Self.contextKey(t.sourceContext), id: t.id)) }

        for ctxKey in Array(lastRuns.keys) {
            guard var ids = lastRuns[ctxKey] else { continue }
            for id in Array(ids.keys) {
                if !live.contains(Self.liveKey(ctx: ctxKey, id: id)) {
                    ids[id] = nil
                }
            }
            if ids.isEmpty {
                lastRuns[ctxKey] = nil
            } else {
                lastRuns[ctxKey] = ids
            }
        }
    }

    public func tick(now: Date) async throws {
        gate.lock()
        let candidates = taskList.filter { $0.trigger.cron != nil }
        gate.unlock()

        for task in candidates {
            try Task.checkCancellation()

            let ctxKey = Self.contextKey(task.sourceContext)
            gate.lock()
            let lastRun = lastRuns[ctxKey]?[task.id] ?? Date.distantPast
            gate.unlock()

            do {
                let expr = try CronExpression.parse(task.trigger.cron!)
                let anchor = lastRun == Date.distantPast ? now.addingTimeInterval(-60) : lastRun
                let next = try expr.getNextOccurrence(after: anchor)
                if next <= now {
                    _ = try await runner.run(task: task, variables: nil)
                    markRun(task: task, when: now)
                }
            } catch {
                // Parse error — already surfaced via loadErrors at the source
                // layer. Skip this task; don't crash the tick.
            }
        }
    }

    public func dispatchEvent(eventName: String, variables: [String: String]?) async throws {
        precondition(!eventName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "eventName required")

        gate.lock()
        let matched = taskList.filter {
            $0.trigger.onEvent?.caseInsensitiveCompare(eventName) == .orderedSame
        }
        gate.unlock()

        for task in matched {
            try Task.checkCancellation()
            _ = try await runner.run(task: task, variables: variables)
            markRun(task: task, when: Date())
        }
    }

    public func runById(id: String, variables: [String: String]?) async throws -> ProactiveTaskRunResult {
        precondition(!id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "id required")

        gate.lock()
        let task = taskList.first { $0.id.caseInsensitiveCompare(id) == .orderedSame }
        gate.unlock()

        guard let task else {
            return ProactiveTaskRunResult(taskId: id, success: false, failureMessage: "No task with id '\(id)'.")
        }
        let result = try await runner.run(task: task, variables: variables)
        markRun(task: task, when: Date())
        return result
    }

    private func markRun(task: ProactiveTask, when: Date) {
        let ctxKey = Self.contextKey(task.sourceContext)
        gate.lock(); defer { gate.unlock() }
        lastRuns[ctxKey, default: [:]][task.id] = when
    }

    private static func contextKey(_ sourceContext: String?) -> String {
        sourceContext ?? ""
    }

    // Case-insensitive composite key for the "live" set (mirrors the C#
    // HashSet<(Ctx, Id)> populated from lower-cased comparisons).
    private static func liveKey(ctx: String, id: String) -> String {
        ctx.lowercased() + "\u{1}" + id.lowercased()
    }
}

// =====================================================================
// Null / in-memory implementations
// =====================================================================

/// Empty source — no tasks, no errors. Ported from `NullProactiveTaskSource`.
public final class NullProactiveTaskSource: IProactiveTaskSource, @unchecked Sendable {
    public static let instance = NullProactiveTaskSource()
    public init() {}
    public var backendId: String { "null" }
    public func getTasks() async throws -> [ProactiveTask] { [] }
    public func getErrors() async throws -> [ProactiveTaskLoadError] { [] }
}

/// Reports every run as a failure with a "no runner registered" message.
/// Fail-closed default so a host that forgot to wire a real runner notices on
/// first scheduled fire. Ported from `NullProactiveTaskRunner`.
public final class NullProactiveTaskRunner: IProactiveTaskRunner, @unchecked Sendable {
    public static let instance = NullProactiveTaskRunner()
    public init() {}
    public var backendId: String { "null" }
    public func run(task: ProactiveTask, variables: [String: String]?) async throws -> ProactiveTaskRunResult {
        ProactiveTaskRunResult(taskId: task.id, success: false,
                               failureMessage: "No IProactiveTaskRunner registered; using NullProactiveTaskRunner.")
    }
}

/// In-memory source for testing + simple consumers. Add / remove tasks; the
/// scheduler picks up changes on next `refresh`. Keyed by (sourceContext, id)
/// so multi-tenant hosts can hold the same task id in two contexts without
/// collision. Ported from `InMemoryProactiveTaskSource`.
public final class InMemoryProactiveTaskSource: IProactiveTaskSource, @unchecked Sendable {
    private let gate = NSLock()
    // (ctxLower, idLower) → task ; preserves the case-insensitive keying of the C# comparer.
    private var byKey: [String: ProactiveTask] = [:]
    private var errors: [ProactiveTaskLoadError] = []

    public init() {}

    public var backendId: String { "in-memory" }

    public func upsert(_ task: ProactiveTask) {
        gate.lock(); byKey[Self.key(task)] = task; gate.unlock()
    }

    @discardableResult
    public func remove(id: String, sourceContext: String? = nil) -> Bool {
        precondition(!id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "id required")
        gate.lock(); defer { gate.unlock() }
        return byKey.removeValue(forKey: Self.key(ctx: sourceContext ?? "", id: id)) != nil
    }

    public func clear() {
        gate.lock(); byKey.removeAll(); errors.removeAll(); gate.unlock()
    }

    public func recordError(_ error: ProactiveTaskLoadError) {
        gate.lock(); errors.append(error); gate.unlock()
    }

    public func getTasks() async throws -> [ProactiveTask] {
        gate.lock(); defer { gate.unlock() }
        return Array(byKey.values)
    }

    public func getErrors() async throws -> [ProactiveTaskLoadError] {
        gate.lock(); defer { gate.unlock() }
        return errors
    }

    private static func key(_ task: ProactiveTask) -> String {
        key(ctx: task.sourceContext ?? "", id: task.id)
    }
    private static func key(ctx: String, id: String) -> String {
        ctx.lowercased() + "\u{1}" + id.lowercased()
    }
}

/// Runner that hands every task off to a host-supplied closure. Ported from
/// `DelegateProactiveTaskRunner`.
public final class DelegateProactiveTaskRunner: IProactiveTaskRunner, @unchecked Sendable {
    public typealias Handler = @Sendable (_ task: ProactiveTask, _ variables: [String: String]?) async -> ProactiveTaskRunResult
    private let handler: Handler

    public init(handler: @escaping Handler) {
        self.handler = handler
    }

    public var backendId: String { "delegate" }

    public func run(task: ProactiveTask, variables: [String: String]?) async throws -> ProactiveTaskRunResult {
        await handler(task, variables)
    }
}

// =====================================================================
// Background tick loop
// =====================================================================

/// Tunable knobs for the background tick loop. Ported from
/// `ProactiveSchedulerOptions`.
public struct ProactiveSchedulerOptions: Sendable {
    /// How often the scheduler ticks. Default 1 minute.
    public var tickInterval: TimeInterval
    /// How often the source is re-snapshotted. Default 5 minutes.
    public var refreshInterval: TimeInterval

    public init(tickInterval: TimeInterval = 60, refreshInterval: TimeInterval = 300) {
        self.tickInterval = tickInterval
        self.refreshInterval = refreshInterval
    }
}

/// Background service that calls `refresh` once at startup, then loops on a
/// timer calling `tick`. Swift-concurrency equivalent of
/// `ProactiveSchedulerBackgroundService` — start it with `run(stoppingToken:)`
/// inside a `Task`; cancel that task to stop.
public final class ProactiveSchedulerBackgroundService: @unchecked Sendable {
    private let scheduler: IProactiveScheduler
    private let options: ProactiveSchedulerOptions

    public init(scheduler: IProactiveScheduler, options: ProactiveSchedulerOptions = ProactiveSchedulerOptions()) {
        self.scheduler = scheduler
        self.options = options
    }

    /// Runs the refresh-then-tick loop until the surrounding task is cancelled.
    public func run() async {
        // Initial refresh — populate the scheduler before the first tick.
        do {
            try await scheduler.refresh()
        } catch is CancellationError {
            return
        } catch {
            // Initial refresh failed; continue into the loop and retry.
        }

        var lastRefresh = Date()

        while !Task.isCancelled {
            do {
                try await Task.sleep(nanoseconds: UInt64(options.tickInterval * 1_000_000_000))
            } catch {
                return // cancelled
            }

            let now = Date()
            do {
                if now.timeIntervalSince(lastRefresh) >= options.refreshInterval {
                    try await scheduler.refresh()
                    lastRefresh = now
                }
                try await scheduler.tick(now: now)
            } catch is CancellationError {
                return
            } catch {
                // Tick failed; retry on next interval.
            }
        }
    }
}
