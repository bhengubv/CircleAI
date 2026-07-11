// Sports.swift
//
// Port of the Sports vertical from src/CircleAI.Sports/SportsPrimitives.cs and
// the static domain-context constants from SportsDomainContext.cs:
//   • DistanceKind                      — Run/Bike/Swim/Walk/Row
//   • SportActivity, PersonalBest, TrainingSession — domain records
//   • ISportsBoard                      — log/history/weekly volume/PB/sessions
//
// The C# record is `CircleAI.Sports.Activity`; renamed to `SportActivity` here
// because the flat Swift module already has a `CircleAI.Crm.Activity` port and
// both would occupy the single Swift namespace.
//   • InMemorySportsBoard               — deterministic in-memory impl
//   • SportsDomainContext               — system-prompt snippet + flags
//   • SportsCompanionAdapter            — ICompanionSession decorator that
//     prefixes the sports domain prompt and adds sports helper methods
//
// Porting notes:
//   • `TimeSpan` → `TimeInterval` (seconds); `DateTimeOffset` → `Date`.
//   • `History(limit)` orders descending by AtUtc, takes `limit`; `limit <= 0`
//     throws `.invalidLimit` (mirrors ArgumentOutOfRangeException).
//   • `TotalKmThisWeek` sums DistanceKm for the user+kind since week start
//     (now's date minus its weekday index, Sunday = 0 as in C# DayOfWeek).
//   • `Best` returns the fastest (min duration) activity of that kind with
//     DistanceKm >= the target, projected onto a PersonalBest; nil if none.
//   • `Complete` on an unknown session throws `.unknownSession`.
//   • `Upcoming` returns incomplete sessions with ScheduledUtc >= now (UTC),
//     ordered ascending. All state guarded by a single `NSLock`.

import Foundation

// MARK: - Enums

/// A distance-based activity kind.
public enum DistanceKind: String, Sendable, Equatable, Codable, CaseIterable {
    case run = "Run"
    case bike = "Bike"
    case swim = "Swim"
    case walk = "Walk"
    case row = "Row"
}

// MARK: - Records

/// A logged distance activity. (C# `CircleAI.Sports.Activity`.)
public struct SportActivity: Sendable, Equatable, Codable {
    public let activityId: String
    public let userId: String
    public let kind: DistanceKind
    public let distanceKm: Double
    public let duration: TimeInterval
    public let atUtc: Date

    public init(activityId: String, userId: String, kind: DistanceKind, distanceKm: Double, duration: TimeInterval, atUtc: Date) {
        self.activityId = activityId
        self.userId = userId
        self.kind = kind
        self.distanceKm = distanceKm
        self.duration = duration
        self.atUtc = atUtc
    }
}

/// A personal best over a given distance.
public struct PersonalBest: Sendable, Equatable, Codable {
    public let userId: String
    public let kind: DistanceKind
    public let distanceKm: Double
    public let time: TimeInterval
    public let achievedUtc: Date

    public init(userId: String, kind: DistanceKind, distanceKm: Double, time: TimeInterval, achievedUtc: Date) {
        self.userId = userId
        self.kind = kind
        self.distanceKm = distanceKm
        self.time = time
        self.achievedUtc = achievedUtc
    }
}

/// A scheduled training session.
public struct TrainingSession: Sendable, Equatable, Codable {
    public let sessionId: String
    public let userId: String
    public let plan: String
    public let scheduledUtc: Date
    public let completed: Bool

    public init(sessionId: String, userId: String, plan: String, scheduledUtc: Date, completed: Bool) {
        self.sessionId = sessionId
        self.userId = userId
        self.plan = plan
        self.scheduledUtc = scheduledUtc
        self.completed = completed
    }
}

// MARK: - Errors

public enum SportsError: Error, Equatable, CustomStringConvertible {
    case invalidLimit
    case unknownSession(String)

    public var description: String {
        switch self {
        case .invalidLimit: return "limit must be positive"
        case .unknownSession(let id): return "Unknown session \(id)"
        }
    }
}

// MARK: - Contract

