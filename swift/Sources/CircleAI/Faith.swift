// Faith.swift
//
// Port of the Faith vertical from src/CircleAI.Faith/FaithPrimitives.cs and the
// static domain-context constants from FaithDomainContext.cs:
//   • FaithService, PrayerRequest, ScriptureReference — domain records
//   • IFaithBoard                                      — services, prayers, scripture
//   • InMemoryFaithBoard                               — deterministic in-memory impl
//   • FaithDomainContext                               — system-prompt snippet + flags
//
// The Companion-facing wrapper (FaithCompanionAdapter) is an ICompanionSession
// decorator that prefixes the faith domain prompt.
//
// Porting notes:
//   • `DateTimeOffset` → `Date`.
//   • `ServicesBetween` filters StartUtc in [start, end], ordered ascending.
//   • `RecentPrayers(limit)` orders descending by SubmittedUtc, take limit (20).
//   • `Lookup` matches tradition+book+chapter+verse exactly (ordinal).
//   • `ByTradition` matches Tradition case-insensitively. All state guarded by a
//     single `NSLock`.

import Foundation

// MARK: - Records

/// A scheduled faith service.
public struct FaithService: Sendable, Equatable, Codable {
    public let serviceId: String
    public let communityName: String
    public let title: String
    public let startUtc: Date
    public let location: String

    public init(serviceId: String, communityName: String, title: String, startUtc: Date, location: String) {
        self.serviceId = serviceId
        self.communityName = communityName
        self.title = title
        self.startUtc = startUtc
        self.location = location
    }
}

/// A prayer request.
public struct PrayerRequest: Sendable, Equatable, Codable {
    public let requestId: String
    public let author: String
    public let body: String
    public let submittedUtc: Date
    public let isAnonymous: Bool

    public init(requestId: String, author: String, body: String, submittedUtc: Date, isAnonymous: Bool) {
        self.requestId = requestId
        self.author = author
        self.body = body
        self.submittedUtc = submittedUtc
        self.isAnonymous = isAnonymous
    }
}

/// A scripture reference.
public struct ScriptureReference: Sendable, Equatable, Codable {
    public let referenceId: String
    public let tradition: String
    public let book: String
    public let chapter: Int
    public let verse: Int
    public let text: String

    public init(referenceId: String, tradition: String, book: String, chapter: Int, verse: Int, text: String) {
        self.referenceId = referenceId
        self.tradition = tradition
        self.book = book
        self.chapter = chapter
        self.verse = verse
        self.text = text
    }
}

// MARK: - Contract

/// Services, prayers, and scripture references for the faith vertical.
public protocol IFaithBoard: AnyObject, Sendable {
    func schedule(_ s: FaithService)
    func servicesBetween(start: Date, end: Date) -> [FaithService]
    func submitPrayer(_ r: PrayerRequest)
    func recentPrayers(limit: Int) -> [PrayerRequest]
    func addScripture(_ r: ScriptureReference)
    func lookup(tradition: String, book: String, chapter: Int, verse: Int) -> ScriptureReference?
    func byTradition(_ tradition: String) -> [ScriptureReference]
}

public extension IFaithBoard {
    /// Convenience overload mirroring the C# default `limit = 20`.
    func recentPrayers() -> [PrayerRequest] { recentPrayers(limit: 20) }
}

// MARK: - InMemoryFaithBoard

