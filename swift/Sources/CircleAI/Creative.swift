// Creative.swift
//
// Port of the Creative vertical from src/CircleAI.Creative/CreativePrimitives.cs
// and the static domain-context constants from CreativeDomainContext.cs:
//   • CreativeWork, Inspiration, Critique — domain records
//   • ICreativeBoard                      — works, inspiration, critiques
//   • InMemoryCreativeBoard               — deterministic in-memory impl
//   • CreativeDomainContext               — system-prompt snippet + flags
//
// The Companion-facing wrapper (CreativeCompanionAdapter) is an ICompanionSession
// decorator that prefixes the creative domain prompt.
//
// Porting notes:
//   • `DateTimeOffset` → `Date`.
//   • `WorksByTag` returns works whose Tags contain the tag (case-insensitive).
//   • `RecentInspiration(limit)` orders descending by SeenUtc, take limit (20).
//   • `AvgScore` averages critique scores for the work; 0.0 when none (mirrors
//     C# DefaultIfEmpty(0).Average()). All state guarded by a single `NSLock`.

import Foundation

// MARK: - Records

/// A creative work.
public struct CreativeWork: Sendable, Equatable, Codable {
    public let workId: String
    public let title: String
    public let medium: String
    public let author: String
    public let createdUtc: Date
    public let tags: [String]

    public init(workId: String, title: String, medium: String, author: String, createdUtc: Date, tags: [String]) {
        self.workId = workId
        self.title = title
        self.medium = medium
        self.author = author
        self.createdUtc = createdUtc
        self.tags = tags
    }
}

/// A recorded inspiration.
public struct Inspiration: Sendable, Equatable, Codable {
    public let inspirationId: String
    public let promptText: String
    public let sourceUrl: String
    public let seenUtc: Date

    public init(inspirationId: String, promptText: String, sourceUrl: String, seenUtc: Date) {
        self.inspirationId = inspirationId
        self.promptText = promptText
        self.sourceUrl = sourceUrl
        self.seenUtc = seenUtc
    }
}

/// A critique of a work.
public struct Critique: Sendable, Equatable, Codable {
    public let critiqueId: String
    public let workId: String
    public let reviewer: String
    public let body: String
    public let score: Int

    public init(critiqueId: String, workId: String, reviewer: String, body: String, score: Int) {
        self.critiqueId = critiqueId
        self.workId = workId
        self.reviewer = reviewer
        self.body = body
        self.score = score
    }
}

// MARK: - Contract

/// Works, inspiration, and critiques for the creative vertical.
public protocol ICreativeBoard: AnyObject, Sendable {
    func addWork(_ w: CreativeWork)
    func getWork(_ id: String) -> CreativeWork?
    func worksByTag(_ tag: String) -> [CreativeWork]
    func recordInspiration(_ i: Inspiration)
    func recentInspiration(limit: Int) -> [Inspiration]
    func addCritique(_ c: Critique)
    func avgScore(workId: String) -> Double
}

public extension ICreativeBoard {
    /// Convenience overload mirroring the C# default `limit = 20`.
    func recentInspiration() -> [Inspiration] { recentInspiration(limit: 20) }
}

// MARK: - InMemoryCreativeBoard

