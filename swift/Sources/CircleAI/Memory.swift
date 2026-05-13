// Memory.swift
//
// AffectState, PersonaState, EpisodicMemoryEntry, FeedbackSignal, Goal,
// and their store protocols — the "HER affect layer" and episodic memory.

import Foundation

// MARK: - AffectState

/// B!'s current emotional/engagement state — the "HER affect layer".
/// Five Float dimensions, all 0.0–1.0. Persisted per-user and injected
/// into the system prompt to shape response tone and initiative.
public final class AffectState: @unchecked Sendable {

    /// Opaque user identifier (device ID or hashed phone number).
    /// Never contains PII in plaintext.
    public var userId: String

    /// UTC time of the last update to this affect state.
    public var lastUpdatedAt: Date

    /// 0 = bored, 1 = fascinated. Drives proactive questions.
    public var curiosity: Float = 0.5

    /// 0 = disengaged, 1 = fully engaged. Rises with frequent quality interactions.
    public var engagement: Float = 0.5

    /// 0 = confident, 1 = confused. High = ask clarifying questions.
    public var uncertainty: Float = 0.2

    /// 0 = stranger, 1 = deep rapport. Grows slowly over many sessions.
    public var rapport: Float = 0.0

    /// 0 = subdued, 1 = energetic. Mirrors time-of-day and interaction pace.
    public var energy: Float = 0.5

    public init(userId: String = "default") {
        self.userId = userId
        self.lastUpdatedAt = Date()
    }

    /// Apply a positive interaction: nudge Engagement and Rapport up slightly.
    public func applyPositiveSignal() {
        engagement  = min(1.0, engagement  + 0.02)
        rapport     = min(1.0, rapport     + 0.01)
        uncertainty = max(0.0, uncertainty - 0.02)
        lastUpdatedAt = Date()
    }

    /// Apply a negative interaction: nudge Engagement down.
    public func applyNegativeSignal() {
        engagement  = max(0.0, engagement  - 0.03)
        uncertainty = min(1.0, uncertainty + 0.03)
        lastUpdatedAt = Date()
    }

    /// Apply idle time decay: Engagement and Energy drift back toward 0.5.
    /// - Parameter idle: Duration of idle time in seconds (TimeInterval).
    public func applyIdleDecay(idle: TimeInterval) {
        let hours = Float(idle / 3600.0)
        let decay = min(0.3, hours * 0.02)
        engagement = lerp(engagement, 0.5, decay)
        energy     = lerp(energy,     0.5, decay)
        lastUpdatedAt = Date()
    }

    private func lerp(_ a: Float, _ b: Float, _ t: Float) -> Float {
        let tc = max(0.0, min(1.0, t))
        return a + (b - a) * tc
    }

    /// Builds a compact affect hint for injection into the system prompt.
    /// Only emits lines that deviate meaningfully from neutral (0.5).
    public func toSystemPromptHint() -> String {
        var hints: [String] = []
        if curiosity   > 0.7 { hints.append("You are deeply curious about this topic — ask a follow-up question.") }
        if engagement  > 0.7 { hints.append("You are fully engaged — be enthusiastic and thorough.") }
        if engagement  < 0.3 { hints.append("Keep your response brief and to the point.") }
        if uncertainty > 0.6 { hints.append("You are uncertain — ask a clarifying question before answering.") }
        if rapport     > 0.7 { hints.append("You know this user well — use a warm, familiar tone.") }
        if energy      < 0.3 { hints.append("Keep your response calm and measured.") }
        if energy      > 0.8 { hints.append("You are energetic — be upbeat and concise.") }
        if hints.isEmpty { return "" }
        return "[Affect state]\n" + hints.joined(separator: "\n") + "\n"
    }
}

// MARK: - PersonaState

/// B!'s dynamic persona state for a specific user. Persisted between
/// sessions and injected into the system prompt to shape tone, vocabulary,
/// and topical depth.
public final class PersonaState: @unchecked Sendable {

    /// Opaque user identifier (device ID or hashed phone number).
    public var userId: String

    /// UTC time of the last update to this persona.
    public var lastUpdatedAt: Date

    // ── Communication style ────────────────────────────────────────────────

    /// Preferred response verbosity: "brief", "balanced" (default), "detailed".
    public var verbosity: String = "balanced"

    /// Formality level: "casual", "neutral" (default), "formal".
    public var formality: String = "neutral"

    /// Preferred response language/locale (IETF BCP-47). nil = match device locale.
    public var preferredLocale: String? = nil

