// Gaming.swift
//
// Port of the Gaming vertical from src/CircleAI.Gaming/GamingPrimitives.cs and
// the static domain-context constants from GamingDomainContext.cs:
//   • GameTitle, PlaySession, AchievementUnlock — domain records
//   • IGamingBoard                              — titles, sessions, achievements
//   • InMemoryGamingBoard                       — deterministic in-memory impl
//   • GamingDomainContext                       — system-prompt snippet + flags
//
// The Companion-facing wrapper (GamingCompanionAdapter) is an ICompanionSession
// decorator that prefixes the gaming domain prompt.
//
// Porting notes:
//   • `TimeSpan` → `TimeInterval` (seconds); `DateTimeOffset` → `Date`.
//   • `TitlesByGenre` matches Genre case-insensitively.
//   • `TotalPlayTime` sums session durations for user+title (0 if none).
//   • `AchievementsFor` orders descending by AtUtc.
//   • `MostPlayed(topK)` groups sessions by TitleId, orders by summed duration
//     descending, takes topK, projects to the known GameTitle (dropping unknown
//     ids). `topK <= 0` throws `.invalidTopK`.
//   • All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A game title.
public struct GameTitle: Sendable, Equatable, Codable {
    public let titleId: String
    public let name: String
    public let genre: String
    public let platform: String

    public init(titleId: String, name: String, genre: String, platform: String) {
        self.titleId = titleId
        self.name = name
        self.genre = genre
        self.platform = platform
    }
}

/// A play session.
public struct PlaySession: Sendable, Equatable, Codable {
    public let sessionId: String
    public let userId: String
    public let titleId: String
    public let duration: TimeInterval
    public let atUtc: Date

    public init(sessionId: String, userId: String, titleId: String, duration: TimeInterval, atUtc: Date) {
        self.sessionId = sessionId
        self.userId = userId
        self.titleId = titleId
        self.duration = duration
        self.atUtc = atUtc
    }
}

/// An achievement unlock.
public struct AchievementUnlock: Sendable, Equatable, Codable {
    public let unlockId: String
    public let userId: String
    public let titleId: String
    public let achievement: String
    public let atUtc: Date

    public init(unlockId: String, userId: String, titleId: String, achievement: String, atUtc: Date) {
        self.unlockId = unlockId
        self.userId = userId
        self.titleId = titleId
        self.achievement = achievement
        self.atUtc = atUtc
    }
}

// MARK: - Errors

public enum GamingError: Error, Equatable, CustomStringConvertible {
    case invalidTopK

    public var description: String {
        switch self {
        case .invalidTopK: return "topK must be positive"
        }
    }
}

// MARK: - Contract

/// Titles, sessions, and achievements for the gaming vertical.
public protocol IGamingBoard: AnyObject, Sendable {
    func addTitle(_ t: GameTitle)
    func getTitle(_ id: String) -> GameTitle?
    func titlesByGenre(_ genre: String) -> [GameTitle]
    func recordSession(_ s: PlaySession)
    func totalPlayTime(userId: String, titleId: String) -> TimeInterval
    func unlock(_ u: AchievementUnlock)
    func achievementsFor(userId: String) -> [AchievementUnlock]
    func mostPlayed(userId: String, topK: Int) throws -> [GameTitle]
}

public extension IGamingBoard {
    /// Convenience overload mirroring the C# default `topK = 5`.
    func mostPlayed(userId: String) throws -> [GameTitle] { try mostPlayed(userId: userId, topK: 5) }
}

// MARK: - InMemoryGamingBoard

/// Deterministic in-memory `IGamingBoard`. All state guarded by a single `NSLock`.
public final class InMemoryGamingBoard: IGamingBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var titles: [String: GameTitle] = [:]
    private var sessions: [PlaySession] = []
    private var unlocks: [AchievementUnlock] = []

    public init() {}

    public func addTitle(_ t: GameTitle) {
        lock.lock(); defer { lock.unlock() }
        titles[t.titleId] = t
    }

    public func getTitle(_ id: String) -> GameTitle? {
        lock.lock(); defer { lock.unlock() }
        return titles[id]
    }

    public func titlesByGenre(_ genre: String) -> [GameTitle] {
        lock.lock(); defer { lock.unlock() }
        return titles.values.filter { $0.genre.caseInsensitiveCompare(genre) == .orderedSame }
    }

    public func recordSession(_ s: PlaySession) {
        lock.lock(); defer { lock.unlock() }
        sessions.append(s)
    }

    public func totalPlayTime(userId: String, titleId: String) -> TimeInterval {
        lock.lock(); defer { lock.unlock() }
        return sessions.filter { $0.userId == userId && $0.titleId == titleId }.reduce(0.0) { $0 + $1.duration }
    }

    public func unlock(_ u: AchievementUnlock) {
        lock.lock(); defer { lock.unlock() }
        unlocks.append(u)
    }

    public func achievementsFor(userId: String) -> [AchievementUnlock] {
        lock.lock(); defer { lock.unlock() }
        return unlocks.filter { $0.userId == userId }.sorted { $0.atUtc > $1.atUtc }
    }

    public func mostPlayed(userId: String, topK: Int = 5) throws -> [GameTitle] {
        if topK <= 0 { throw GamingError.invalidTopK }
        lock.lock(); defer { lock.unlock() }
        // Group by TitleId preserving first-encounter order (matches C# GroupBy),
        // then order by summed duration descending with a stable tie-break so the
        // result is deterministic where C#'s OrderByDescending is stable.
        var order: [String] = []
        var totals: [String: TimeInterval] = [:]
        for s in sessions where s.userId == userId {
            if totals[s.titleId] == nil { order.append(s.titleId) }
            totals[s.titleId, default: 0] += s.duration
        }
        let ranked = order.enumerated()
            .sorted { lhs, rhs in
                let ld = totals[lhs.element] ?? 0, rd = totals[rhs.element] ?? 0
                if ld != rd { return ld > rd }
                return lhs.offset < rhs.offset // stable: keep first-seen order on ties
            }
            .prefix(topK)
        return ranked.compactMap { titles[$0.element] }
    }
}

