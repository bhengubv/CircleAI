// Memory.kt
//
// Kotlin port of the Circle.AI.Memory portable layer.
//
// Covers:
//   AffectState          — five-dimensional affect model (HER affect layer)
//   EpisodicMemoryEntry  — one user↔assistant exchange + embedding
//   FeedbackSignal       — explicit user rating of a B! response
//   PersonaState         — evolving persona state for a specific user
//   Goal / GoalStatus / GoalPriority — user goal tracking
//   IAffectStore         — load/save affect
//   IEpisodicMemoryStore — episodic retrieval
//   IPersonaStore        — load/save persona
//   IFeedbackStore       — append and query feedback signals
//   IGoalStore           — CRUD for goals

package com.bhengubv.circleai.memory

import java.time.Duration
import java.time.Instant
import java.util.UUID

// ---------------------------------------------------------------------------
// GoalStatus / GoalPriority enums
// ---------------------------------------------------------------------------

/** Lifecycle state of a [Goal]. */
enum class GoalStatus {
    /** Goal is currently being pursued. */
    Active,
    /** Goal has been achieved. */
    Completed,
    /** Goal has been abandoned without completion. */
    Abandoned,
}

/** Relative importance of a [Goal]. */
enum class GoalPriority {
    /** Nice-to-have; may be deferred. */
    Low,
    /** Standard importance. */
    Normal,
    /** Urgent or critical to the user. */
    High,
}

// ---------------------------------------------------------------------------
// FeedbackPolarity enum
// ---------------------------------------------------------------------------

/** Polarity of the feedback signal. */
enum class FeedbackPolarity(val value: Int) {
    /** User explicitly approved / up-voted the response. */
    Positive(1),
    /** User explicitly rejected / down-voted the response. */
    Negative(-1),
    /**
     * User provided a correction (neutral polarity, but carries the
     * preferred text in [FeedbackSignal.correctedText]).
     */
    Correction(0),
}

// ---------------------------------------------------------------------------
// AffectState
// ---------------------------------------------------------------------------

/**
 * B!'s current emotional/engagement state — the "HER affect layer".
 * Five float dimensions, all 0.0–1.0. Persisted per-user and injected
 * into the system prompt to shape response tone and initiative.
 *
 * CRITICAL: the math in [applyPositiveSignal], [applyNegativeSignal],
 * and [applyIdleDecay] is byte-identical to the C# reference implementation.
 * Do not change the constants.
 */
class AffectState(
    /** Opaque user identifier (device ID or hashed phone number). Never contains PII in plaintext. */
    val userId: String = "default"
) {
    /** UTC time of the last update to this affect state. */
    var lastUpdatedAt: Instant = Instant.now()

    /** 0=bored, 1=fascinated. Drives proactive questions. */
    var curiosity: Float = 0.5f

    /** 0=disengaged, 1=fully engaged. Rises with frequent quality interactions. */
    var engagement: Float = 0.5f

    /** 0=confident, 1=confused. High = ask clarifying questions. */
    var uncertainty: Float = 0.2f

    /** 0=stranger, 1=deep rapport. Grows slowly over many sessions. */
    var rapport: Float = 0.0f

    /** 0=subdued, 1=energetic. Mirrors time-of-day and interaction pace. */
    var energy: Float = 0.5f

    /** Apply a positive interaction: nudge Engagement and Rapport up slightly. */
    fun applyPositiveSignal() {
        engagement  = (engagement  + 0.02f).coerceIn(0f, 1f)
        rapport     = (rapport     + 0.01f).coerceIn(0f, 1f)
        uncertainty = (uncertainty - 0.02f).coerceIn(0f, 1f)
        lastUpdatedAt = Instant.now()
    }

    /** Apply a negative interaction: nudge Engagement down. */
    fun applyNegativeSignal() {
        engagement  = (engagement  - 0.03f).coerceIn(0f, 1f)
        uncertainty = (uncertainty + 0.03f).coerceIn(0f, 1f)
        lastUpdatedAt = Instant.now()
    }

    /**
     * Apply idle time decay: Engagement and Energy drift back toward 0.5.
     *
     * [idle] is a [java.time.Duration]. Converted to fractional hours as
     * `idle.seconds / 3600f` to match the C# `TotalHours` float arithmetic exactly.
     */
    fun applyIdleDecay(idle: Duration) {
        val hours = idle.seconds.toFloat() / 3600f
        val decay = minOf(0.3f, hours * 0.02f)
        engagement = lerp(engagement, 0.5f, decay)
        energy     = lerp(energy,     0.5f, decay)
        lastUpdatedAt = Instant.now()
    }

    private fun lerp(a: Float, b: Float, t: Float): Float {
        val tc = t.coerceIn(0f, 1f)
        return a + (b - a) * tc
    }

    /**
     * Builds a compact affect hint for injection into the system prompt.
     * Only emits lines that deviate meaningfully from neutral (0.5).
     */
    fun toSystemPromptHint(): String {
        val hints = mutableListOf<String>()

        if (curiosity > 0.7f)
            hints.add("You are deeply curious about this topic — ask a follow-up question.")
        if (engagement > 0.7f)
            hints.add("You are fully engaged — be enthusiastic and thorough.")
        if (engagement < 0.3f)
            hints.add("Keep your response brief and to the point.")
        if (uncertainty > 0.6f)
            hints.add("You are uncertain — ask a clarifying question before answering.")
        if (rapport > 0.7f)
            hints.add("You know this user well — use a warm, familiar tone.")
        if (energy < 0.3f)
            hints.add("Keep your response calm and measured.")
        if (energy > 0.8f)
            hints.add("You are energetic — be upbeat and concise.")

        if (hints.isEmpty()) return ""
        return "[Affect state]\n" + hints.joinToString("\n") + "\n"
    }
}

