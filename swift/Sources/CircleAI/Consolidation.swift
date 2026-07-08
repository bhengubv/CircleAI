// Consolidation.swift
// Hierarchical memory consolidation — the "sleep cycle" engine. Ported from
// CircleAI.Memory.Consolidation (C#): SleepKind, CoreMemory, DailyMemorySummary,
// SemanticMemoryCluster, PersonaDeltaSnapshot, the four tier stores, the
// HeuristicSummarizer, and the MemoryConsolidator orchestration engine. Mirrors
// the verified TS port 1:1.
//
// Promotes episodic → daily → weekly (semantic) → monthly (persona delta) →
// core, and enforces retention. All time decisions go through an injectable
// clock so tests are deterministic. This is the in-memory port: identical
// algorithms and formulas to the C# reference, no persistence.
//
// C# `DateOnly` is represented here as a "YYYY-MM-DD" UTC string. ISO date
// strings compare correctly with `<`/`<=`/`>=`, so the range/idempotency/prune
// comparisons carry over unchanged.

import Foundation

// MARK: - SleepKind + CoreMemoryKind

/// Which tier of hierarchical consolidation a tick should run.
public enum SleepKind: String, Sendable {
    /// End-of-day: collapse the day's episodic entries into a DailyMemorySummary.
    case daily = "Daily"
    /// End-of-week: cluster the week's daily summaries into semantic topic groups.
    case weekly = "Weekly"
    /// End-of-month: compute the persona delta and write a PersonaDeltaSnapshot.
    case monthly = "Monthly"
    /// Caller-initiated pass — runs whichever tiers have work pending.
    case onDemand = "OnDemand"
}

/// Why a memory was promoted to the core tier.
public enum CoreMemoryKind: String, Sendable {
    /// A fact the user explicitly asked the AI to remember.
    case userAsserted = "UserAsserted"
    /// Inferred from interaction patterns — a long-standing preference / theme.
    case patternInferred = "PatternInferred"
    /// Promoted because of extreme salience.
    case highSalience = "HighSalience"
    /// Promoted by the host directly (profile sync, identity bootstrap).
    case hostProvided = "HostProvided"
}

// MARK: - Tier records

/// A core memory the AI will not forget. Compact by design. Reference type
/// because the store mutates `lastReinforcedUtc` and `reinforcementCount` in
/// place (see `InMemoryCoreMemoryStore.reinforce`).
public final class CoreMemory: @unchecked Sendable {
    /// Stable identifier.
    public let id: UUID
    /// UTC time the memory was committed to core.
    public let createdAtUtc: Date
    /// UTC time the memory was last reinforced (re-asserted, re-cited). Mutable.
    public var lastReinforcedUtc: Date
    /// Short, dense statement of the memory, third-person from the AI's view.
    public let statement: String
    /// How the memory came to be in core.
    public let kind: CoreMemoryKind
    /// Optional topic label (e.g. "family", "career", "health").
    public let topic: String?
    /// Embedding of the statement for retrieval; nil when unavailable.
    public let embedding: [Float]?
    /// How many times this memory has been reinforced. Mutable.
    public var reinforcementCount: Int
    /// Trace back to the lower-tier source memory, if one exists.
    public let sourceMemoryId: UUID?

    /// Builds a CoreMemory with C#-equivalent defaults (new id, now timestamps).
    public init(statement: String = "",
                kind: CoreMemoryKind = .userAsserted,
                topic: String? = nil,
                embedding: [Float]? = nil,
                sourceMemoryId: UUID? = nil,
                clock: () -> Date = { Date() }) {
        let now = clock()
        self.id = UUID()
        self.createdAtUtc = now
        self.lastReinforcedUtc = now
        self.statement = statement
        self.kind = kind
        self.topic = topic
        self.embedding = embedding
        self.reinforcementCount = 0
        self.sourceMemoryId = sourceMemoryId
    }
}

/// Compressed record of a single calendar day's worth of episodic memory.
public struct DailyMemorySummary: Sendable {
    /// Stable identifier.
    public let id: UUID
    /// The calendar day this summary covers ("YYYY-MM-DD", UTC).
    public let day: String
    /// UTC time the summary was produced.
    public let generatedAtUtc: Date
    /// Short prose summary of the day's gist.
    public let summary: String
    /// The most salient verbatim exchanges from the day (typically 3–5).
    public let highlightEntries: [EpisodicMemoryEntry]
    /// Total number of episodic entries collapsed into this summary.
    public let episodeCount: Int
    /// Aggregated topic weights across the day's exchanges (label → weight).
    public let topicWeights: [String: Float]
    /// Mean cosine-distance dispersion of the day's embeddings (0..1).
    public let topicDispersion: Double
    /// Salience score 0.0–1.0 assigned by the summariser.
    public let salience: Double

    /// Builds a DailyMemorySummary with C#-equivalent defaults.
    public init(day: String,
                summary: String = "",
                highlightEntries: [EpisodicMemoryEntry] = [],
                episodeCount: Int = 0,
                topicWeights: [String: Float] = [:],
                topicDispersion: Double = 0,
                salience: Double = 0,
                clock: () -> Date = { Date() }) {
        self.id = UUID()
        self.day = day
        self.generatedAtUtc = clock()
        self.summary = summary
        self.highlightEntries = highlightEntries
        self.episodeCount = episodeCount
        self.topicWeights = topicWeights
        self.topicDispersion = topicDispersion
        self.salience = salience
    }
}

/// Topic-coherent cluster of daily summaries — the "semantic memory" tier.
public struct SemanticMemoryCluster: Sendable {
    /// Stable identifier.
    public let id: UUID
    /// UTC time the cluster was produced.
    public let generatedAtUtc: Date
    /// The week this cluster covers — Monday of that week ("YYYY-MM-DD", UTC).
    public let weekStartingMonday: String
    /// Dominant topic label for this cluster.
    public let topic: String
    /// Short prose summary of the cluster's gist.
    public let summary: String
    /// Centroid embedding (mean of constituent embeddings); nil when unavailable.
    public let centroidEmbedding: [Float]?
    /// IDs of the daily summaries that contributed to this cluster.
    public let sourceDailyIds: [UUID]
    /// Aggregate weight of the topic across constituent days.
    public let topicWeight: Float
    /// Salience score 0.0–1.0.
    public let salience: Double

