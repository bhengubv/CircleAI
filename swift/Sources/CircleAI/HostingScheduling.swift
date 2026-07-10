// HostingScheduling.swift
//
// Port of the CircleAI.Hosting scheduled-task substrate (Track 3):
//   - CronScheduleParser.cs    → CronScheduleParser (5-field cron next-occurrence)
//   - CronJobModels.cs         → DeliveryTarget / CronJobState / CronJob
//   - IScheduledTaskStore.cs   → IScheduledTaskStore
//   - InMemoryScheduledTaskStore.cs → InMemoryScheduledTaskStore
//   - ScheduledAIService.cs    → ScheduledAIService + JobCompletedEventArgs
//
// The cron parser is real, deterministic, and computes the earliest UTC
// timestamp STRICTLY after the reference time — matching the C# reference
// (advance-to-next-month / -hour / -day helpers included).

import Foundation

// =====================================================================
// CronScheduleParser
// =====================================================================

/// Errors raised while parsing a cron expression. Mirrors the C#
/// `ArgumentException` / `InvalidOperationException` cases.
public enum CronScheduleError: Error, Equatable {
    case invalidExpression(String)
    case noOccurrence(String)
}

/// Computes the next occurrence of a 5-field cron expression strictly after a
/// given `Date`. Handles wildcards, lists, steps, and ranges.
///
/// Field order: minute (0-59), hour (0-23), day-of-month (1-31), month (1-12),
/// day-of-week (0-6, 0=Sunday). Ported from `CronScheduleParser`.
public enum CronScheduleParser {

    private static func utcCalendar() -> Calendar {
        var c = Calendar(identifier: .gregorian)
        c.timeZone = TimeZone(identifier: "UTC")!
        return c
    }

    /// Returns the earliest UTC timestamp strictly after `after` that satisfies
    /// `cronExpression`.
    public static func getNextOccurrence(_ cronExpression: String, after: Date) throws -> Date {
        let trimmed = cronExpression.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            throw CronScheduleError.invalidExpression("Cron expression must not be empty.")
        }
        let parts = trimmed.split(separator: " ", omittingEmptySubsequences: true).map(String.init)
        if parts.count != 5 {
            throw CronScheduleError.invalidExpression(
                "Cron expression must have exactly 5 fields, got \(parts.count): '\(cronExpression)'")
        }

        let minuteSet = try parseField(parts[0], min: 0, max: 59)
        let hourSet   = try parseField(parts[1], min: 0, max: 23)
        let domSet    = try parseField(parts[2], min: 1, max: 31)
        let monthSet  = try parseField(parts[3], min: 1, max: 12)
        let dowSet    = try parseField(parts[4], min: 0, max: 6)

        let cal = utcCalendar()

        // Start searching from the next whole minute after `after`.
        var candidate = truncateToMinute(after, cal: cal).addingTimeInterval(60)

        // Cap iteration to 5 years to guard impossible expressions (e.g. Feb 31).
        guard let limit = cal.date(byAdding: .year, value: 5, to: candidate) else {
            throw CronScheduleError.noOccurrence(
                "No occurrence found within 5 years for cron expression '\(cronExpression)'.")
        }

        while candidate <= limit {
            let c = cal.dateComponents([.minute, .hour, .day, .month, .weekday], from: candidate)
            let month = c.month!
            // Month check.
            if !monthSet.contains(month) {
                candidate = try advanceToNextMonth(candidate, monthSet: monthSet, cal: cal)
                continue
            }
            // Day-of-month check.
            if !domSet.contains(c.day!) {
                candidate = startOfNextDay(candidate, cal: cal)
                continue
            }
            // Day-of-week check. Swift weekday 1=Sunday…7=Saturday → .NET 0…6.
            let dow = (c.weekday! - 1)
            if !dowSet.contains(dow) {
                candidate = startOfNextDay(candidate, cal: cal)
                continue
            }
            // Hour check.
            if !hourSet.contains(c.hour!) {
                candidate = try advanceToNextHour(candidate, hourSet: hourSet, cal: cal)
                continue
            }
            // Minute check.
            if !minuteSet.contains(c.minute!) {
                candidate = candidate.addingTimeInterval(60)
                continue
            }
            // All fields match.
            return candidate
        }

