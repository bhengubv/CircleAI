// PersonalMental.swift
//
// Port of the Personal.Mental vertical from
// src/CircleAI.Personal.Mental/PersonalMentalPrimitives.cs and the static
// domain-context constants from PersonalMentalDomainContext.cs:
//   • Mood (enum), MoodLog, JournalEntry, CopingStrategy — records
//   • IMentalHealthBoard        — mood logs / journal / coping strategies / trend
//   • InMemoryMentalHealthBoard — deterministic in-memory impl
//   • PersonalMentalDomainContext — system-prompt snippet + flags
//
// The Companion-facing wrapper (PersonalMentalCompanionAdapter) is intentionally
// NOT ported (see Healthcare.swift for the rationale).
//
// Porting notes:
//   • `Mood` is an `Int`-backed enum so its ordinal (0…4) matches the C# enum,
//     which `AvgMood7Day` averages via `(int)m.Mood`.
//   • `DateTimeOffset` → `Date`. `Last7Days` uses a cutoff of now − 7 days and
//     orders ascending by time.
//   • `AddEntry` requires a non-blank `entryId` (`ArgumentException`) →
//     `MentalHealthError.entryIdRequired`.
//   • `StrategiesByTag` requires a non-blank tag → `MentalHealthError.tagRequired`;
//     tag matching is case-insensitive.
//   • `Entries` is ordered descending by time. `AvgMood7Day` returns `Double.nan`
//     for an empty 7-day window (C# `double.NaN`).

import Foundation

// MARK: - Enums

/// A self-reported mood level. `Int`-backed so ordinals match the C# enum
/// (VeryLow = 0 … Great = 4).
public enum Mood: Int, Sendable, Codable, CaseIterable {
    case veryLow = 0
    case low
    case neutral
    case good
    case great
}

// MARK: - Records

/// A single mood log entry.
public struct MoodLog: Sendable, Equatable, Codable {
    /// The logged mood.
    public let mood: Mood
    /// UTC timestamp.
    public let atUtc: Date
    /// Optional note.
    public let note: String?

    public init(mood: Mood, atUtc: Date, note: String?) {
        self.mood = mood
        self.atUtc = atUtc
        self.note = note
    }
}

/// A journal entry.
public struct JournalEntry: Sendable, Equatable, Codable {
    /// Stable identifier for the entry.
    public let entryId: String
    /// Entry title.
    public let title: String
    /// Entry body.
    public let body: String
    /// UTC timestamp.
    public let atUtc: Date

    public init(entryId: String, title: String, body: String, atUtc: Date) {
        self.entryId = entryId
        self.title = title
        self.body = body
        self.atUtc = atUtc
    }
}

/// A reusable coping strategy in the strategy library.
public struct CopingStrategy: Sendable, Equatable, Codable {
    /// Stable identifier for the strategy.
    public let strategyId: String
    /// Strategy title.
    public let title: String
    /// Strategy description.
    public let description: String
    /// Tags used to categorise / search the strategy.
    public let tags: [String]

    public init(strategyId: String, title: String, description: String, tags: [String]) {
        self.strategyId = strategyId
        self.title = title
        self.description = description
        self.tags = tags
    }
}

// MARK: - Errors

/// Errors thrown by the mental-health board.
public enum MentalHealthError: Error, Equatable, CustomStringConvertible {
    /// `addEntry` received a journal entry with a blank id.
    case entryIdRequired
    /// `strategiesByTag` was called with a blank tag.
    case tagRequired

    public var description: String {
        switch self {
        case .entryIdRequired: return "EntryId required"
        case .tagRequired: return "tag required"
        }
    }
}

// MARK: - IMentalHealthBoard