/// Workouts, sessions, personal bests, and weekly volume for the sports vertical.
public protocol ISportsBoard: AnyObject, Sendable {
    func log(_ a: SportActivity)
    func history(userId: String, limit: Int) throws -> [SportActivity]
    func totalKmThisWeek(userId: String, kind: DistanceKind, now: Date) -> Double
    func best(userId: String, kind: DistanceKind, distanceKm: Double) -> PersonalBest?
    func schedule(_ s: TrainingSession)
    func complete(sessionId: String) throws
    func upcoming(userId: String) -> [TrainingSession]
}

public extension ISportsBoard {
    /// Convenience overload mirroring the C# default `limit = 50`.
    func history(userId: String) throws -> [SportActivity] { try history(userId: userId, limit: 50) }
}

// MARK: - InMemorySportsBoard

/// Deterministic in-memory `ISportsBoard`. All state guarded by a single `NSLock`.
public final class InMemorySportsBoard: ISportsBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var activities: [SportActivity] = []
    private var sessions: [String: TrainingSession] = [:]

    public init() {}

    public func log(_ a: SportActivity) {
        lock.lock(); defer { lock.unlock() }
        activities.append(a)
    }

    public func history(userId: String, limit: Int = 50) throws -> [SportActivity] {
        if limit <= 0 { throw SportsError.invalidLimit }
        lock.lock(); defer { lock.unlock() }
        return Array(activities.filter { $0.userId == userId }.sorted { $0.atUtc > $1.atUtc }.prefix(limit))
    }

    public func totalKmThisWeek(userId: String, kind: DistanceKind, now: Date) -> Double {
        let weekStart = Self.weekStart(now)
        lock.lock(); defer { lock.unlock() }
        return activities
            .filter { $0.userId == userId && $0.kind == kind && $0.atUtc >= weekStart }
            .reduce(0.0) { $0 + $1.distanceKm }
    }

    public func best(userId: String, kind: DistanceKind, distanceKm: Double) -> PersonalBest? {
        lock.lock(); defer { lock.unlock() }
        let hit = activities
            .filter { $0.userId == userId && $0.kind == kind && $0.distanceKm >= distanceKm }
            .min { $0.duration < $1.duration }
        guard let hit else { return nil }
        return PersonalBest(userId: userId, kind: kind, distanceKm: distanceKm, time: hit.duration, achievedUtc: hit.atUtc)
    }

    public func schedule(_ s: TrainingSession) {
        lock.lock(); defer { lock.unlock() }
        sessions[s.sessionId] = s
    }

    public func complete(sessionId: String) throws {
        lock.lock(); defer { lock.unlock() }
        guard let s = sessions[sessionId] else { throw SportsError.unknownSession(sessionId) }
        sessions[sessionId] = TrainingSession(sessionId: s.sessionId, userId: s.userId, plan: s.plan, scheduledUtc: s.scheduledUtc, completed: true)
    }

    public func upcoming(userId: String) -> [TrainingSession] {
        let now = Date()
        lock.lock(); defer { lock.unlock() }
        return sessions.values
            .filter { $0.userId == userId && !$0.completed && $0.scheduledUtc >= now }
            .sorted { $0.scheduledUtc < $1.scheduledUtc }
    }

    /// Start of the current week: the date component of `now` minus its weekday
    /// index (Sunday = 0), mirroring C# `now.Date.AddDays(-(int)now.DayOfWeek)`.
    private static func weekStart(_ now: Date) -> Date {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        let startOfDay = cal.startOfDay(for: now)
        // Calendar weekday: 1 = Sunday … 7 = Saturday. C# DayOfWeek: 0 = Sunday.
        let weekdayIndex = cal.component(.weekday, from: startOfDay) - 1
        return cal.date(byAdding: .day, value: -weekdayIndex, to: startOfDay)!
    }
}

// MARK: - SportsDomainContext

/// Static domain-context constants for the sports vertical.
public enum SportsDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Sports] Expert sports management and performance assistant. Help with training programme design, athlete nutrition guidance, club administration, fixture scheduling, performance data analysis, and sports event management. Apply periodisation and load management principles. Compliance: WADA anti-doping rules, SASCOC, Sport and Recreation SA, POPIA."
    public static let complianceFlags: [String] = ["WADA", "SASCOC", "Sport_Recreation_SA", "POPIA"]
    public static let suggestedTools: [String] = ["performance_tracker", "analytics", "schedule_manager", "document_editor"]
}

// MARK: - SportsCompanionAdapter

