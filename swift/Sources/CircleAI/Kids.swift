// Kids.swift
//
// Port of the Kids vertical from src/CircleAI.Kids/KidsPrimitives.cs and the
// static domain-context constants from KidsDomainContext.cs:
//   • AgeAppropriateness                 — Toddler…Teen age bands
//   • KidsContent, DailyTime, TimeLog    — domain records
//   • IKidsBoard                         — content, limits, screen/reading time
//   • InMemoryKidsBoard                  — deterministic in-memory impl
//   • KidsDomainContext                  — system-prompt snippet + flags
//
// The Companion-facing wrapper (KidsCompanionAdapter) is an ICompanionSession
// decorator that prefixes the kids domain prompt.
//
// Porting notes:
//   • `TimeSpan` → `TimeInterval` (seconds); `DateTimeOffset` → `Date`.
//   • `ContentFor(band)` filters by AgeBand, ordered ascending by Title.
//   • `UsedToday(kid, kind, now)` sums durations for the kid+kind logged on the
//     same UTC calendar day as `now`.
//   • `OverLimit(kid, kind, now)` returns false when no limits set; the cap is
//     the screen/reading limit for "screen"/"reading" (case-insensitive), else
//     effectively unbounded (`.greatestFiniteMagnitude`, mirroring TimeSpan.MaxValue).
//   • All state guarded by a single `NSLock` (used-today total is a non-locking
//     private helper).

import Foundation

// MARK: - Enums

/// Age-appropriateness band for kids content.
public enum AgeAppropriateness: String, Sendable, Equatable, Codable, CaseIterable {
    case toddler = "Toddler"
    case preschool = "Preschool"
    case earlyPrimary = "EarlyPrimary"
    case latePrimary = "LatePrimary"
    case preTeen = "PreTeen"
    case teen = "Teen"
}

// MARK: - Records

/// A piece of kids content.
public struct KidsContent: Sendable, Equatable, Codable {
    public let contentId: String
    public let title: String
    public let ageBand: AgeAppropriateness
    public let kind: String
    public let tags: [String]

    public init(contentId: String, title: String, ageBand: AgeAppropriateness, kind: String, tags: [String]) {
        self.contentId = contentId
        self.title = title
        self.ageBand = ageBand
        self.kind = kind
        self.tags = tags
    }
}

/// Daily screen and reading limits for a child.
public struct DailyTime: Sendable, Equatable, Codable {
    public let kidName: String
    public let screenLimit: TimeInterval
    public let readingLimit: TimeInterval

    public init(kidName: String, screenLimit: TimeInterval, readingLimit: TimeInterval) {
        self.kidName = kidName
        self.screenLimit = screenLimit
        self.readingLimit = readingLimit
    }
}

/// A logged block of screen or reading time.
public struct TimeLog: Sendable, Equatable, Codable {
    public let kidName: String
    public let kind: String
    public let duration: TimeInterval
    public let atUtc: Date

    public init(kidName: String, kind: String, duration: TimeInterval, atUtc: Date) {
        self.kidName = kidName
        self.kind = kind
        self.duration = duration
        self.atUtc = atUtc
    }
}

// MARK: - Contract

/// Content, limits, and screen/reading time tracking for the kids vertical.
public protocol IKidsBoard: AnyObject, Sendable {
    func addContent(_ c: KidsContent)
    func contentFor(band: AgeAppropriateness) -> [KidsContent]
    func setLimits(_ d: DailyTime)
    func limitsFor(kidName: String) -> DailyTime?
    func recordTime(_ t: TimeLog)
    func usedToday(kidName: String, kind: String, now: Date) -> TimeInterval
    func overLimit(kidName: String, kind: String, now: Date) -> Bool
}

// MARK: - InMemoryKidsBoard