/// Deterministic in-memory `IFaithBoard`. All state guarded by a single `NSLock`.
public final class InMemoryFaithBoard: IFaithBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var services: [String: FaithService] = [:]
    private var prayers: [PrayerRequest] = []
    private var scripture: [String: ScriptureReference] = [:]

    public init() {}

    public func schedule(_ s: FaithService) {
        lock.lock(); defer { lock.unlock() }
        services[s.serviceId] = s
    }

    public func servicesBetween(start: Date, end: Date) -> [FaithService] {
        lock.lock(); defer { lock.unlock() }
        return services.values.filter { $0.startUtc >= start && $0.startUtc <= end }.sorted { $0.startUtc < $1.startUtc }
    }

    public func submitPrayer(_ r: PrayerRequest) {
        lock.lock(); defer { lock.unlock() }
        prayers.append(r)
    }

    public func recentPrayers(limit: Int = 20) -> [PrayerRequest] {
        lock.lock(); defer { lock.unlock() }
        return Array(prayers.sorted { $0.submittedUtc > $1.submittedUtc }.prefix(limit))
    }

    public func addScripture(_ r: ScriptureReference) {
        lock.lock(); defer { lock.unlock() }
        scripture[r.referenceId] = r
    }

    public func lookup(tradition: String, book: String, chapter: Int, verse: Int) -> ScriptureReference? {
        lock.lock(); defer { lock.unlock() }
        return scripture.values.first { $0.tradition == tradition && $0.book == book && $0.chapter == chapter && $0.verse == verse }
    }

    public func byTradition(_ tradition: String) -> [ScriptureReference] {
        lock.lock(); defer { lock.unlock() }
        return scripture.values.filter { $0.tradition.caseInsensitiveCompare(tradition) == .orderedSame }
    }
}

// MARK: - FaithDomainContext

/// Static domain-context constants for the faith vertical.
public enum FaithDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Faith] Respectful, non-denominational spiritual companion. Help with scripture study, prayer composition, devotional content, faith community planning, and spiritual reflection prompts. Respect all faith traditions equally. Never impose one tradition on another. Compliance: POPIA."
    public static let complianceFlags: [String] = ["POPIA", "Non_Denominational_Respect"]
    public static let suggestedTools: [String] = ["scripture_tools", "document_editor", "calendar"]
}

// MARK: - FaithCompanionAdapter

/// An `ICompanionSession` decorator that prepends the faith domain system prompt
/// to every conversational call and adds faith helper methods.
/// Port of `CircleAI.Faith.FaithCompanionAdapter`. Identity/context/feedback are
/// forwarded to the inner session; proactive events forward through the inner
/// session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class FaithCompanionAdapter: ICompanionSession, @unchecked Sendable {
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

    private func enrich(_ m: String) -> String { "\(FaithDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Faith helpers ─────────────────────────────────────────────────────────

    /// Generate a devotional (C# `GenerateDevotionalAsync`).
    public func generateDevotional(theme: String, tradition: String) async throws -> String {
        try await inner.agent(
            "Write a short devotional on the theme of \(theme) for the \(tradition) tradition. Include a scripture reference, reflection, and closing prayer or meditation.")
    }

    /// Study a scripture passage (C# `StudyScriptureAsync`).
    public func studyScripture(passage: String, question: String) async throws -> String {
        try await inner.agent(
            "Help me study \(passage). Question: \(question). Provide historical context, key interpretations across traditions, and practical application.")
    }

    /// Compose a reflection (C# `ComposeReflectionAsync`).
    public func composeReflection(tradition: String, occasion: String, scriptureRef: String) async throws -> String {
        try await inner.agent(
            "Compose a 200-word reflection in the \(tradition) for \(occasion), anchored in \(scriptureRef). Warm, inclusive, devotional.")
    }

    /// Draft an order of service (C# `DraftServiceOrderAsync`).
    public func draftServiceOrder(tradition: String, serviceType: String, durationMinutes: Int) async throws -> String {
        try await inner.agent(
            "Draft a \(durationMinutes)-minute \(serviceType) order of service in the \(tradition). Sections, transitions, music cues, scripture readings.")
    }

    /// Write a pastoral care note (C# `WritePastoralCareNoteAsync`).
    public func writePastoralCareNote(parishionerSituation: String) async throws -> String {
        try await inner.agent(
            "Write a pastoral care note for: \(parishionerSituation). Acknowledge, hold space, offer concrete next step. Avoid platitudes.")
    }

    /// Find scripture passages on a theme (C# `FindScripturePassagesAsync`).
    public func findScripturePassages(tradition: String, theme: String) async throws -> String {
        try await inner.agent(
            "Find 3 scripture passages on '\(theme)' in the \(tradition). For each: reference, key verse text, brief context.")
    }
}