    // ── Interest signals ───────────────────────────────────────────────────

    /// Weighted topic interests accumulated from positive interactions.
    public var topicWeights: [String: Float] = [:]

    /// Topics the user has down-voted or explicitly rejected.
    public var disfavouredTopics: Set<String> = []

    // ── Interaction stats ──────────────────────────────────────────────────

    /// Total number of recorded interactions with this persona.
    public var totalInteractions: Int = 0

    /// Cumulative positive feedback signals.
    public var positiveSignals: Int = 0

    /// Cumulative negative feedback signals.
    public var negativeSignals: Int = 0

    /// Derived satisfaction score 0.0–1.0. Returns nil when insufficient data
    /// (fewer than 10 signals).
    public var satisfactionScore: Double? {
        let total = positiveSignals + negativeSignals
        guard total >= 10 else { return nil }
        return Double(positiveSignals) / Double(total)
    }

    public init(userId: String = "default") {
        self.userId = userId
        self.lastUpdatedAt = Date()
    }

    /// Builds a compact persona instruction block suitable for prepending
    /// to the B! system prompt. Returns an empty string when the persona
    /// is in its default/unlearned state.
    public func toSystemPromptHint() -> String {
        var hints: [String] = []
        if verbosity != "balanced" { hints.append("Keep responses \(verbosity).") }
        switch formality {
        case "casual": hints.append("Use a casual, friendly tone.")
        case "formal": hints.append("Maintain a formal, professional tone.")
        default: break
        }
        if let locale = preferredLocale, !locale.isEmpty {
            hints.append("Respond in the language appropriate for locale \(locale).")
        }
        if hints.isEmpty { return "" }
        return "[User preferences]\n" + hints.joined(separator: "\n") + "\n"
    }
}

// MARK: - EpisodicMemoryEntry

/// A single recorded episode (one user↔assistant exchange) stored in
/// IEpisodicMemoryStore.
public struct EpisodicMemoryEntry: Sendable {
    /// Stable identifier for the entry.
    public var id: UUID

    /// UTC timestamp of the assistant's response.
    public var recordedAt: Date

    /// The user's message text.
    public var userText: String

    /// The assistant's response text.
    public var assistantText: String

    /// Optional identifier for the app context (e.g. "tgn.bidbaas").
    public var appContext: String?

    /// L2-normalised embedding of userText + " " + assistantText.
    /// nil if the embedding backend was unavailable when the entry was stored.
    public var embedding: [Float]?

    /// Arbitrary key-value tags (e.g. locale, sentiment).
    public var tags: [String: String]?

    public init(
        id: UUID = UUID(),
        recordedAt: Date = Date(),
        userText: String = "",
        assistantText: String = "",
        appContext: String? = nil,
        embedding: [Float]? = nil,
        tags: [String: String]? = nil
    ) {
        self.id = id
        self.recordedAt = recordedAt
        self.userText = userText
        self.assistantText = assistantText
        self.appContext = appContext
        self.embedding = embedding
        self.tags = tags
    }
}

// MARK: - FeedbackPolarity

/// Polarity of the feedback signal.
public enum FeedbackPolarity: Int, Sendable {
    /// User explicitly approved / up-voted the response.
    case positive  =  1
    /// User explicitly rejected / down-voted the response.
    case negative  = -1
    /// User provided a correction (neutral polarity).
    case correction = 0
}

// MARK: - FeedbackSignal

/// A single user-feedback event tied to a specific B! response.
public struct FeedbackSignal: Sendable {
    /// Stable identifier for the signal.
    public var id: UUID

    /// UTC time when the user provided the signal.
    public var recordedAt: Date

    /// The EpisodicMemoryEntry.id of the episode this feedback refers to.
    public var episodeId: UUID?

    /// The user's original message.
    public var userText: String

    /// B!'s response that is being rated.
    public var assistantText: String

    /// User's rating.
    public var polarity: FeedbackPolarity

    /// For correction signals — the user's preferred response.
    public var correctedText: String?

    /// Free-text comment the user optionally attached.
    public var comment: String?

    public init(
        id: UUID = UUID(),
        recordedAt: Date = Date(),
        episodeId: UUID? = nil,
        userText: String = "",
        assistantText: String = "",
        polarity: FeedbackPolarity,
        correctedText: String? = nil,
        comment: String? = nil
    ) {
        self.id = id
        self.recordedAt = recordedAt
        self.episodeId = episodeId
        self.userText = userText
        self.assistantText = assistantText
        self.polarity = polarity
        self.correctedText = correctedText
        self.comment = comment
    }
}