// ---------------------------------------------------------------------------
// EpisodicMemoryEntry
// ---------------------------------------------------------------------------

/**
 * A single recorded episode (one user↔assistant exchange) stored in
 * [IEpisodicMemoryStore].
 */
data class EpisodicMemoryEntry(
    /** Stable identifier for the entry. */
    val id: UUID = UUID.randomUUID(),
    /** UTC timestamp of the assistant's response. */
    val recordedAtUtc: Instant = Instant.now(),
    /** The user's message text. */
    val userText: String = "",
    /** The assistant's response text. */
    val assistantText: String = "",
    /**
     * Optional identifier for the app context in which the exchange happened
     * (e.g. "tgn.bidbaas").
     */
    val appContext: String? = null,
    /**
     * L2-normalised embedding of `userText + " " + assistantText`, pre-computed
     * at write time. null if the embedding backend was unavailable when the entry
     * was stored.
     */
    val embedding: FloatArray? = null,
    /**
     * Arbitrary key-value tags (e.g. locale, sentiment).
     */
    val tags: Map<String, String>? = null
)

// ---------------------------------------------------------------------------
// FeedbackSignal
// ---------------------------------------------------------------------------

/**
 * A single user-feedback event tied to a specific B! response.
 * Stored by [IFeedbackStore] for later analysis and potential on-device adaptation.
 */
data class FeedbackSignal(
    /** Stable identifier for the signal. */
    val id: UUID = UUID.randomUUID(),
    /** UTC time when the user provided the signal. */
    val recordedAtUtc: Instant = Instant.now(),
    /**
     * The [EpisodicMemoryEntry.id] of the episode this feedback refers to,
     * if the exchange was also stored episodically.
     */
    val episodeId: UUID? = null,
    /** The user's original message. */
    val userText: String = "",
    /** B!'s response that is being rated. */
    val assistantText: String = "",
    /** User's rating. */
    val polarity: FeedbackPolarity = FeedbackPolarity.Positive,
    /**
     * For [FeedbackPolarity.Correction] signals — the user's preferred response
     * that should have been given.
     */
    val correctedText: String? = null,
    /** Free-text comment the user optionally attached to the signal. */
    val comment: String? = null
)

// ---------------------------------------------------------------------------
// PersonaState
// ---------------------------------------------------------------------------

/**
 * B!'s dynamic persona state for a specific user. Persisted between sessions
 * and injected into the system prompt to shape tone, vocabulary, and topical depth.
 */
class PersonaState(
    /** Opaque user identifier (device ID or hashed phone number). Never contains PII in plaintext. */
    val userId: String = "default"
) {
    /** UTC time of the last update to this persona. */
    var lastUpdatedAt: Instant = Instant.now()

    /**
     * Preferred response verbosity inferred from feedback:
     * "brief", "balanced" (default), or "detailed".
     */
    var verbosity: String = "balanced"

    /**
     * Formality level inferred from the user's own language:
     * "casual", "neutral" (default), or "formal".
     */
    var formality: String = "neutral"

    /**
     * Preferred response language/locale (IETF BCP-47).
     * null means "match the device locale".
     */
    var preferredLocale: String? = null

    /**
     * Weighted topic interests accumulated from positive interactions.
     * Key = normalised topic label (e.g. "finance", "sport"),
     * Value = accumulated positive-signal weight (unbounded positive float).
     */
    val topicWeights: MutableMap<String, Float> = mutableMapOf()

    /** Topics the user has down-voted or explicitly rejected. */
    val disfavouredTopics: MutableSet<String> = mutableSetOf()

    /** Total number of recorded interactions with this persona. */
    var totalInteractions: Int = 0

    /** Cumulative positive feedback signals. */
    var positiveSignals: Int = 0

    /** Cumulative negative feedback signals. */
    var negativeSignals: Int = 0

    /**
     * Derived satisfaction score 0.0–1.0. Returns null when insufficient data
     * (fewer than 10 signals).
     */
    val satisfactionScore: Double?
        get() = if (positiveSignals + negativeSignals < 10) null
                else positiveSignals.toDouble() / (positiveSignals + negativeSignals)

    /**
     * Builds a compact persona instruction block suitable for prepending to the
     * B! system prompt. Returns an empty string when the persona is in its
     * default/unlearned state.
     */
    fun toSystemPromptHint(): String {
        val hints = mutableListOf<String>()

        if (verbosity != "balanced")
            hints.add("Keep responses $verbosity.")

        when (formality) {
            "casual" -> hints.add("Use a casual, friendly tone.")
            "formal" -> hints.add("Maintain a formal, professional tone.")
        }

        preferredLocale?.let { hints.add("Respond in the language appropriate for locale $it.") }

        if (hints.isEmpty()) return ""
        return "[User preferences]\n" + hints.joinToString("\n") + "\n"
    }
}