    /// Builds a SemanticMemoryCluster with C#-equivalent defaults.
    public init(weekStartingMonday: String,
                topic: String = "",
                summary: String = "",
                centroidEmbedding: [Float]? = nil,
                sourceDailyIds: [UUID] = [],
                topicWeight: Float = 0,
                salience: Double = 0,
                clock: () -> Date = { Date() }) {
        self.id = UUID()
        self.generatedAtUtc = clock()
        self.weekStartingMonday = weekStartingMonday
        self.topic = topic
        self.summary = summary
        self.centroidEmbedding = centroidEmbedding
        self.sourceDailyIds = sourceDailyIds
        self.topicWeight = topicWeight
        self.salience = salience
    }
}

/// Diff between a PersonaState at the start and end of a consolidation period.
public struct PersonaDeltaSnapshot: Sendable {
    /// Stable identifier.
    public let id: UUID
    /// UTC time the delta was captured.
    public let generatedAtUtc: Date
    /// Start of the period ("YYYY-MM-DD", UTC).
    public let periodStart: String
    /// End of the period ("YYYY-MM-DD", UTC).
    public let periodEnd: String
    /// User identifier.
    public let userId: String
    /// Verbosity at period start.
    public let verbosityBefore: String
    /// Verbosity at period end.
    public let verbosityAfter: String
    /// Formality at period start.
    public let formalityBefore: String
    /// Formality at period end.
    public let formalityAfter: String
    /// New topics that emerged in the period (label → accumulated weight).
    public let newTopics: [String: Float]
    /// Topics that gained the most weight (label → weight delta).
    public let strengthenedTopics: [String: Float]
    /// Topics the user explicitly down-voted during the period.
    public let newlyDisfavouredTopics: [String]
    /// Net positive minus negative signals across the period.
    public let netSignalDelta: Int
    /// Total interactions during the period.
    public let interactionsInPeriod: Int
    /// Short human-readable narrative of how the persona changed.
    public let narrative: String

    /// Builds a PersonaDeltaSnapshot with C#-equivalent defaults.
    public init(periodStart: String,
                periodEnd: String,
                userId: String = "default",
                verbosityBefore: String = "",
                verbosityAfter: String = "",
                formalityBefore: String = "",
                formalityAfter: String = "",
                newTopics: [String: Float] = [:],
                strengthenedTopics: [String: Float] = [:],
                newlyDisfavouredTopics: [String] = [],
                netSignalDelta: Int = 0,
                interactionsInPeriod: Int = 0,
                narrative: String = "",
                clock: () -> Date = { Date() }) {
        self.id = UUID()
        self.generatedAtUtc = clock()
        self.periodStart = periodStart
        self.periodEnd = periodEnd
        self.userId = userId
        self.verbosityBefore = verbosityBefore
        self.verbosityAfter = verbosityAfter
        self.formalityBefore = formalityBefore
        self.formalityAfter = formalityAfter
        self.newTopics = newTopics
        self.strengthenedTopics = strengthenedTopics
        self.newlyDisfavouredTopics = newlyDisfavouredTopics
        self.netSignalDelta = netSignalDelta
        self.interactionsInPeriod = interactionsInPeriod
        self.narrative = narrative
    }
}

/// Outcome of a single consolidator tick.
public struct ConsolidationOutcome: Sendable {
    public let kind: SleepKind
    public let dailySummariesProduced: Int
    public let semanticClustersProduced: Int
    public let personaDeltasProduced: Int
    public let corePromotions: Int
    public let episodesPruned: Int
    public let dailiesPruned: Int
    public let semanticsPruned: Int
    public let ranAtUtc: Date

    public init(kind: SleepKind, dailySummariesProduced: Int, semanticClustersProduced: Int,
                personaDeltasProduced: Int, corePromotions: Int, episodesPruned: Int,
                dailiesPruned: Int, semanticsPruned: Int, ranAtUtc: Date) {
        self.kind = kind
        self.dailySummariesProduced = dailySummariesProduced
        self.semanticClustersProduced = semanticClustersProduced
        self.personaDeltasProduced = personaDeltasProduced
        self.corePromotions = corePromotions
        self.episodesPruned = episodesPruned
        self.dailiesPruned = dailiesPruned
        self.semanticsPruned = semanticsPruned
        self.ranAtUtc = ranAtUtc
    }
}

/// Retention windows + core-promotion thresholds. Defaults follow the
/// hierarchical-memory plan: 7-day episodic, 30-day daily, 12-month semantic,
/// salience ≥ 0.80 promotes to core.
public struct MemoryConsolidationOptions: Sendable {
    /// Days of episodic entries to retain after they've been summarised.
    public let episodicRetentionDays: Int
    /// Days of daily summaries to retain after weekly consolidation.
    public let dailyRetentionDays: Int
    /// Days of semantic clusters to retain.
    public let semanticRetentionDays: Int
    /// Salience threshold above which daily summaries promote to core.
    public let dailyCorePromotionThreshold: Double
    /// Salience threshold above which weekly clusters promote to core.
    public let weeklyCorePromotionThreshold: Double

    public init(episodicRetentionDays: Int = 7,
                dailyRetentionDays: Int = 30,
                semanticRetentionDays: Int = 365,
                dailyCorePromotionThreshold: Double = 0.80,
                weeklyCorePromotionThreshold: Double = 0.75) {
        self.episodicRetentionDays = episodicRetentionDays
        self.dailyRetentionDays = dailyRetentionDays
        self.semanticRetentionDays = semanticRetentionDays
        self.dailyCorePromotionThreshold = dailyCorePromotionThreshold
        self.weeklyCorePromotionThreshold = weeklyCorePromotionThreshold
    }
}

// MARK: - Day helpers — "YYYY-MM-DD" UTC date arithmetic

/// A UTC calendar used for all day arithmetic (no DST, no locale drift).
private let utcCalendar: Calendar = {
    var cal = Calendar(identifier: .gregorian)
    cal.timeZone = TimeZone(identifier: "UTC")!
    return cal
}()

/// UTC calendar day of a Date, as "YYYY-MM-DD".
public func dayKey(from date: Date) -> String {
    let c = utcCalendar.dateComponents([.year, .month, .day], from: date)
    return formatDay(year: c.year!, month: c.month!, day: c.day!)
}

/// Parses a "YYYY-MM-DD" key back into a UTC Date at midnight.
private func parseDayKey(_ day: String) -> Date {
    let parts = day.split(separator: "-").map { Int($0) ?? 0 }
    var c = DateComponents()
    c.year = parts.count > 0 ? parts[0] : 0
    c.month = parts.count > 1 ? parts[1] : 1
    c.day = parts.count > 2 ? parts[2] : 1
    return utcCalendar.date(from: c)!
}