/// An `ICompanionSession` decorator that prepends the sports domain system
/// prompt to every conversational call and adds sports-authoring convenience
/// methods (training programmes, performance analysis, recovery, reports).
/// Port of `CircleAI.Sports.SportsCompanionAdapter`.
///
/// The inner session's identity/context/feedback surface is forwarded verbatim.
/// C# exposes `ProactiveMessageReady` as an event that add/remove-forwards to
/// the inner session; the Swift `ICompanionSession` surface models proactive
/// events as the `proactiveEvents` async stream, so this adapter forwards that
/// stream straight through. (The C# `DisposeAsync` forwarding has no analogue
/// because the Swift `ICompanionSession` protocol does not declare disposal.)
public final class SportsCompanionAdapter: ICompanionSession, @unchecked Sendable {
    private let inner: ICompanionSession

    public init(_ inner: ICompanionSession) {
        self.inner = inner
    }

    // ── Forwarded identity / surface ──────────────────────────────────────────

    public var sessionId: String { inner.sessionId }
    public var identityId: String { inner.identityId }
    public var interface: InterfaceKind { inner.interface }
    public var history: [CompanionTurn] { inner.history }

    public func getContext() -> CompanionContext { inner.getContext() }

    public func refreshContext() async throws { try await inner.refreshContext() }

    public func signalFeedback(positive: Bool, note: String?) async throws {
        try await inner.signalFeedback(positive: positive, note: note)
    }

    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> { inner.proactiveEvents }

    // ── Domain-prefixed conversation ──────────────────────────────────────────

    public func send(_ message: String) async throws -> String {
        try await inner.send(enrich(message))
    }

    public func stream(_ message: String) -> AsyncStream<String> {
        inner.stream(enrich(message))
    }

    public func agent(_ instruction: String) async throws -> String {
        try await inner.agent(enrich(instruction))
    }

    /// Prepend the sports domain system prompt to a message. Port of the private
    /// `E(m)` helper (`$"{SystemPromptSnippet}\n\n{m}"`).
    private func enrich(_ m: String) -> String {
        "\(SportsDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // ── Sports helpers (route through inner.agent) ────────────────────────────

    /// Design a periodised training programme (C# `DesignTrainingProgramAsync`).
    public func designTrainingProgram(sport: String, athleteProfile: String, goal: String, weeks: Int) async throws -> String {
        try await inner.agent(
            "Design a \(weeks)-week periodised training programme for \(sport). Athlete: \(athleteProfile). Goal: \(goal). Include weekly volume, intensity zones, key sessions, and recovery weeks.")
    }

    /// Analyse athlete performance data (C# `AnalysePerformanceAsync` — single arg).
    public func analysePerformance(athleteData: String) async throws -> String {
        try await inner.agent(
            "Analyse this athlete performance data and identify strengths, weaknesses, and priority interventions:\n\(athleteData)")
    }

    /// Design a training block peaking at a target event (C# `DesignTrainingBlockAsync`).
    public func designTrainingBlock(sport: String, targetEvent: String, weeks: Int) async throws -> String {
        try await inner.agent(
            "Design a \(weeks)-week training block for \(sport) peaking at \(targetEvent). Periodisation, key sessions, tapers.")
    }

    /// Analyse recent performance against metrics (C# `AnalysePerformanceAsync` — three args).
    public func analysePerformance(sport: String, recentResults: String, keyMetrics: String) async throws -> String {
        try await inner.agent(
            "Analyse recent \(sport) performance: \(recentResults). Key metrics: \(keyMetrics). Strengths to lean into, gaps to close.")
    }

    /// Plan recovery between sessions (C# `PlanRecoveryAsync`).
    public func planRecovery(sessionIntensity: String, daysUntilNext: String) async throws -> String {
        try await inner.agent(
            "Plan recovery between sessions: \(sessionIntensity), \(daysUntilNext) days. Nutrition, sleep, mobility, modality picks.")
    }

    /// Draft a post-match report (C# `DraftPostMatchReportAsync`).
    public func draftPostMatchReport(match: String, keyMoments: String) async throws -> String {
        try await inner.agent(
            "Draft a post-match report on \(match). Key moments: \(keyMoments). Tactical, individual standouts, areas to drill.")
    }
}