// ---------------------------------------------------------------------------
// Goal
// ---------------------------------------------------------------------------

/**
 * A user goal that B! tracks and proactively helps with.
 * Inspired by the way Samantha in *Her* remembered what Theodore cared about.
 */
data class Goal(
    /** Unique stable identifier for this goal. */
    val id: String,
    /** Owner of this goal. */
    val userId: String,
    /** Short, human-readable title. */
    val title: String,
    /** Full description of what the user wants to achieve. */
    val description: String,
    /** Current lifecycle state. */
    val status: GoalStatus,
    /** Relative importance. */
    val priority: GoalPriority,
    /** When this goal was first recorded (UTC). */
    val createdUtc: Instant,
    /** Optional deadline (UTC). */
    val dueUtc: Instant? = null,
    /** When the goal was completed or abandoned (UTC). */
    val completedUtc: Instant? = null,
    /** Freeform notes B! or the user has attached to this goal. */
    val notes: String? = null
)

// ---------------------------------------------------------------------------
// Store interfaces
// ---------------------------------------------------------------------------

/** Loads and persists [AffectState] for a specific user. */
interface IAffectStore {
    /**
     * Loads the affect state for [userId]. Returns a fresh default state when
     * none is found.
     */
    suspend fun loadAsync(userId: String): AffectState

    /**
     * Persists the affect state. Implementations must be crash-safe
     * (write-then-swap or similar) to avoid partial writes.
     */
    suspend fun saveAsync(state: AffectState)
}

/**
 * Persistent store for episodic memories (conversational exchanges + embeddings).
 * Implementations may be in-memory (tests/edge), SQLite-vec (production on-device),
 * or a remote vector database.
 */
interface IEpisodicMemoryStore {
    /**
     * Appends a new entry to the store. The store must assign
     * [EpisodicMemoryEntry.id] if not already set.
     */
    suspend fun addAsync(entry: EpisodicMemoryEntry)

    /**
     * Returns the [topK] entries whose embeddings are most similar (cosine) to
     * [queryEmbedding]. When [queryEmbedding] is null, falls back to recency
     * (most recent [topK] entries).
     */
    suspend fun searchAsync(queryEmbedding: FloatArray?, topK: Int = 5): List<EpisodicMemoryEntry>

    /**
     * Returns the most recent [count] entries ordered newest-first.
     */
    suspend fun getRecentAsync(count: Int = 10): List<EpisodicMemoryEntry>

    /** Total number of entries currently stored. */
    suspend fun countAsync(): Int

    /**
     * Removes all entries older than [cutoff].
     * Returns the number of entries removed.
     */
    suspend fun pruneOlderThanAsync(cutoff: Instant): Int
}

/** Loads and persists [PersonaState] for a specific user. */
interface IPersonaStore {
    /**
     * Loads the persona for [userId]. Returns a fresh default persona when none
     * is found.
     */
    suspend fun loadAsync(userId: String): PersonaState

    /**
     * Persists the persona. The implementation must be crash-safe
     * (write-then-swap or similar) to avoid partial writes.
     */
    suspend fun saveAsync(persona: PersonaState)
}

/** Persists user feedback signals for later analysis and on-device adaptation. */
interface IFeedbackStore {
    /** Records a new feedback signal. */
    suspend fun addAsync(signal: FeedbackSignal)

    /**
     * Returns the most recent [count] signals, newest-first.
     */
    suspend fun getRecentAsync(count: Int = 50): List<FeedbackSignal>

    /** Total number of signals stored. */
    suspend fun countAsync(): Int

    /**
     * Returns the fraction of stored signals that are [FeedbackPolarity.Positive]
     * (0.0–1.0). Returns null when no signals are available.
     */
    suspend fun positiveRatioAsync(): Double?
}

/** Persists and retrieves [Goal] records for a user. */
interface IGoalStore {
    /** Returns all goals for the given user, in any order. */
    suspend fun listAsync(userId: String): List<Goal>

    /**
     * Returns the goal with the given [id], or null if it does not exist.
     */
    suspend fun getAsync(id: String): Goal?

    /**
     * Inserts or replaces the goal. The goal's id is the natural key.
     * Returns the stored goal.
     */
    suspend fun upsertAsync(goal: Goal): Goal

    /** Deletes the goal with the given [id]. No-op if not found. */
    suspend fun deleteAsync(id: String)

    /**
     * Returns all goals for [userId] where [Goal.status] is [GoalStatus.Active].
     */
    suspend fun getActiveAsync(userId: String): List<Goal>
}