private func formatDay(year: Int, month: Int, day: Int) -> String {
    let y = String(format: "%04d", year)
    let m = String(format: "%02d", month)
    let d = String(format: "%02d", day)
    return "\(y)-\(m)-\(d)"
}

/// Adds `days` (may be negative) to a "YYYY-MM-DD" key.
public func addDays(_ day: String, _ days: Int) -> String {
    let dt = utcCalendar.date(byAdding: .day, value: days, to: parseDayKey(day))!
    return dayKey(from: dt)
}

/// The Monday of the week containing `day`. Monday = d minus ((dow+6)%7) days
/// (Sunday=0). Mirrors the C# `((int)DayOfWeek + 6) % 7`.
public func mondayOf(_ day: String) -> String {
    // Foundation weekday: Sunday=1..Saturday=7 → map to Sunday=0..Saturday=6.
    let weekday = utcCalendar.component(.weekday, from: parseDayKey(day))
    let dow = weekday - 1                 // Sun=0..Sat=6
    let delta = (dow + 6) % 7             // Sun=0..Sat=6 → Mon=0..Sun=6
    return addDays(day, -delta)
}

/// Four-digit year of a "YYYY-MM-DD" key.
public func yearOf(_ day: String) -> Int {
    utcCalendar.component(.year, from: parseDayKey(day))
}

/// 1-based month of a "YYYY-MM-DD" key.
public func monthOf(_ day: String) -> Int {
    utcCalendar.component(.month, from: parseDayKey(day))
}

/// First day of the month containing `day`, as "YYYY-MM-DD".
public func monthFirstDay(of day: String) -> String {
    formatDay(year: yearOf(day), month: monthOf(day), day: 1)
}

// MARK: - Cosine — FULL cosine (differs from the episodic store's dot-only cosine)

/// Full cosine similarity: dot / (‖a‖·‖b‖). Returns 0 on a length mismatch or a
/// near-zero denominator (matches C# `double.Epsilon` via
/// `Double.leastNonzeroMagnitude`). This does NOT assume the vectors are
/// L2-normalised, so it differs from the episodic store's dot-product cosine —
/// both are kept.
public func cosineFull(_ a: [Float], _ b: [Float]) -> Double {
    if a.count != b.count { return 0 }
    var dot = 0.0, magA = 0.0, magB = 0.0
    for i in 0..<a.count {
        let ai = Double(a[i]), bi = Double(b[i])
        dot += ai * bi
        magA += ai * ai
        magB += bi * bi
    }
    let denom = magA.squareRoot() * magB.squareRoot()
    return denom < Double.leastNonzeroMagnitude ? 0 : dot / denom
}

// MARK: - Store protocols

/// Persistent store for tier-2 daily summaries.
public protocol IDailyMemoryStore {
    /// Adds a daily summary. Replaces any existing entry for the same day.
    func upsert(_ summary: DailyMemorySummary) async throws
    /// Returns the summary for the given day, or nil when none exists.
    func get(day: String) async throws -> DailyMemorySummary?
    /// Returns all summaries between fromInclusive and toInclusive (day-ordered).
    func getRange(fromInclusive: String, toInclusive: String) async throws -> [DailyMemorySummary]
    /// Removes summaries older than cutoff. Returns count removed.
    func pruneOlderThan(cutoff: String) async throws -> Int
    /// Total summaries currently stored.
    func count() async throws -> Int
}

/// Persistent store for tier-3 semantic memory clusters.
public protocol ISemanticMemoryStore {
    /// Adds a cluster.
    func add(_ cluster: SemanticMemoryCluster) async throws
    /// Returns all clusters for the given week, ordered by topicWeight desc.
    func getWeek(weekStartingMonday: String) async throws -> [SemanticMemoryCluster]
    /// Top-topK clusters by centroid cosine similarity; recency fallback when nil.
    func search(queryEmbedding: [Float]?, topK: Int) async throws -> [SemanticMemoryCluster]
    /// Removes clusters whose week start is before cutoff.
    func pruneOlderThan(cutoff: String) async throws -> Int
    /// Total clusters currently stored.
    func count() async throws -> Int
}

/// Persistent store for tier-4 persona-delta snapshots. Retained forever.
public protocol IPersonaDeltaStore {
    /// Adds a delta snapshot.
    func add(_ snapshot: PersonaDeltaSnapshot) async throws
    /// Returns all snapshots for the given user, ordered by periodStart.
    func getForUser(userId: String) async throws -> [PersonaDeltaSnapshot]
    /// Total snapshots currently stored.
    func count() async throws -> Int
}

/// Persistent store for tier-5 core memories — things the AI will not forget.
public protocol ICoreMemoryStore {
    /// Adds a core memory.
    func add(_ memory: CoreMemory) async throws
    /// Returns a core memory by id, or nil when not found.
    func get(id: UUID) async throws -> CoreMemory?
    /// Top-topK core memories by embedding cosine; reinforcement-order fallback when nil.
    func search(queryEmbedding: [Float]?, topK: Int) async throws -> [CoreMemory]
    /// All core memories in reinforcement order (most reinforced first).
    func listAll() async throws -> [CoreMemory]
    /// Increments reinforcementCount and bumps lastReinforcedUtc. No-op when unknown.
    func reinforce(id: UUID) async throws
    /// Removes a core memory.
    func remove(id: UUID) async throws -> Bool
    /// Total core memories currently stored.
    func count() async throws -> Int
}

// MARK: - In-memory store implementations

/// In-memory `IDailyMemoryStore`.
public final class InMemoryDailyMemoryStore: IDailyMemoryStore, @unchecked Sendable {
    private let lock = NSLock()
    private var store: [String: DailyMemorySummary] = [:]

    public init() {}

    // Synchronous, lock-guarded accessors — safe to call from async contexts
    // (the lock is never held across an await).
    private func put(_ summary: DailyMemorySummary) { lock.lock(); defer { lock.unlock() }; store[summary.day] = summary }
    private func fetch(_ day: String) -> DailyMemorySummary? { lock.lock(); defer { lock.unlock() }; return store[day] }
    private func range(_ from: String, _ to: String) -> [DailyMemorySummary] {
        lock.lock(); defer { lock.unlock() }
        return store.values.filter { $0.day >= from && $0.day <= to }.sorted { $0.day < $1.day }
    }
    private func prune(_ cutoff: String) -> Int {
        lock.lock(); defer { lock.unlock() }
        let toRemove = store.keys.filter { $0 < cutoff }
        for d in toRemove { store.removeValue(forKey: d) }
        return toRemove.count
    }
    private func size() -> Int { lock.lock(); defer { lock.unlock() }; return store.count }