/// Mood logs, journal entries, a coping-strategy library, and a 7-day mood
/// trend for the mental-health vertical. Per-user instance only. A synchronous
/// contract — implementations are expected to be thread-safe.
public protocol IMentalHealthBoard: AnyObject, Sendable {
    /// Logs a mood entry.
    func logMood(_ m: MoodLog)
    /// Mood logs from the last 7 days, ascending by time.
    func last7Days() -> [MoodLog]
    /// Adds (or replaces, by `entryId`) a journal entry. Throws on a blank id.
    func addEntry(_ e: JournalEntry) throws
    /// Journal entries, most-recent first.
    var entries: [JournalEntry] { get }
    /// Registers (or replaces, by `strategyId`) a coping strategy.
    func registerStrategy(_ s: CopingStrategy)
    /// Strategies tagged `tag` (case-insensitive). Throws on a blank tag.
    func strategiesByTag(_ tag: String) throws -> [CopingStrategy]
    /// Mean mood ordinal over the last 7 days, or `Double.nan` if none.
    func avgMood7Day() -> Double
}

// MARK: - InMemoryMentalHealthBoard

/// Deterministic in-memory `IMentalHealthBoard`. All state is guarded by a
/// single `NSLock`; every accessor returns an immutable snapshot.
public final class InMemoryMentalHealthBoard: IMentalHealthBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var moods: [MoodLog] = []
    private var entriesById: [String: JournalEntry] = [:]
    private var strats: [String: CopingStrategy] = [:]

    public init() {}

    public func logMood(_ m: MoodLog) {
        lock.lock(); defer { lock.unlock() }
        moods.append(m)
    }

    public func last7Days() -> [MoodLog] {
        let cutoff = Date().addingTimeInterval(-7 * 24 * 60 * 60)
        lock.lock(); defer { lock.unlock() }
        return moods.filter { $0.atUtc >= cutoff }.sorted { $0.atUtc < $1.atUtc }
    }

    public func addEntry(_ e: JournalEntry) throws {
        if e.entryId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw MentalHealthError.entryIdRequired
        }
        lock.lock(); defer { lock.unlock() }
        entriesById[e.entryId] = e
    }

    public var entries: [JournalEntry] {
        lock.lock(); defer { lock.unlock() }
        return entriesById.values.sorted { $0.atUtc > $1.atUtc }
    }

    public func registerStrategy(_ s: CopingStrategy) {
        lock.lock(); defer { lock.unlock() }
        strats[s.strategyId] = s
    }

    public func strategiesByTag(_ tag: String) throws -> [CopingStrategy] {
        if tag.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw MentalHealthError.tagRequired }
        lock.lock(); defer { lock.unlock() }
        return strats.values.filter { s in
            s.tags.contains { $0.caseInsensitiveCompare(tag) == .orderedSame }
        }
    }

    public func avgMood7Day() -> Double {
        let items = last7Days()
        if items.isEmpty { return Double.nan }
        let total = items.reduce(0) { $0 + $1.mood.rawValue }
        return Double(total) / Double(items.count)
    }
}

// MARK: - PersonalMentalDomainContext

/// Static domain-context constants for the mental-health vertical. Mirrors
/// `PersonalMentalDomainContext` in PersonalMentalDomainContext.cs.
public enum PersonalMentalDomainContext {
    /// System-prompt snippet injected ahead of user turns in this domain.
    public static let systemPromptSnippet = "[DOMAIN: Personal.Mental] Warm, empathetic mental wellness companion. Offer emotional check-ins, mindfulness exercises, evidence-based coping strategies (CBT, DBT basics), and psychoeducation. Never diagnose. Always validate feelings before offering tools. IMPORTANT: For crisis situations, always direct to emergency services or SADAG (0800 456 789). Not a substitute for professional therapy. Compliance: POPIA, Mental Health Care Act."
    /// Regulatory / compliance flags relevant to this domain.
    public static let complianceFlags: [String] = ["POPIA", "Mental_Health_Care_Act_17_2002", "Not_Therapy", "Crisis_Protocol"]
    /// Tools the Companion may suggest in this domain.
    public static let suggestedTools: [String] = ["journal", "breathing_tools", "mood_tracker", "web_search"]
}