        throw CronScheduleError.noOccurrence(
            "No occurrence found within 5 years for cron expression '\(cronExpression)'.")
    }

    // ------------------------------------------------------------------
    // Parsing helpers
    // ------------------------------------------------------------------

    private static func parseField(_ field: String, min: Int, max: Int) throws -> Set<Int> {
        var result = Set<Int>()
        for part in field.split(separator: ",", omittingEmptySubsequences: false) {
            try parsePart(String(part).trimmingCharacters(in: .whitespaces), min: min, max: max, into: &result)
        }
        return result
    }

    private static func parsePart(_ part: String, min: Int, max: Int, into result: inout Set<Int>) throws {
        var step: Int? = nil
        var core = part

        if let slashIdx = part.firstIndex(of: "/") {
            let stepStr = String(part[part.index(after: slashIdx)...])
            guard let s = Int(stepStr), s >= 1 else {
                throw CronScheduleError.invalidExpression("Invalid step in cron field part '\(part)'.")
            }
            step = s
            core = String(part[..<slashIdx])
        }

        let rangeMin: Int
        let rangeMax: Int

        if core == "*" {
            rangeMin = min
            rangeMax = max
        } else if let dashIdx = core.firstIndex(of: "-") {
            guard let a = Int(core[..<dashIdx]),
                  let b = Int(core[core.index(after: dashIdx)...]) else {
                throw CronScheduleError.invalidExpression("Invalid range in cron field part '\(part)'.")
            }
            rangeMin = a
            rangeMax = b
        } else {
            guard let v = Int(core) else {
                throw CronScheduleError.invalidExpression("Invalid value in cron field part '\(part)'.")
            }
            rangeMin = v
            rangeMax = v
        }

        if rangeMin < min || rangeMax > max || rangeMin > rangeMax {
            throw CronScheduleError.invalidExpression(
                "Cron field value \(rangeMin)-\(rangeMax) out of range [\(min),\(max)].")
        }

        let effectiveStep = step ?? 1
        var v = rangeMin
        while v <= rangeMax {
            result.insert(v)
            v += effectiveStep
        }
    }

    // ------------------------------------------------------------------
    // Advancement helpers
    // ------------------------------------------------------------------

    private static func truncateToMinute(_ date: Date, cal: Calendar) -> Date {
        let c = cal.dateComponents([.year, .month, .day, .hour, .minute], from: date)
        return cal.date(from: c) ?? date
    }

    private static func startOfNextDay(_ date: Date, cal: Calendar) -> Date {
        // Next day at 00:00 UTC (mirrors AddDays(1).Date()).
        let midnight = cal.startOfDay(for: date)
        return cal.date(byAdding: .day, value: 1, to: midnight) ?? date.addingTimeInterval(86_400)
    }

    private static func advanceToNextMonth(_ date: Date, monthSet: Set<Int>, cal: Calendar) throws -> Date {
        let comps = cal.dateComponents([.year, .month], from: date)
        var year = comps.year!
        var month = comps.month! + 1
        if month > 12 { month = 1; year += 1 }

        let startYear = comps.year!
        while year < startYear + 6 {
            if monthSet.contains(month) {
                var dc = DateComponents()
                dc.year = year; dc.month = month; dc.day = 1
                dc.hour = 0; dc.minute = 0; dc.second = 0
                if let d = cal.date(from: dc) { return d }
            }
            month += 1
            if month > 12 { month = 1; year += 1 }
        }
        throw CronScheduleError.noOccurrence("No valid month found in cron expression.")
    }

    private static func advanceToNextHour(_ date: Date, hourSet: Set<Int>, cal: Calendar) throws -> Date {
        let comps = cal.dateComponents([.year, .month, .day, .hour], from: date)
        // Try subsequent hours today.
        var h = comps.hour! + 1
        while h <= 23 {
            if hourSet.contains(h) {
                var dc = DateComponents()
                dc.year = comps.year; dc.month = comps.month; dc.day = comps.day
                dc.hour = h; dc.minute = 0; dc.second = 0
                if let d = cal.date(from: dc) { return d }
            }
            h += 1
        }
        // No valid hour today — move to next day, first valid hour.
        let nextDay = startOfNextDay(date, cal: cal)
        let ndComps = cal.dateComponents([.year, .month, .day], from: nextDay)
        let minHour = hourSet.min() ?? 0
        var dc = DateComponents()
        dc.year = ndComps.year; dc.month = ndComps.month; dc.day = ndComps.day
        dc.hour = minHour; dc.minute = 0; dc.second = 0
        return cal.date(from: dc) ?? nextDay
    }
}

// =====================================================================
// CronJob models
// =====================================================================