    public func upsert(_ summary: DailyMemorySummary) async throws { put(summary) }
    public func get(day: String) async throws -> DailyMemorySummary? { fetch(day) }
    public func getRange(fromInclusive: String, toInclusive: String) async throws -> [DailyMemorySummary] {
        range(fromInclusive, toInclusive)
    }
    public func pruneOlderThan(cutoff: String) async throws -> Int { prune(cutoff) }
    public func count() async throws -> Int { size() }
}

/// In-memory `ISemanticMemoryStore`.
public final class InMemorySemanticMemoryStore: ISemanticMemoryStore, @unchecked Sendable {
    private let lock = NSLock()
    private var store: [SemanticMemoryCluster] = []

    public init() {}

    private func append(_ cluster: SemanticMemoryCluster) { lock.lock(); defer { lock.unlock() }; store.append(cluster) }
    private func snapshot() -> [SemanticMemoryCluster] { lock.lock(); defer { lock.unlock() }; return store }
    private func prune(_ cutoff: String) -> Int {
        lock.lock(); defer { lock.unlock() }
        let before = store.count
        store.removeAll { $0.weekStartingMonday < cutoff }
        return before - store.count
    }
    private func size() -> Int { lock.lock(); defer { lock.unlock() }; return store.count }

    public func add(_ cluster: SemanticMemoryCluster) async throws { append(cluster) }

    public func getWeek(weekStartingMonday: String) async throws -> [SemanticMemoryCluster] {
        snapshot()
            .filter { $0.weekStartingMonday == weekStartingMonday }
            .sorted { $0.topicWeight > $1.topicWeight }
    }

    public func search(queryEmbedding: [Float]?, topK: Int = 5) async throws -> [SemanticMemoryCluster] {
        let all = snapshot()
        guard let q = queryEmbedding else {
            return Array(all.sorted { $0.generatedAtUtc > $1.generatedAtUtc }.prefix(topK))
        }
        return all
            .filter { $0.centroidEmbedding != nil }
            .map { (cluster: $0, score: cosineFull(q, $0.centroidEmbedding!)) }
            .sorted { $0.score > $1.score }
            .prefix(topK)
            .map { $0.cluster }
    }

    public func pruneOlderThan(cutoff: String) async throws -> Int { prune(cutoff) }
    public func count() async throws -> Int { size() }
}

/// In-memory `IPersonaDeltaStore`.
public final class InMemoryPersonaDeltaStore: IPersonaDeltaStore, @unchecked Sendable {
    private let lock = NSLock()
    private var store: [PersonaDeltaSnapshot] = []

    public init() {}

    private func append(_ snapshot: PersonaDeltaSnapshot) { lock.lock(); defer { lock.unlock() }; store.append(snapshot) }
    private func snapshot() -> [PersonaDeltaSnapshot] { lock.lock(); defer { lock.unlock() }; return store }
    private func size() -> Int { lock.lock(); defer { lock.unlock() }; return store.count }

    public func add(_ snapshot: PersonaDeltaSnapshot) async throws { append(snapshot) }

    public func getForUser(userId: String) async throws -> [PersonaDeltaSnapshot] {
        snapshot()
            .filter { $0.userId == userId }
            .sorted { $0.periodStart < $1.periodStart }
    }

    public func count() async throws -> Int { size() }
}

/// In-memory `ICoreMemoryStore`.
public final class InMemoryCoreMemoryStore: ICoreMemoryStore, @unchecked Sendable {
    private let lock = NSLock()
    private var store: [UUID: CoreMemory] = [:]

    public init() {}

    private func put(_ memory: CoreMemory) { lock.lock(); defer { lock.unlock() }; store[memory.id] = memory }
    private func fetch(_ id: UUID) -> CoreMemory? { lock.lock(); defer { lock.unlock() }; return store[id] }
    private func values() -> [CoreMemory] { lock.lock(); defer { lock.unlock() }; return Array(store.values) }
    private func bump(_ id: UUID) {
        lock.lock(); defer { lock.unlock() }
        if let memory = store[id] {
            memory.reinforcementCount += 1
            memory.lastReinforcedUtc = Date()
        }
    }
    private func drop(_ id: UUID) -> Bool { lock.lock(); defer { lock.unlock() }; return store.removeValue(forKey: id) != nil }
    private func size() -> Int { lock.lock(); defer { lock.unlock() }; return store.count }

    public func add(_ memory: CoreMemory) async throws { put(memory) }
    public func get(id: UUID) async throws -> CoreMemory? { fetch(id) }

    public func search(queryEmbedding: [Float]?, topK: Int = 5) async throws -> [CoreMemory] {
        let all = values()
        guard let q = queryEmbedding else {
            return Array(all.sorted(by: byReinforcement).prefix(topK))
        }
        return all
            .filter { $0.embedding != nil }
            .map { (memory: $0, score: cosineFull(q, $0.embedding!)) }
            .sorted { $0.score > $1.score }
            .prefix(topK)
            .map { $0.memory }
    }

    public func listAll() async throws -> [CoreMemory] { values().sorted(by: byReinforcement) }
    public func reinforce(id: UUID) async throws { bump(id) }
    public func remove(id: UUID) async throws -> Bool { drop(id) }
    public func count() async throws -> Int { size() }
}

/// Sort: reinforcementCount desc, then lastReinforcedUtc desc.
private func byReinforcement(_ a: CoreMemory, _ b: CoreMemory) -> Bool {
    if a.reinforcementCount != b.reinforcementCount {
        return a.reinforcementCount > b.reinforcementCount
    }
    return a.lastReinforcedUtc > b.lastReinforcedUtc
}

// MARK: - IMemorySummarizer + HeuristicSummarizer