// MARK: - GamingDomainContext

/// Static domain-context constants for the gaming vertical.
public enum GamingDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Gaming] Expert gaming companion. Help with game strategy guides, build optimisation, community event planning, game review writing, speedrun technique research, and gaming health (screen time, ergonomics). Compliance: POPIA, WASPA (in-game purchases), child protection where applicable."
    public static let complianceFlags: [String] = ["POPIA", "WASPA", "Child_Protection"]
    public static let suggestedTools: [String] = ["game_db", "community_tools", "analytics", "web_search"]
}

// MARK: - GamingCompanionAdapter

/// An `ICompanionSession` decorator that prepends the gaming domain system
/// prompt to every conversational call and adds gaming helper methods.
/// Port of `CircleAI.Gaming.GamingCompanionAdapter`. Identity/context/feedback
/// are forwarded to the inner session; proactive events forward through the
/// inner session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class GamingCompanionAdapter: ICompanionSession, @unchecked Sendable {
    private let inner: ICompanionSession

    public init(_ inner: ICompanionSession) {
        self.inner = inner
    }

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

    public func send(_ message: String) async throws -> String { try await inner.send(enrich(message)) }
    public func stream(_ message: String) -> AsyncStream<String> { inner.stream(enrich(message)) }
    public func agent(_ instruction: String) async throws -> String { try await inner.agent(enrich(instruction)) }

    private func enrich(_ m: String) -> String { "\(GamingDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Gaming helpers ────────────────────────────────────────────────────────

    /// Build a competitive strategy (C# `BuildStrategyAsync`).
    public func buildStrategy(game: String, goal: String, currentSetup: String) async throws -> String {
        try await inner.agent(
            "Build a competitive strategy for \(game). Goal: \(goal). Current setup: \(currentSetup). Include build recommendations, macro strategy, and key counters.")
    }

    /// Write a structured game review (C# `WriteGameReviewAsync`).
    public func writeGameReview(game: String, playtime: String, verdict: String) async throws -> String {
        try await inner.agent(
            "Write a structured game review for \(game). Playtime: \(playtime). My verdict: \(verdict). Include: graphics, gameplay, story, performance, value, and a score out of 10.")
    }

    /// Recommend games for a mood (C# `RecommendGameAsync`).
    public func recommendGame(mood: String, platform: String, timeAvailableMin: Int) async throws -> String {
        try await inner.agent(
            "Recommend 3 games for mood '\(mood)' on \(platform), with \(timeAvailableMin) min. Mix indie/AAA, justify per pick.")
    }

    /// Sketch a speedrun route (C# `DesignSpeedrunRouteAsync`).
    public func designSpeedrunRoute(gameTitle: String, category: String) async throws -> String {
        try await inner.agent(
            "Sketch a speedrun route outline for \(gameTitle) (\(category)). Cover key skips, glitches at high level, risk-vs-reward gates.")
    }

    /// Draft patch notes (C# `DraftPatchNotesAsync`).
    public func draftPatchNotes(changes: String, audience: String) async throws -> String {
        try await inner.agent(
            "Draft patch notes for changes: \(changes). Audience: \(audience). Group balance/QoL/bugfix, lead with player impact.")
    }

    /// Analyse player retention curves (C# `AnalysePlayerRetentionAsync`).
    public func analysePlayerRetention(day1Pct: String, day7Pct: String, day30Pct: String) async throws -> String {
        try await inner.agent(
            "Analyse retention: D1=\(day1Pct), D7=\(day7Pct), D30=\(day30Pct). Diagnose the weakest curve segment + an experiment to lift it.")
    }
}
