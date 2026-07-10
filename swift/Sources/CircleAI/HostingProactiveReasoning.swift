// HostingProactiveReasoning.swift
//
// Port of the CircleAI.Hosting proactive-reasoning surface:
//   - ITriggerCondition.cs         → ITriggerCondition + ProactiveContext (snapshot)
//   - ScheduleTrigger.cs           → ScheduleTrigger (fires once/day in a 5-min window)
//   - IdleTrigger.cs               → IdleTrigger (fires past an idle threshold)
//   - IProactiveReasoningService.cs→ IProactiveReasoningService + ProactiveMessageEventArgs
//   - ProactiveReasoningService.cs → ProactiveReasoningService (first-trigger-wins check)
//
// This is B!'s ability to initiate contact rather than merely respond. Only one
// trigger fires per `check` call (list order = priority).

import Foundation

// =====================================================================
// ProactiveContext + ITriggerCondition
// =====================================================================

/// Context snapshot passed to trigger conditions. Mirrors the C#
/// `ProactiveContext` record used by the reasoning service (distinct from the
/// scheduling `ProactiveTask` substrate in `Proactive.swift`).
public struct ProactiveContext: @unchecked Sendable {
    public let userId: String
    public let nowUtc: Date
    public let timeSinceLastInteraction: TimeInterval
    public let affectState: AffectState?
    public let activeGoals: [Goal]

    public init(userId: String, nowUtc: Date, timeSinceLastInteraction: TimeInterval,
                affectState: AffectState?, activeGoals: [Goal]) {
        self.userId = userId
        self.nowUtc = nowUtc
        self.timeSinceLastInteraction = timeSinceLastInteraction
        self.affectState = affectState
        self.activeGoals = activeGoals
    }
}

/// A condition that, when true, signals B! should check in proactively. Ported
/// from `ITriggerCondition`.
public protocol ITriggerCondition: AnyObject, Sendable {
    /// Stable name used for logging and deduplication.
    var name: String { get }
    /// Returns true when the condition is currently met.
    func isMet(_ context: ProactiveContext) async throws -> Bool
}

// =====================================================================
// ScheduleTrigger
// =====================================================================

/// Time-of-day component (hour + minute), the port's stand-in for the C#
/// `TimeOnly` used by `ScheduleTrigger`.
public struct TimeOfDay: Sendable, Equatable {
    public let hour: Int
    public let minute: Int
    public init(hour: Int, minute: Int) {
        self.hour = hour
        self.minute = minute
    }
    /// Minutes since midnight — used for window comparisons.
    var totalMinutes: Int { hour * 60 + minute }
    /// Add minutes, wrapping across midnight.
    func adding(minutes: Int) -> TimeOfDay {
        let m = ((totalMinutes + minutes) % (24 * 60) + (24 * 60)) % (24 * 60)
        return TimeOfDay(hour: m / 60, minute: m % 60)
    }
}

/// Fires at a specific local time of day. Active for a 5-minute window starting
/// at `triggerTime`; fires at most once per calendar day. Ported from
/// `ScheduleTrigger`.
public final class ScheduleTrigger: ITriggerCondition, @unchecked Sendable {
    private let triggerTime: TimeOfDay
    private let lock = NSLock()
    private var lastFireDate: DateComponents?

    public init(triggerTime: TimeOfDay, name: String = "schedule") {
        self.triggerTime = triggerTime
        self.name = name
    }

    public let name: String

    public func isMet(_ context: ProactiveContext) async throws -> Bool {
        // Convert NowUtc to LOCAL time (matches C# NowUtc.LocalDateTime).
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone.current
        let comps = cal.dateComponents([.year, .month, .day, .hour, .minute], from: context.nowUtc)
        let localDate = DateComponents(year: comps.year, month: comps.month, day: comps.day)
        let localTime = TimeOfDay(hour: comps.hour ?? 0, minute: comps.minute ?? 0)

        lock.lock(); let last = lastFireDate; lock.unlock()
        if let last = last,
           last.year == localDate.year, last.month == localDate.month, last.day == localDate.day {
            return false // Already fired today.
        }

        let windowStart = triggerTime
        let windowEnd = triggerTime.adding(minutes: 5)

        let inWindow: Bool
        if windowEnd.totalMinutes >= windowStart.totalMinutes {
            inWindow = localTime.totalMinutes >= windowStart.totalMinutes
                    && localTime.totalMinutes < windowEnd.totalMinutes
        } else {
            // Window wraps midnight.
            inWindow = localTime.totalMinutes >= windowStart.totalMinutes
                    || localTime.totalMinutes < windowEnd.totalMinutes
        }

        if !inWindow { return false }

        lock.lock(); lastFireDate = localDate; lock.unlock()
        return true
    }
}

// =====================================================================
// IdleTrigger
// =====================================================================

/// Fires when `ProactiveContext.timeSinceLastInteraction` exceeds
/// `idleThreshold` (default 4 hours). Ported from `IdleTrigger`.
public final class IdleTrigger: ITriggerCondition, @unchecked Sendable {
    private let idleThreshold: TimeInterval

    public init(idleThreshold: TimeInterval = 4 * 3600) {
        self.idleThreshold = idleThreshold
    }

    public var name: String { "idle" }