/// Produces the text + scores for each consolidation tier.
public protocol IMemorySummarizer {
    /// Produces a DailyMemorySummary from the day's episodic entries.
    func summarizeDay(day: String, entries: [EpisodicMemoryEntry]) async throws -> DailyMemorySummary
    /// Produces zero or more SemanticMemoryCluster records from a week's dailies.
    func consolidateWeek(weekStartingMonday: String,
                         daysInWeek: [DailyMemorySummary]) async throws -> [SemanticMemoryCluster]
    /// Computes the PersonaDeltaSnapshot across the period.
    func derivePersonaDelta(before: PersonaState, after: PersonaState,
                            daysInPeriod: [DailyMemorySummary]) async throws -> PersonaDeltaSnapshot
}

/// Heuristic `IMemorySummarizer` that requires no LLM. Produces summaries
/// entirely from structural signals — embedding clustering, topic-weight
/// aggregation, length-and-recency salience. Formulas are identical to the C#
/// HeuristicSummarizer.
public final class HeuristicSummarizer: IMemorySummarizer, @unchecked Sendable {
    /// Max high-salience verbatim entries kept per DailyMemorySummary.
    public let highlightCount: Int
    /// Min contributing days a topic needs across a week to form a cluster.
    public let minDaysPerTopicForCluster: Int
    private let clock: () -> Date

    public init(highlightCount: Int = 5,
                minDaysPerTopicForCluster: Int = 2,
                clock: @escaping () -> Date = { Date() }) {
        self.highlightCount = highlightCount
        self.minDaysPerTopicForCluster = minDaysPerTopicForCluster
        self.clock = clock
    }

    // ── summarizeDay ─────────────────────────────────────────────────────────

    public func summarizeDay(day: String, entries: [EpisodicMemoryEntry]) async throws -> DailyMemorySummary {
        if entries.isEmpty {
            return DailyMemorySummary(day: day,
                                      summary: "No exchanges recorded on \(day).",
                                      episodeCount: 0,
                                      clock: clock)
        }

        let topicWeights = Self.aggregateTopicWeights(entries)
        let dispersion = Self.meanPairwiseCosineDistance(entries)
        let highlights = Self.selectHighlights(entries, highlightCount)
        let salience = Self.computeDailySalience(entries.count, topicWeights, dispersion)
        let summary = Self.buildDailySummaryText(day, entries.count, topicWeights, highlights)

        return DailyMemorySummary(day: day,
                                  summary: summary,
                                  highlightEntries: highlights,
                                  episodeCount: entries.count,
                                  topicWeights: topicWeights,
                                  topicDispersion: dispersion,
                                  salience: salience,
                                  clock: clock)
    }

    // ── consolidateWeek ───────────────────────────────────────────────────────

    public func consolidateWeek(weekStartingMonday: String,
                                daysInWeek: [DailyMemorySummary]) async throws -> [SemanticMemoryCluster] {
        if daysInWeek.isEmpty { return [] }

        // Tally how many days each topic appeared in and its cumulative weight.
        // Topics are compared case-insensitively (mirrors StringComparer.OrdinalIgnoreCase);
        // topic labels arrive already lowercased from aggregateTopicWeights.
        var topicToDays: [String: [DailyMemorySummary]] = [:]
        var topicToWeight: [String: Float] = [:]

        for d in daysInWeek {
            for (topic, w) in d.topicWeights {
                topicToDays[topic, default: []].append(d)
                topicToWeight[topic, default: 0] += w
            }
        }

        var totalWeight = topicToWeight.values.reduce(0, +)
        if totalWeight <= 0 { totalWeight = 1 }

        var clusters: [SemanticMemoryCluster] = []
        let topicsByWeightDesc = topicToWeight.keys.sorted { topicToWeight[$0]! > topicToWeight[$1]! }
        for topic in topicsByWeightDesc {
            let contributingDays = topicToDays[topic]!
            if contributingDays.count < minDaysPerTopicForCluster { continue }

            let centroid = Self.centroidOfHighlights(contributingDays)
            let weight = topicToWeight[topic]!
            let clusterSalience = min(1.0,
                                      Double(weight) / Double(totalWeight)
                                      + (Double(contributingDays.count) / 7.0) * 0.25)

            clusters.append(SemanticMemoryCluster(weekStartingMonday: weekStartingMonday,
                                                  topic: topic,
                                                  summary: Self.buildWeeklyClusterText(topic, contributingDays),
                                                  centroidEmbedding: centroid,
                                                  sourceDailyIds: contributingDays.map { $0.id },
                                                  topicWeight: weight,
                                                  salience: clusterSalience,
                                                  clock: clock))
        }
        return clusters
    }

    // ── derivePersonaDelta ─────────────────────────────────────────────────────

    public func derivePersonaDelta(before: PersonaState, after: PersonaState,
                                   daysInPeriod: [DailyMemorySummary]) async throws -> PersonaDeltaSnapshot {
        var newTopics: [String: Float] = [:]
        var strengthened: [String: Float] = [:]
        for (topic, afterW) in after.topicWeights {
            let beforeW = before.topicWeights[topic] ?? 0
            let delta = afterW - beforeW
            if beforeW <= 0 && afterW > 0 {
                newTopics[topic] = afterW
            } else if delta > 0 {
                strengthened[topic] = delta
            }
        }

        let disfavouredNew = Array(after.disfavouredTopics.filter { !before.disfavouredTopics.contains($0) })

        let netSignals = (after.positiveSignals - before.positiveSignals)
                       - (after.negativeSignals - before.negativeSignals)
        let interactions = after.totalInteractions - before.totalInteractions

        let periodStart = daysInPeriod.isEmpty ? dayKey(from: after.lastUpdatedAt) : Self.minDay(daysInPeriod)
        let periodEnd = daysInPeriod.isEmpty ? dayKey(from: after.lastUpdatedAt) : Self.maxDay(daysInPeriod)

        let narrative = Self.buildPersonaNarrative(before, after, newTopics, strengthened,
                                                   disfavouredNew, netSignals, interactions,
                                                   periodStart, periodEnd)

        return PersonaDeltaSnapshot(periodStart: periodStart,
                                    periodEnd: periodEnd,
                                    userId: after.userId,
                                    verbosityBefore: before.verbosity,
                                    verbosityAfter: after.verbosity,
                                    formalityBefore: before.formality,
                                    formalityAfter: after.formality,
                                    newTopics: newTopics,
                                    strengthenedTopics: strengthened,
                                    newlyDisfavouredTopics: disfavouredNew,
                                    netSignalDelta: netSignals,
                                    interactionsInPeriod: interactions,
                                    narrative: narrative,
                                    clock: clock)
    }

    // ── Summarizer helpers — topic + dispersion ────────────────────────────────