/// Delivery channel for a scheduled job's output. Ported from `DeliveryTarget`.
public enum DeliveryTarget: Int, Sendable, CaseIterable {
    /// Deliver via in-process observer callback.
    case local = 0
    /// Deliver via push notification (requires IPushNotificationSender).
    case push
    /// Deliver as a Telegram message (requires webhook config).
    case telegram
    /// Deliver via email (requires SMTP config).
    case email
    /// Caller handles delivery via custom callback.
    case custom
}

/// State of a scheduled job's last execution. Ported from `CronJobState`.
public enum CronJobState: Int, Sendable, CaseIterable {
    /// Job has never run.
    case pending = 0
    /// Job is currently executing.
    case running
    /// Last run completed without error.
    case succeeded
    /// Last run threw an error or the model returned an error.
    case failed
    /// Job has been manually paused and will not fire until re-enabled.
    case paused
}

/// A named, recurring B! task with a cron schedule. Value type mirroring the
/// C# `record CronJob`; `with`-style copies are done via `copy(...)`.
public struct CronJob: Sendable, Equatable {
    public let id: String
    public let name: String
    public let prompt: String
    public let cronExpression: String
    public let delivery: DeliveryTarget
    public let lastRunUtc: Date?
    public let nextRunUtc: Date?
    public let state: CronJobState
    public let isEnabled: Bool

    public init(
        id: String,
        name: String,
        prompt: String,
        cronExpression: String,
        delivery: DeliveryTarget,
        lastRunUtc: Date? = nil,
        nextRunUtc: Date? = nil,
        state: CronJobState = .pending,
        isEnabled: Bool = true
    ) {
        self.id = id
        self.name = name
        self.prompt = prompt
        self.cronExpression = cronExpression
        self.delivery = delivery
        self.lastRunUtc = lastRunUtc
        self.nextRunUtc = nextRunUtc
        self.state = state
        self.isEnabled = isEnabled
    }

    /// Non-destructive mutation mirroring C# `job with { ... }`.
    public func copy(
        lastRunUtc: Date?? = nil,
        nextRunUtc: Date?? = nil,
        state: CronJobState? = nil,
        isEnabled: Bool? = nil
    ) -> CronJob {
        CronJob(
            id: id,
            name: name,
            prompt: prompt,
            cronExpression: cronExpression,
            delivery: delivery,
            lastRunUtc: lastRunUtc ?? self.lastRunUtc,
            nextRunUtc: nextRunUtc ?? self.nextRunUtc,
            state: state ?? self.state,
            isEnabled: isEnabled ?? self.isEnabled)
    }
}

// =====================================================================
// IScheduledTaskStore + in-memory implementation
// =====================================================================

/// Persistence abstraction for `CronJob` records. All operations are async and
/// must be thread-safe. Ported from `IScheduledTaskStore`.
public protocol IScheduledTaskStore: AnyObject, Sendable {
    /// Every registered job, regardless of enabled/disabled state.
    func list() async throws -> [CronJob]
    /// The job with the given id, or nil if not found.
    func get(id: String) async throws -> CronJob?
    /// Inserts or replaces the job identified by `CronJob.id`. Returns the stored record.
    @discardableResult
    func upsert(_ job: CronJob) async throws -> CronJob
    /// Removes the job with the given id. No-op if it does not exist.
    func delete(id: String) async throws
    /// All enabled jobs whose `nextRunUtc` is at or before now.
    func getDueJobs() async throws -> [CronJob]
}

/// Thread-safe, in-memory `IScheduledTaskStore`. All state is lost on process
/// exit. Ported from `InMemoryScheduledTaskStore`.
public final class InMemoryScheduledTaskStore: IScheduledTaskStore, @unchecked Sendable {
    private let lock = NSLock()
    private var store: [String: CronJob] = [:]

    public init() {}

    public func list() async throws -> [CronJob] {
        lock.lock(); defer { lock.unlock() }
        return Array(store.values)
    }

    public func get(id: String) async throws -> CronJob? {
        precondition(!id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "id required")
        lock.lock(); defer { lock.unlock() }
        return store[id]
    }

    @discardableResult
    public func upsert(_ job: CronJob) async throws -> CronJob {
        lock.lock(); store[job.id] = job; lock.unlock()
        return job
    }

    public func delete(id: String) async throws {
        precondition(!id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "id required")
        lock.lock(); store[id] = nil; lock.unlock()
    }

    public func getDueJobs() async throws -> [CronJob] {
        let now = Date()
        lock.lock(); let snapshot = Array(store.values); lock.unlock()
        return snapshot.filter { j in
            guard j.isEnabled, let next = j.nextRunUtc else { return false }
            return next <= now
        }
    }
}