// MARK: - GoalStatus / GoalPriority

/// Lifecycle state of a Goal.
public enum GoalStatus: String, Sendable {
    /// Goal is currently being pursued.
    case active
    /// Goal has been achieved.
    case completed
    /// Goal has been abandoned without completion.
    case abandoned
}

/// Relative importance of a Goal.
public enum GoalPriority: String, Sendable {
    /// Nice-to-have; may be deferred.
    case low
    /// Standard importance.
    case normal
    /// Urgent or critical to the user.
    case high
}

// MARK: - Goal

/// A user goal that B! tracks and proactively helps with.
public struct Goal: Sendable {
    /// Unique stable identifier for this goal.
    public var id: String

    /// Owner of this goal.
    public var userId: String

    /// Short, human-readable title.
    public var title: String

    /// Full description of what the user wants to achieve.
    public var description: String

    /// Current lifecycle state.
    public var status: GoalStatus

    /// Relative importance.
    public var priority: GoalPriority

    /// When this goal was first recorded (UTC).
    public var createdAt: Date

    /// Optional deadline (UTC).
    public var dueAt: Date?

    /// When the goal was completed or abandoned (UTC).
    public var completedAt: Date?

    /// Freeform notes B! or the user has attached to this goal.
    public var notes: String?

    public init(
        id: String,
        userId: String,
        title: String,
        description: String,
        status: GoalStatus,
        priority: GoalPriority,
        createdAt: Date,
        dueAt: Date? = nil,
        completedAt: Date? = nil,
        notes: String? = nil
    ) {
        self.id = id
        self.userId = userId
        self.title = title
        self.description = description
        self.status = status
        self.priority = priority
        self.createdAt = createdAt
        self.dueAt = dueAt
        self.completedAt = completedAt
        self.notes = notes
    }
}

// MARK: - Store protocols

/// Loads and persists AffectState for a specific user.
public protocol IAffectStore {
    /// Loads the affect state for userId. Returns a fresh default state when none is found.
    func load(userId: String) async throws -> AffectState
    /// Persists the affect state.
    func save(_ state: AffectState) async throws
}

/// Loads and persists PersonaState for a specific user.
public protocol IPersonaStore {
    /// Loads the persona for userId. Returns a fresh default persona when none is found.
    func load(userId: String) async throws -> PersonaState
    /// Persists the persona.
    func save(_ persona: PersonaState) async throws
}

/// Persistent store for episodic memories.
public protocol IEpisodicMemoryStore {
    /// Appends a new entry to the store.
    func add(_ entry: EpisodicMemoryEntry) async throws

    /// Returns the topK entries whose embeddings are most similar (cosine)
    /// to queryEmbedding. Falls back to recency when queryEmbedding is nil.
    func search(queryEmbedding: [Float]?, topK: Int) async throws -> [EpisodicMemoryEntry]

    /// Returns the most recent count entries ordered newest-first.
    func getRecent(count: Int) async throws -> [EpisodicMemoryEntry]

    /// Total number of entries currently stored.
    func count() async throws -> Int

    /// Removes all entries older than cutoff. Returns the number removed.
    func pruneOlderThan(cutoff: Date) async throws -> Int
}

/// Persists user feedback signals.
public protocol IFeedbackStore {
    /// Records a new feedback signal.
    func add(_ signal: FeedbackSignal) async throws

    /// Returns the most recent count signals, newest-first.
    func getRecent(count: Int) async throws -> [FeedbackSignal]

    /// Total number of signals stored.
    func count() async throws -> Int

    /// Returns the fraction of stored signals that are positive (0.0–1.0).
    /// Returns nil when no signals are available.
    func positiveRatio() async throws -> Double?
}

/// Persists and retrieves Goal records for a user.
public protocol IGoalStore {
    /// Returns all goals for the given user, in any order.
    func list(userId: String) async throws -> [Goal]

    /// Returns the goal with the given id, or nil if it does not exist.
    func get(id: String) async throws -> Goal?

    /// Inserts or replaces the goal. Returns the stored goal.
    func upsert(_ goal: Goal) async throws -> Goal

    /// Deletes the goal with the given id. No-op if not found.
    func delete(id: String) async throws

    /// Returns all goals for userId where status is .active.
    func getActive(userId: String) async throws -> [Goal]
}