    /// Topic weights from "topic" (+1) and pipe-split "topics" (each +1), lowercased.
    static func aggregateTopicWeights(_ entries: [EpisodicMemoryEntry]) -> [String: Float] {
        var weights: [String: Float] = [:]
        for e in entries {
            guard let tags = e.tags else { continue }
            if let t = tags["topic"], !t.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                accumulateTopic(&weights, t, 1)
            }
            if let multi = tags["topics"], !multi.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                for p in multi.split(separator: "|", omittingEmptySubsequences: true) {
                    accumulateTopic(&weights, String(p), 1)
                }
            }
        }
        return weights
    }

    private static func accumulateTopic(_ dict: inout [String: Float], _ topic: String, _ weight: Float) {
        let key = topic.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if key.isEmpty { return }
        dict[key, default: 0] += weight
    }

    /// Mean over all pairs of (1 - clamp(fullCosine,-1,1)); 0 when <2 embedded entries.
    static func meanPairwiseCosineDistance(_ entries: [EpisodicMemoryEntry]) -> Double {
        let withEmbeddings = entries.filter { hasEmbedding($0) }
        if withEmbeddings.count < 2 { return 0 }

        var total = 0.0
        var pairs = 0
        for i in 0..<withEmbeddings.count {
            for j in (i + 1)..<withEmbeddings.count {
                let sim = cosineFull(withEmbeddings[i].embedding!, withEmbeddings[j].embedding!)
                total += 1.0 - clampD(sim, -1.0, 1.0)
                pairs += 1
            }
        }
        return pairs == 0 ? 0 : clampD(total / Double(pairs), 0.0, 1.0)
    }

    /// Top-`count` entries by salience proxy (or all when ≤count), re-sorted by time.
    static func selectHighlights(_ entries: [EpisodicMemoryEntry], _ count: Int) -> [EpisodicMemoryEntry] {
        if entries.count <= count {
            return entries.sorted(by: byTimeAsc)
        }
        return entries
            .map { (entry: $0, score: entrySalienceProxy($0, entries)) }
            // OrderByDescending(score).ThenByDescending(recordedAt)
            .sorted { a, b in
                if a.score != b.score { return a.score > b.score }
                return a.entry.recordedAt > b.entry.recordedAt
            }
            .prefix(count)
            .map { $0.entry }
            .sorted(by: byTimeAsc)
    }

    private static func entrySalienceProxy(_ entry: EpisodicMemoryEntry, _ all: [EpisodicMemoryEntry]) -> Double {
        let lengthScore = min(1.0, Double(entry.userText.count + entry.assistantText.count) / 800.0)
        var uniquenessScore = 0.5
        if hasEmbedding(entry) {
            let others = all.filter { $0.id != entry.id && hasEmbedding($0) }
            if !others.isEmpty {
                var sum = 0.0
                for e in others { sum += cosineFull(entry.embedding!, e.embedding!) }
                let meanSim = sum / Double(others.count)
                uniquenessScore = 1.0 - clampD(meanSim, -1.0, 1.0)
            }
        }
        return lengthScore * 0.6 + uniquenessScore * 0.4
    }

    /// Daily salience = volume·0.4 + dispersion·0.3 + topicConcentration·0.3.
    static func computeDailySalience(_ episodeCount: Int,
                                     _ topicWeights: [String: Float],
                                     _ dispersion: Double) -> Double {
        let volumeScore = min(1.0, Double(episodeCount) / 30.0)
        let topicConcentration: Double
        if topicWeights.isEmpty {
            topicConcentration = 0.5
        } else {
            let maxW = topicWeights.values.max()!
            let sumW = topicWeights.values.reduce(0, +)
            topicConcentration = min(1.0, Double(maxW) / Double(max(1, sumW)))
        }
        return volumeScore * 0.4 + dispersion * 0.3 + topicConcentration * 0.3
    }

    /// Mean of all highlight embeddings across contributing days; nil when none.
    static func centroidOfHighlights(_ days: [DailyMemorySummary]) -> [Float]? {
        var allEmbeddings: [[Float]] = []
        for d in days {
            for e in d.highlightEntries where hasEmbedding(e) {
                allEmbeddings.append(e.embedding!)
            }
        }
        if allEmbeddings.isEmpty { return nil }
        let dim = allEmbeddings[0].count
        var centroid = [Float](repeating: 0, count: dim)
        for e in allEmbeddings {
            var i = 0
            while i < dim && i < e.count { centroid[i] += e[i]; i += 1 }
        }
        for i in 0..<dim { centroid[i] /= Float(allEmbeddings.count) }
        return centroid
    }

    // ── Summarizer helpers — text builders ──────────────────────────────────────

    static func buildDailySummaryText(_ day: String, _ count: Int,
                                      _ topics: [String: Float],
                                      _ highlights: [EpisodicMemoryEntry]) -> String {
        let topTopics = topics.sorted { $0.value > $1.value }.prefix(3).map { $0.key }
        let topicsClause = topTopics.isEmpty ? "" : " Top topics: \(topTopics.joined(separator: ", "))."
        let highlightClause = highlights.isEmpty ? ""
            : " Standout moment: \"\(truncate(highlights[0].userText, 120))\"."
        return "On \(day) you had \(count) "
            + (count == 1 ? "exchange." : "exchanges.")
            + topicsClause + highlightClause
    }

    static func buildWeeklyClusterText(_ topic: String, _ contributingDays: [DailyMemorySummary]) -> String {
        let totalEpisodes = contributingDays.reduce(0) { $0 + $1.episodeCount }
        return "Across \(contributingDays.count) days this week you returned to "
            + "\"\(topic)\" — \(totalEpisodes) exchanges in total."
    }

    static func buildPersonaNarrative(_ before: PersonaState, _ after: PersonaState,
                                      _ newTopics: [String: Float], _ strengthened: [String: Float],
                                      _ disfavoured: [String], _ netSignals: Int, _ interactions: Int,
                                      _ periodStart: String, _ periodEnd: String) -> String {
        var parts: [String] = []
        parts.append("Between \(periodStart) and \(periodEnd), \(interactions) interactions were recorded.")
        if !newTopics.isEmpty {
            parts.append("New interests appeared: " + topNKeys(newTopics, 3).joined(separator: ", ") + ".")
        }
        if !strengthened.isEmpty {
            parts.append("Existing interests deepened around " + topNKeys(strengthened, 3).joined(separator: ", ") + ".")
        }
        if !disfavoured.isEmpty {
            parts.append("Topics now avoided: " + disfavoured.joined(separator: ", ") + ".")
        }
        if before.verbosity != after.verbosity {
            parts.append("Preferred verbosity shifted from \(before.verbosity) to \(after.verbosity).")
        }
        if before.formality != after.formality {
            parts.append("Preferred tone shifted from \(before.formality) to \(after.formality).")
        }
        if netSignals != 0 {
            parts.append(netSignals > 0
                         ? "Net feedback was positive (+\(netSignals))."
                         : "Net feedback was negative (\(netSignals)).")
        }
        return parts.joined(separator: " ")
    }

    /// Keys of `map` ordered by value desc, top-n.
    private static func topNKeys(_ map: [String: Float], _ n: Int) -> [String] {
        map.sorted { $0.value > $1.value }.prefix(n).map { $0.key }
    }

    static func truncate(_ s: String, _ max: Int) -> String {
        if s.isEmpty { return "" }
        if s.count <= max { return s }
        let sliced = String(s.prefix(max))
        // C# TrimEnd() trims trailing whitespace before appending the ellipsis.
        let trimmed = String(sliced.reversed().drop(while: { $0.isWhitespace }).reversed())
        return trimmed + "…"
    }

    // ── Shared small helpers ────────────────────────────────────────────────────

    private static func minDay(_ days: [DailyMemorySummary]) -> String {
        var m = days[0].day
        for d in days where d.day < m { m = d.day }
        return m
    }

    private static func maxDay(_ days: [DailyMemorySummary]) -> String {
        var m = days[0].day
        for d in days where d.day > m { m = d.day }
        return m
    }
}