// =====================================================================
// ScheduledAIService
// =====================================================================

/// Data emitted when a scheduled job finishes (success or failure).
/// Ported from `JobCompletedEventArgs`.
public struct JobCompletedEventArgs: @unchecked Sendable {
    public let job: CronJob
    public let response: String
    public let error: Error?

    public init(job: CronJob, response: String, error: Error?) {
        self.job = job
        self.response = response
        self.error = error
    }
}

/// Runs a background loop that polls `IScheduledTaskStore` for due `CronJob`
/// records every 30 seconds, executes them via `IAIService.ask`, and invokes
/// `onJobCompleted`. Delivery routing is left to the host via the callback so
/// the SDK has no dependency on platform notification libraries.
///
/// Swift-concurrency port of `ScheduledAIService`: start it with `start()`
/// which spawns a detached loop task; call `stop()` to cancel and await it.
public final class ScheduledAIService: @unchecked Sendable {
    public typealias JobCompletedHandler = @Sendable (JobCompletedEventArgs) -> Void

    private let butler: IAIService
    private let store: IScheduledTaskStore
    private let pollInterval: TimeInterval

    private let lock = NSLock()
    private var loopTask: Task<Void, Never>?
    private var handlers: [JobCompletedHandler] = []

    public init(butler: IAIService, store: IScheduledTaskStore, pollInterval: TimeInterval = 30) {
        self.butler = butler
        self.store = store
        self.pollInterval = pollInterval
    }

    /// Subscribe to job-completion events. Invoked on the loop task; handlers
    /// must be thread-safe.
    public func onJobCompleted(_ handler: @escaping JobCompletedHandler) {
        lock.lock(); handlers.append(handler); lock.unlock()
    }

    /// Starts the background polling loop. Calling this when the loop is already
    /// running is a no-op.
    public func start() {
        lock.lock()
        if let existing = loopTask, !existing.isCancelled {
            lock.unlock(); return
        }
        let task = Task<Void, Never> { [weak self] in
            guard let self = self else { return }
            await self.runLoop()
        }
        loopTask = task
        lock.unlock()
    }

    /// Signals the polling loop to stop and waits for it to exit.
    public func stop() async {
        lock.lock(); let task = loopTask; loopTask = nil; lock.unlock()
        task?.cancel()
        await task?.value
    }

    // ------------------------------------------------------------------
    // Core loop
    // ------------------------------------------------------------------

    private func runLoop() async {
        while !Task.isCancelled {
            do {
                try await processDueJobs()
            } catch is CancellationError {
                break
            } catch {
                // Unhandled error in poll cycle — swallow, keep looping.
            }
            do {
                try await Task.sleep(nanoseconds: UInt64(pollInterval * 1_000_000_000))
            } catch {
                break // cancelled
            }
        }
    }

    private func processDueJobs() async throws {
        let dueJobs = try await store.getDueJobs()
        if dueJobs.isEmpty { return }
        for job in dueJobs {
            if Task.isCancelled { break }
            await executeJob(job)
        }
    }

    /// Runs one job to completion. Public so hosts/tests can drive a single
    /// execution deterministically without the timer loop.
    public func executeJob(_ job: CronJob) async {
        let now = Date()

        // Mark as Running.
        let running = job.copy(state: .running)
        _ = try? await store.upsert(running)

        var response = ""
        var error: Error? = nil

        do {
            response = try await butler.ask(job.prompt)
        } catch is CancellationError {
            // Cancellation is not a job failure — restore previous state.
            let restored = job.copy(state: .pending)
            _ = try? await store.upsert(restored)
            return
        } catch let ex {
            error = ex
        }

        let nextRun = Self.computeNextRun(job.cronExpression, after: now)
        let updatedState: CronJobState = error == nil ? .succeeded : .failed
        let updated = job.copy(
            lastRunUtc: .some(now),
            nextRunUtc: .some(nextRun),
            state: updatedState)

        _ = try? await store.upsert(updated)

        let snapshot: [JobCompletedHandler]
        lock.lock(); snapshot = handlers; lock.unlock()
        let args = JobCompletedEventArgs(job: updated, response: response, error: error)
        for h in snapshot { h(args) }
    }

    private static func computeNextRun(_ cronExpression: String, after: Date) -> Date? {
        try? CronScheduleParser.getNextOccurrence(cronExpression, after: after)
    }
}