    public func isMet(_ context: ProactiveContext) async throws -> Bool {
        context.timeSinceLastInteraction > idleThreshold
    }
}

// =====================================================================
// IProactiveReasoningService + ProactiveReasoningService
// =====================================================================

/// Emitted when B! generates a proactive message. Mirrors C#
/// `ProactiveMessageEventArgs`.
public struct ProactiveMessageEventArgs: Sendable {
    public let userId: String
    public let message: String
    public let triggerName: String
    public let generatedUtc: Date

    public init(userId: String, message: String, triggerName: String, generatedUtc: Date) {
        self.userId = userId
        self.message = message
        self.triggerName = triggerName
        self.generatedUtc = generatedUtc
    }
}

/// Evaluates trigger conditions and, when any fires, generates a proactive
/// check-in message unprompted. Ported from `IProactiveReasoningService`.
public protocol IProactiveReasoningService: AnyObject, Sendable {
    /// Evaluates all triggers; on the first hit, generates a message and invokes
    /// the registered `onProactiveMessageReady` handlers.
    func check(userId: String) async throws
    /// Subscribe to proactive-message events.
    func onProactiveMessageReady(_ handler: @escaping @Sendable (ProactiveMessageEventArgs) -> Void)
}

/// Default `IProactiveReasoningService`. Evaluates a prioritised list of
/// `ITriggerCondition`s and calls `IAIService.ask` to generate a warm,
/// goal-aware check-in when any condition fires. Only the first firing trigger
/// per `check` causes a message. Ported from `ProactiveReasoningService`.
public final class ProactiveReasoningService: IProactiveReasoningService, @unchecked Sendable {
    private let butler: IAIService
    private let goalStore: (any IGoalStore)?
    private let affectStore: (any IAffectStore)?
    private let triggers: [ITriggerCondition]
    private let lock = NSLock()
    private var handlers: [@Sendable (ProactiveMessageEventArgs) -> Void] = []

    public init(
        butler: IAIService,
        goalStore: (any IGoalStore)?,
        affectStore: (any IAffectStore)?,
        triggers: [ITriggerCondition]
    ) {
        self.butler = butler
        self.goalStore = goalStore
        self.affectStore = affectStore
        self.triggers = triggers
    }

    public func onProactiveMessageReady(_ handler: @escaping @Sendable (ProactiveMessageEventArgs) -> Void) {
        lock.lock(); handlers.append(handler); lock.unlock()
    }

    public func check(userId: String) async throws {
        precondition(!userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "userId required")
        if triggers.isEmpty { return }

        // 1. Load affect state.
        var affect: AffectState? = nil
        if let store = affectStore {
            affect = try? await store.load(userId: userId)
        }

        // 2. Load active goals.
        var activeGoals: [Goal] = []
        if let store = goalStore {
            activeGoals = (try? await store.getActive(userId: userId)) ?? []
        }

        // 3. Build context snapshot.
        let now = Date()
        let timeSinceLast = affect != nil ? now.timeIntervalSince(affect!.lastUpdatedAt) : 0

        let context = ProactiveContext(
            userId: userId,
            nowUtc: now,
            timeSinceLastInteraction: timeSinceLast,
            affectState: affect,
            activeGoals: activeGoals)

        // 4. Check triggers in order — fire only the first one.
        for trigger in triggers {
            let met: Bool
            do {
                met = try await trigger.isMet(context)
            } catch {
                continue // trigger threw — skip it.
            }
            if !met { continue }

            let prompt = Self.buildProactivePrompt(
                userId: userId, timeSinceLastInteraction: timeSinceLast, activeGoals: activeGoals)

            let message: String
            do {
                message = try await butler.ask(prompt)
            } catch {
                return // generation failed — bail out for this call.
            }

            let args = ProactiveMessageEventArgs(
                userId: userId, message: message, triggerName: trigger.name, generatedUtc: Date())

            let snapshot: [@Sendable (ProactiveMessageEventArgs) -> Void]
            lock.lock(); snapshot = handlers; lock.unlock()
            for h in snapshot { h(args) }

            return // Only fire one trigger per call.
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    static func buildProactivePrompt(
        userId: String,
        timeSinceLastInteraction: TimeInterval,
        activeGoals: [Goal]
    ) -> String {
        var sb = "You are B!. "

        if timeSinceLastInteraction / 60.0 > 5 {
            let hours = Int(timeSinceLastInteraction / 3600.0)
            let minutes = Int((timeSinceLastInteraction / 60.0).truncatingRemainder(dividingBy: 60))
            if hours > 0 {
                sb += "The user has been away for approximately \(hours) hour\(hours == 1 ? "" : "s"). "
            } else {
                sb += "The user has been away for approximately \(minutes) minute\(minutes == 1 ? "" : "s"). "
            }
        }

        if !activeGoals.isEmpty {
            sb += "They have \(activeGoals.count) active goal\(activeGoals.count == 1 ? "" : "s"): "
            for (i, g) in activeGoals.enumerated() {
                sb += "\"" + g.title + "\""
                if i < activeGoals.count - 1 { sb += ", " }
            }
            sb += ". "
        }

        sb += "Generate a brief, friendly check-in message (1-2 sentences). "
        sb += "Be warm, specific to their goals if you know them, and not intrusive."
        return sb
    }
}