// Shared free helpers (file-private to the consolidation module).

private func hasEmbedding(_ e: EpisodicMemoryEntry) -> Bool {
    if let emb = e.embedding { return !emb.isEmpty }
    return false
}

private func clampD(_ x: Double, _ lo: Double, _ hi: Double) -> Double {
    Swift.max(lo, Swift.min(hi, x))
}

private func byTimeAsc(_ a: EpisodicMemoryEntry, _ b: EpisodicMemoryEntry) -> Bool {
    a.recordedAt < b.recordedAt
}

// MARK: - IMemoryConsolidator + MemoryConsolidator

/// Promotes lower-tier memory into higher tiers and enforces retention.
public protocol IMemoryConsolidator {
    /// Runs the consolidation pass for the given kind. OnDemand runs every tier
    /// with work pending. Returns the breakdown of what was produced and pruned.
    func tick(kind: SleepKind) async throws -> ConsolidationOutcome
}

/// Default `IMemoryConsolidator` implementation.
public final class MemoryConsolidator: IMemoryConsolidator, @unchecked Sendable {
    private let episodic: IEpisodicMemoryStore
    private let daily: IDailyMemoryStore
    private let semantic: ISemanticMemoryStore
    private let personaDelta: IPersonaDeltaStore
    private let core: ICoreMemoryStore
    private let personaStore: IPersonaStore
    private let summarizer: IMemorySummarizer
    private let options: MemoryConsolidationOptions
    private let clock: () -> Date
    private let userId: String

    public init(episodic: IEpisodicMemoryStore,
                daily: IDailyMemoryStore,
                semantic: ISemanticMemoryStore,
                personaDelta: IPersonaDeltaStore,
                core: ICoreMemoryStore,
                personaStore: IPersonaStore,
                summarizer: IMemorySummarizer,
                options: MemoryConsolidationOptions = MemoryConsolidationOptions(),
                clock: @escaping () -> Date = { Date() },
                userId: String = "default") {
        self.episodic = episodic
        self.daily = daily
        self.semantic = semantic
        self.personaDelta = personaDelta
        self.core = core
        self.personaStore = personaStore
        self.summarizer = summarizer
        self.options = options
        self.clock = clock
        self.userId = userId
    }

    public func tick(kind: SleepKind) async throws -> ConsolidationOutcome {
        let now = clock()
        var dailies = 0, clusters = 0, deltas = 0, corePromoted = 0
        var episodesPruned = 0, dailiesPruned = 0, semanticsPruned = 0

        if kind == .daily || kind == .onDemand {
            let (produced, promotedFromDaily) = try await runDaily(now)
            dailies = produced
            corePromoted += promotedFromDaily
            episodesPruned += try await pruneEpisodic(now)
        }

        if kind == .weekly || kind == .onDemand {
            let (produced, promotedFromWeekly) = try await runWeekly(now)
            clusters = produced
            corePromoted += promotedFromWeekly
            dailiesPruned += try await pruneDailies(now)
        }

        if kind == .monthly || kind == .onDemand {
            deltas = try await runMonthly(now)
            semanticsPruned += try await pruneSemantics(now)
        }

        return ConsolidationOutcome(kind: kind,
                                    dailySummariesProduced: dailies,
                                    semanticClustersProduced: clusters,
                                    personaDeltasProduced: deltas,
                                    corePromotions: corePromoted,
                                    episodesPruned: episodesPruned,
                                    dailiesPruned: dailiesPruned,
                                    semanticsPruned: semanticsPruned,
                                    ranAtUtc: now)
    }

    // ── Daily pass ─────────────────────────────────────────────────────────────

    private func runDaily(_ now: Date) async throws -> (Int, Int) {
        let recent = try await episodic.getRecent(count: Int.max)
        if recent.isEmpty { return (0, 0) }

        // Group episodes by their calendar day (UTC).
        let today = dayKey(from: now)
        var byDay: [String: [EpisodicMemoryEntry]] = [:]
        for e in recent {
            byDay[dayKey(from: e.recordedAt), default: []].append(e)
        }

        var produced = 0
        var promoted = 0
        for (day, group) in byDay {
            if !(day < today) { continue }  // only fully completed days

            let existing = try await daily.get(day: day)
            if let existing = existing, existing.episodeCount == group.count {
                continue  // idempotent skip — already consolidated this day
            }

            let ordered = group.sorted(by: byTimeAsc)
            let summary = try await summarizer.summarizeDay(day: day, entries: ordered)
            try await daily.upsert(summary)
            produced += 1

            if summary.salience >= options.dailyCorePromotionThreshold {
                promoted += try await promoteDailyToCore(summary)
            }
        }
        return (produced, promoted)
    }

    // ── Weekly pass ────────────────────────────────────────────────────────────