/// Deterministic in-memory `ICreativeBoard`. All state guarded by a single `NSLock`.
public final class InMemoryCreativeBoard: ICreativeBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var works: [String: CreativeWork] = [:]
    private var inspiration: [Inspiration] = []
    private var critiques: [Critique] = []

    public init() {}

    public func addWork(_ w: CreativeWork) {
        lock.lock(); defer { lock.unlock() }
        works[w.workId] = w
    }

    public func getWork(_ id: String) -> CreativeWork? {
        lock.lock(); defer { lock.unlock() }
        return works[id]
    }

    public func worksByTag(_ tag: String) -> [CreativeWork] {
        lock.lock(); defer { lock.unlock() }
        return works.values.filter { w in w.tags.contains { $0.caseInsensitiveCompare(tag) == .orderedSame } }
    }

    public func recordInspiration(_ i: Inspiration) {
        lock.lock(); defer { lock.unlock() }
        inspiration.append(i)
    }

    public func recentInspiration(limit: Int = 20) -> [Inspiration] {
        lock.lock(); defer { lock.unlock() }
        return Array(inspiration.sorted { $0.seenUtc > $1.seenUtc }.prefix(limit))
    }

    public func addCritique(_ c: Critique) {
        lock.lock(); defer { lock.unlock() }
        critiques.append(c)
    }

    public func avgScore(workId: String) -> Double {
        lock.lock(); defer { lock.unlock() }
        let scores = critiques.filter { $0.workId == workId }.map { Double($0.score) }
        if scores.isEmpty { return 0.0 }
        return scores.reduce(0.0, +) / Double(scores.count)
    }

    /// Number of works catalogued (matches C#'s `WorkCount`).
    public var workCount: Int {
        lock.lock(); defer { lock.unlock() }
        return works.count
    }

    /// Remove a work by id; if it existed, also drops all its critiques
    /// (cascade). Returns true if the work was present (matches C#'s
    /// `RemoveWork`).
    @discardableResult
    public func removeWork(_ workId: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        let removed = works.removeValue(forKey: workId) != nil
        if removed { critiques.removeAll { $0.workId == workId } }
        return removed
    }

    /// Works by a given author (case-insensitive), newest first. Matches C#'s
    /// `WorksByAuthor` → `OrderByDescending(CreatedUtc)`.
    public func worksByAuthor(_ author: String) -> [CreativeWork] {
        lock.lock(); defer { lock.unlock() }
        return works.values
            .filter { $0.author.caseInsensitiveCompare(author) == .orderedSame }
            .sorted { $0.createdUtc > $1.createdUtc }
    }

    /// Works in a given medium (case-insensitive), newest first. Matches C#'s
    /// `WorksByMedium` → `OrderByDescending(CreatedUtc)`.
    public func worksByMedium(_ medium: String) -> [CreativeWork] {
        lock.lock(); defer { lock.unlock() }
        return works.values
            .filter { $0.medium.caseInsensitiveCompare(medium) == .orderedSame }
            .sorted { $0.createdUtc > $1.createdUtc }
    }

    /// The highest average-scored work that still exists, or nil. Matches C#'s
    /// `TopRatedWork` (groups critiques by workId ordinally; ties keep
    /// first-appearance order; skips works that were removed).
    public func topRatedWork() -> CreativeWork? {
        lock.lock(); defer { lock.unlock() }
        var order: [String] = []
        var sums: [String: Double] = [:]
        var counts: [String: Int] = [:]
        for c in critiques {
            if counts[c.workId] == nil { order.append(c.workId) }
            sums[c.workId, default: 0] += Double(c.score)
            counts[c.workId, default: 0] += 1
        }
        let ranked = order.enumerated().sorted { a, b in
            let avgA = sums[a.element]! / Double(counts[a.element]!)
            let avgB = sums[b.element]! / Double(counts[b.element]!)
            if avgA != avgB { return avgA > avgB }
            return a.offset < b.offset
        }
        for entry in ranked {
            if let w = works[entry.element] { return w }
        }
        return nil
    }

    /// All distinct tags across works (case-insensitive), ordered
    /// case-insensitively ascending. Matches C#'s `AllTags` →
    /// `Distinct(OrdinalIgnoreCase).OrderBy(OrdinalIgnoreCase)` (first-seen
    /// casing kept for each tag).
    public func allTags() -> [String] {
        lock.lock(); defer { lock.unlock() }
        var seen = Set<String>()
        var distinct: [String] = []
        for w in works.values {
            for t in w.tags {
                let key = t.lowercased()
                if !seen.contains(key) { seen.insert(key); distinct.append(t) }
            }
        }
        return distinct.sorted { $0.caseInsensitiveCompare($1) == .orderedAscending }
    }
}

// MARK: - CreativeDomainContext

/// Static domain-context constants for the creative vertical.
public enum CreativeDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Creative] Imaginative creative arts companion. Help with storytelling, poetry, worldbuilding, visual art direction, music lyrics, creative briefs, and overcoming creative blocks. Encourage experimentation and original voice. Compliance: Copyright Act 98/1978, POPIA."
    public static let complianceFlags: [String] = ["Copyright_Act_98_1978", "POPIA"]
    public static let suggestedTools: [String] = ["writing_tools", "image_tools", "music_tools", "document_editor"]
}

// MARK: - CreativeCompanionAdapter

/// An `ICompanionSession` decorator that prepends the creative domain system
/// prompt to every conversational call and adds creative helper methods.
/// Port of `CircleAI.Creative.CreativeCompanionAdapter`. Identity/context/feedback
/// are forwarded to the inner session; proactive events forward through the
/// inner session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class CreativeCompanionAdapter: ICompanionSession, @unchecked Sendable {
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

    private func enrich(_ m: String) -> String { "\(CreativeDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Creative helpers ──────────────────────────────────────────────────────

    /// Generate writing prompts (C# `GenerateWritingPromptAsync`).
    public func generateWritingPrompt(genre: String, mood: String) async throws -> String {
        try await inner.agent(
            "Generate 5 unique \(genre) writing prompts with a \(mood) tone. For each, include a character seed, central conflict, and opening line.")
    }

    /// Overcome a creative block (C# `OvercomeBlockAsync`).
    public func overcomeBlock(project: String, blockDescription: String) async throws -> String {
        try await inner.agent(
            "Help me overcome creative block on \(project). Block: \(blockDescription). Use lateral thinking techniques and suggest 3 unconventional approaches to re-ignite momentum.")
    }

    /// Generate a creative brief (C# `GenerateBriefAsync`).
    public func generateBrief(project: String, audience: String, deadline: String) async throws -> String {
        try await inner.agent(
            "Generate a creative brief for '\(project)' aimed at \(audience), due \(deadline). Include problem, success, tone, constraints, deliverables.")
    }

    /// Critique a work (C# `CritiqueWorkAsync`).
    public func critiqueWork(workDescription: String, criteria: String) async throws -> String {
        try await inner.agent(
            "Critique this work: \(workDescription) against \(criteria). Use 'I notice / I wonder / I suggest', no destructive framing.")
    }

    /// Suggest style references (C# `SuggestStyleReferencesAsync`).
    public func suggestStyleReferences(aesthetic: String, medium: String) async throws -> String {
        try await inner.agent(
            "Suggest 5 style references for \(aesthetic) in \(medium). For each: who/when/why-fits.")
    }

    /// Unblock a creative state (C# `UnblockCreativeAsync`).
    public func unblockCreative(currentState: String, blocker: String) async throws -> String {
        try await inner.agent(
            "Help unblock this creative state: \(currentState). Blocker: \(blocker). Offer 3 different reframes + one micro-exercise.")
    }
}