/// Deterministic in-memory `IKidsBoard`. All state guarded by a single `NSLock`.
public final class InMemoryKidsBoard: IKidsBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var content: [String: KidsContent] = [:]
    private var limits: [String: DailyTime] = [:]
    private var logs: [TimeLog] = []

    private static let utcCalendar: Calendar = {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        return cal
    }()

    public init() {}

    public func addContent(_ c: KidsContent) {
        lock.lock(); defer { lock.unlock() }
        content[c.contentId] = c
    }

    public func contentFor(band: AgeAppropriateness) -> [KidsContent] {
        lock.lock(); defer { lock.unlock() }
        return content.values.filter { $0.ageBand == band }.sorted { $0.title < $1.title }
    }

    public func setLimits(_ d: DailyTime) {
        lock.lock(); defer { lock.unlock() }
        limits[d.kidName] = d
    }

    public func limitsFor(kidName: String) -> DailyTime? {
        lock.lock(); defer { lock.unlock() }
        return limits[kidName]
    }

    public func recordTime(_ t: TimeLog) {
        lock.lock(); defer { lock.unlock() }
        logs.append(t)
    }

    public func usedToday(kidName: String, kind: String, now: Date) -> TimeInterval {
        lock.lock(); defer { lock.unlock() }
        return usedTodayLocked(kidName: kidName, kind: kind, now: now)
    }

    public func overLimit(kidName: String, kind: String, now: Date) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let limits = limits[kidName] else { return false }
        let used = usedTodayLocked(kidName: kidName, kind: kind, now: now)
        let cap: TimeInterval
        if kind.caseInsensitiveCompare("screen") == .orderedSame {
            cap = limits.screenLimit
        } else if kind.caseInsensitiveCompare("reading") == .orderedSame {
            cap = limits.readingLimit
        } else {
            cap = .greatestFiniteMagnitude
        }
        return used > cap
    }

    /// Total time used today for a kid+kind. Caller must hold `lock`.
    private func usedTodayLocked(kidName: String, kind: String, now: Date) -> TimeInterval {
        let cal = Self.utcCalendar
        return logs
            .filter { $0.kidName == kidName && $0.kind == kind && cal.isDate($0.atUtc, inSameDayAs: now) }
            .reduce(0.0) { $0 + $1.duration }
    }
}

// MARK: - KidsDomainContext

/// Static domain-context constants for the kids vertical.
public enum KidsDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Kids] Safe, age-appropriate learning companion for children. Use simple, encouraging language. Help with homework, creative storytelling, educational games, and curiosity questions. Never generate inappropriate content. Validate effort, not just results. Compliance: POPIA (children's data), COPPA-principles, Children's Act, CAPS curriculum."
    public static let complianceFlags: [String] = ["POPIA_Childrens_Data", "COPPA_principles", "Childrens_Act", "CAPS_curriculum"]
    public static let suggestedTools: [String] = ["educational_content", "story_tools", "quiz_tools"]
}

// MARK: - KidsCompanionAdapter

/// An `ICompanionSession` decorator that prepends the kids domain system prompt
/// to every conversational call and adds kid-safe helper methods.
/// Port of `CircleAI.Kids.KidsCompanionAdapter`. Identity/context/feedback are
/// forwarded to the inner session; proactive events forward through the inner
/// session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class KidsCompanionAdapter: ICompanionSession, @unchecked Sendable {
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

    private func enrich(_ m: String) -> String { "\(KidsDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Kids helpers ──────────────────────────────────────────────────────────

    /// Help with homework via Socratic questions (C# `HelpHomeworkAsync`).
    public func helpHomework(subject: String, grade: String, question: String) async throws -> String {
        try await inner.agent(
            "Help a Grade \(grade) learner with \(subject) homework. Question: \(question). Guide with Socratic questions rather than giving the answer directly. Keep explanation simple and encouraging.")
    }

    /// Tell an age-appropriate story (C# `TellStoryAsync`).
    public func tellStory(theme: String, characters: String, ageGroup: String) async throws -> String {
        try await inner.agent(
            "Tell a short, imaginative story for age group \(ageGroup). Theme: \(theme). Characters: \(characters). Keep it age-appropriate, with a positive lesson at the end.")
    }

    /// Design an activity (C# `DesignActivityAsync`).
    public func designActivity(ageBand: String, minutes: Int, interests: String) async throws -> String {
        try await inner.agent(
            "Design a \(minutes)-minute activity for \(ageBand) with interests: \(interests). Materials, steps, learning value, mess level.")
    }

    /// Explain a hard concept (C# `ExplainHardConceptAsync`).
    public func explainHardConcept(concept: String, ageBand: String) async throws -> String {
        try await inner.agent(
            "Explain '\(concept)' to \(ageBand). Use one analogy from their world, one example they've seen, one question to check understanding.")
    }

    /// Screen content for an age band (C# `ScreenContentAsync`).
    public func screenContent(contentTitle: String, ageBand: String) async throws -> String {
        try await inner.agent(
            "Screen '\(contentTitle)' for \(ageBand): themes, violence/language/scary moments, talk-after questions, age verdict.")
    }

    /// Coach a parent through big feelings (C# `HandleBigFeelingAsync`).
    public func handleBigFeeling(ageBand: String, situation: String) async throws -> String {
        try await inner.agent(
            "Coach a parent through helping a \(ageBand) with big feelings about: \(situation). Validate-name-co-regulate script.")
    }
}