    private func runWeekly(_ now: Date) async throws -> (Int, Int) {
        let today = dayKey(from: now)
        let thisMonday = mondayOf(today)
        let lastMonday = addDays(thisMonday, -7)
        let lastSunday = addDays(lastMonday, 6)

        let lastWeek = try await daily.getRange(fromInclusive: lastMonday, toInclusive: lastSunday)
        if lastWeek.isEmpty { return (0, 0) }

        // Idempotency: if we already have clusters for this week, skip.
        let existing = try await semantic.getWeek(weekStartingMonday: lastMonday)
        if !existing.isEmpty { return (0, 0) }

        let clusters = try await summarizer.consolidateWeek(weekStartingMonday: lastMonday, daysInWeek: lastWeek)
        var promoted = 0
        for c in clusters {
            try await semantic.add(c)
            if c.salience >= options.weeklyCorePromotionThreshold {
                promoted += try await promoteClusterToCore(c)
            }
        }
        return (clusters.count, promoted)
    }

    // ── Monthly pass ─────────────────────────────────────────────────────────────

    private func runMonthly(_ now: Date) async throws -> Int {
        let today = dayKey(from: now)
        // Consider the most recently completed full month.
        let firstOfThisMonth = monthFirstDay(of: today)
        let lastMonthEnd = addDays(firstOfThisMonth, -1)
        let lastMonthStart = monthFirstDay(of: lastMonthEnd)

        // Idempotency: skip if we already have a delta whose PeriodStart falls in
        // the previous month (compared by month-year, not exact dates).
        let existingDeltas = try await personaDelta.getForUser(userId: userId)
        if existingDeltas.contains(where: { yearOf($0.periodStart) == yearOf(lastMonthStart)
                                            && monthOf($0.periodStart) == monthOf(lastMonthStart) }) {
            return 0
        }

        let days = try await daily.getRange(fromInclusive: lastMonthStart, toInclusive: lastMonthEnd)
        if days.isEmpty { return 0 }

        let after = try await personaStore.load(userId: userId)

        // For "before", reconstruct from the most recent prior delta if one exists;
        // otherwise treat as a fresh persona.
        let priors = existingDeltas
            .filter { $0.periodEnd < lastMonthStart }
            .sorted { $0.periodEnd > $1.periodEnd }
        let prior = priors.first
        let before = prior == nil
            ? Self.newPersona(userId)
            : Self.reconstructPersonaBefore(after: after, daysInPeriod: days, prior: prior!)

        let delta = try await summarizer.derivePersonaDelta(before: before, after: after, daysInPeriod: days)
        try await personaDelta.add(delta)
        return 1
    }

    // ── Core promotions ──────────────────────────────────────────────────────

    private func promoteDailyToCore(_ summary: DailyMemorySummary) async throws -> Int {
        // FirstOrDefault on TopicWeights.OrderByDescending — nil topic when empty.
        var topTopic: String? = nil
        var topWeight = -Float.greatestFiniteMagnitude
        for (k, v) in summary.topicWeights where v > topWeight {
            topWeight = v
            topTopic = k
        }

        let statement = topTopic == nil
            ? "On \(summary.day) an unusually meaningful day was recorded."
            : "\"\(topTopic!)\" mattered enough on \(summary.day) to be remembered."

        var embedding: [Float]? = nil
        for h in summary.highlightEntries {
            if let emb = h.embedding, !emb.isEmpty { embedding = emb; break }
        }

        let memory = CoreMemory(statement: statement,
                                kind: .highSalience,
                                topic: topTopic,
                                embedding: embedding,
                                sourceMemoryId: summary.id,
                                clock: clock)
        try await core.add(memory)
        return 1
    }

    private func promoteClusterToCore(_ cluster: SemanticMemoryCluster) async throws -> Int {
        let memory = CoreMemory(statement: "\"\(cluster.topic)\" has been a recurring theme "
                                    + "(week of \(cluster.weekStartingMonday)).",
                                kind: .patternInferred,
                                topic: cluster.topic,
                                embedding: cluster.centroidEmbedding,
                                sourceMemoryId: cluster.id,
                                clock: clock)
        try await core.add(memory)
        return 1
    }

    // ── Retention ────────────────────────────────────────────────────────────

    private func pruneEpisodic(_ now: Date) async throws -> Int {
        let cutoff = utcCalendar.date(byAdding: .day, value: -options.episodicRetentionDays, to: now)!
        return try await episodic.pruneOlderThan(cutoff: cutoff)
    }

    private func pruneDailies(_ now: Date) async throws -> Int {
        let cutoff = addDays(dayKey(from: now), -options.dailyRetentionDays)
        return try await daily.pruneOlderThan(cutoff: cutoff)
    }

    private func pruneSemantics(_ now: Date) async throws -> Int {
        let cutoff = addDays(dayKey(from: now), -options.semanticRetentionDays)
        return try await semantic.pruneOlderThan(cutoff: cutoff)
    }

    // ── Persona reconstruction ─────────────────────────────────────────────────

    /// Approximates the persona at the start of the period by subtracting the
    /// in-period gains from the current persona. Conservative — when in doubt it
    /// shows no change. Faithful port of ReconstructPersonaBeforeAsync.
    private static func reconstructPersonaBefore(after: PersonaState,
                                                 daysInPeriod: [DailyMemorySummary],
                                                 prior: PersonaDeltaSnapshot) -> PersonaState {
        let before = PersonaState()
        before.userId = after.userId
        before.verbosity = prior.verbosityAfter
        before.formality = prior.formalityAfter
        before.preferredLocale = after.preferredLocale
        let episodeSum = daysInPeriod.reduce(0) { $0 + $1.episodeCount }
        before.totalInteractions = after.totalInteractions - episodeSum
        before.positiveSignals = Swift.max(0, after.positiveSignals - clampPositive(prior.netSignalDelta))
        before.negativeSignals = after.negativeSignals

        // Carry over topic weights minus the strongest in-period gains.
        var weights: [String: Float] = [:]
        for (topic, w) in after.topicWeights {
            if let delta = prior.strengthenedTopics[topic] {
                weights[topic] = Swift.max(0, w - delta)
            } else {
                weights[topic] = w
            }
        }
        before.topicWeights = weights
        before.disfavouredTopics = after.disfavouredTopics
        return before
    }

    private static func newPersona(_ userId: String) -> PersonaState {
        let p = PersonaState()
        p.userId = userId
        return p
    }
}

private func clampPositive(_ v: Int) -> Int {
    v < 0 ? 0 : v
}
