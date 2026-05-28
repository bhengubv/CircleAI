// Memory.kt
//
// Android/Kotlin port of the Circle.AI.Memory portable layer.
//
// Covers:
//   AffectState          — five-dimensional affect model (HER affect layer)
//   EpisodicMemoryEntry  — one user↔assistant exchange + embedding
//   FeedbackSignal       — explicit user rating of a B! response
//   PersonaState         — evolving persona state for a specific user
//   Goal / GoalStatus / GoalPriority — user goal tracking
//   Store interfaces

package com.bhengubv.circleai.android.memory

import java.time.Instant

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
enum class FeedbackPolarity {
    /** User explicitly approved / up-voted the response. */
    Positive,
    /** User explicitly rejected / down-voted the response. */
    Negative,
    /** No explicit signal (neutral observation). */
    Neutral,
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
    val userId: String = "default",
    lastUpdatedAt: Instant = Instant.now(),
    curiosity: Float = 0.5f,
    engagement: Float = 0.5f,
    uncertainty: Float = 0.2f,
    rapport: Float = 0.0f,
    energy: Float = 0.5f
) {
    /** UTC time of the last update to this affect state. */
    var lastUpdatedAt: Instant = lastUpdatedAt

    /** 0=bored, 1=fascinated. Drives proactive questions. */
    var curiosity: Float = curiosity

    /** 0=disengaged, 1=fully engaged. Rises with frequent quality interactions. */
    var engagement: Float = engagement

    /** 0=confident, 1=confused. High = ask clarifying questions. */
    var uncertainty: Float = uncertainty

    /** 0=stranger, 1=deep rapport. Grows slowly over many sessions. */
    var rapport: Float = rapport

    /** 0=subdued, 1=energetic. Mirrors time-of-day and interaction pace. */
    var energy: Float = energy

    /**
     * Apply a positive interaction:
     * engagement += 0.02, rapport += 0.01, uncertainty -= 0.02
     */
    fun applyPositiveSignal() {
        engagement  = (engagement  + 0.02f).coerceIn(0f, 1f)
        rapport     = (rapport     + 0.01f).coerceIn(0f, 1f)
        uncertainty = (uncertainty - 0.02f).coerceIn(0f, 1f)
        lastUpdatedAt = Instant.now()
    }

    /**
     * Apply a negative interaction:
     * engagement -= 0.03, uncertainty += 0.03
     */
    fun applyNegativeSignal() {
        engagement  = (engagement  - 0.03f).coerceIn(0f, 1f)
        uncertainty = (uncertainty + 0.03f).coerceIn(0f, 1f)
        lastUpdatedAt = Instant.now()
    }

    /**
     * Apply idle time decay: Engagement and Energy drift back toward 0.5.
     *
     * [hours] is a fractional hours value.
     * decay = min(0.3, hours * 0.02)
     * lerp(a, b, t) = a + (b - a) * t.coerceIn(0, 1)
     */
    fun applyIdleDecay(hours: Float) {
        val decay = (hours * 0.02f).coerceAtMost(0.3f)
        engagement = lerp(engagement, 0.5f, decay)
        energy     = lerp(energy,     0.5f, decay)
        lastUpdatedAt = Instant.now()
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

    companion object {
        private fun lerp(a: Float, b: Float, t: Float): Float {
            val tc = t.coerceIn(0f, 1f)
            return a + (b - a) * tc
        }
    }
}

// ---------------------------------------------------------------------------
// EpisodicMemoryEntry
// ---------------------------------------------------------------------------

/**
 * A single recorded episode stored in [EpisodicMemoryStore].
 */
data class EpisodicMemoryEntry(
    /** Stable identifier for the entry. */
    val id: String,
    /** Owner user identifier. */
    val userId: String,
    /** The content of this episodic memory. */
    val content: String,
    /**
     * L2-normalised embedding vector, pre-computed at write time.
     */
    val embedding: FloatArray,
    /** UTC timestamp when this entry was recorded. */
    val createdAt: Instant = Instant.now(),
    /** Arbitrary tags for filtering. */
    val tags: List<String> = emptyList(),
    /** Importance score 0.0–1.0. */
    val importance: Float = 0.5f
) {
    // FloatArray is reference-equal by default; override for value semantics.
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is EpisodicMemoryEntry) return false
        return id == other.id &&
            userId == other.userId &&
            content == other.content &&
            embedding.contentEquals(other.embedding) &&
            createdAt == other.createdAt &&
            tags == other.tags &&
            importance == other.importance
    }

    override fun hashCode(): Int {
        var result = id.hashCode()
        result = 31 * result + userId.hashCode()
        result = 31 * result + content.hashCode()
        result = 31 * result + embedding.contentHashCode()
        result = 31 * result + createdAt.hashCode()
        result = 31 * result + tags.hashCode()
        result = 31 * result + importance.hashCode()
        return result
    }
}

// ---------------------------------------------------------------------------
// FeedbackSignal
// ---------------------------------------------------------------------------

/**
 * A single user-feedback event tied to a specific B! response.
 */
data class FeedbackSignal(
    /** Stable identifier for the signal. */
    val id: String,
    /** Owner user identifier. */
    val userId: String,
    /** The turn or response being rated. */
    val turnId: String,
    /** User's rating. */
    val polarity: FeedbackPolarity,
    /** Optional free-text note the user attached to the signal. */
    val note: String? = null,
    /** UTC time when the user provided the signal. */
    val recordedAt: Instant = Instant.now()
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
     * Short description of the user's traits, used as a system prompt hint.
     */
    var traitSummary: String = ""

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
    val createdAt: Instant,
    /** Optional deadline (UTC). */
    val dueAt: Instant? = null,
    /** When the goal was completed or abandoned (UTC). */
    val completedAt: Instant? = null,
    /** Freeform notes B! or the user has attached to this goal. */
    val notes: String? = null,
    /**
     * Progress toward completion, 0.0–1.0.
     * Updated via [advanceProgress].
     */
    val progress: Float = 0f
) {
    /**
     * Returns a copy of this goal with progress advanced by [delta],
     * clamped to [0.0, 1.0].
     * Formula: new_progress = clamp(progress + delta, 0.0, 1.0)
     */
    fun advanceProgress(delta: Float): Goal =
        copy(progress = (progress + delta).coerceIn(0f, 1f))
}

// ---------------------------------------------------------------------------
// Store interfaces
// ---------------------------------------------------------------------------

/** Loads and persists [AffectState] for a specific user. */
interface AffectStore {
    /**
     * Loads the affect state for [userId]. Returns a fresh default state when
     * none is found.
     */
    suspend fun loadAsync(userId: String): AffectState

    /**
     * Persists the affect state.
     */
    suspend fun saveAsync(state: AffectState)
}

/**
 * Persistent store for episodic memories.
 */
interface EpisodicMemoryStore {
    /** Saves a new entry to the store. */
    suspend fun save(entry: EpisodicMemoryEntry)

    /** Returns the [limit] most recent entries for [userId], newest-first. */
    suspend fun getRecent(userId: String, limit: Int): List<EpisodicMemoryEntry>

    /** Deletes the entry with the given [id]. No-op if not found. */
    suspend fun delete(id: String)
}

/** Loads and persists [PersonaState] for a specific user. */
interface PersonaStore {
    /** Loads the persona for [userId]. Returns a fresh default persona when none is found. */
    suspend fun loadAsync(userId: String): PersonaState

    /** Persists the persona. */
    suspend fun saveAsync(persona: PersonaState)
}

/** Persists user feedback signals for later analysis and on-device adaptation. */
interface FeedbackStore {
    /** Records a new feedback signal. */
    suspend fun save(signal: FeedbackSignal)

    /** Returns the most recent [limit] signals for [userId], newest-first. */
    suspend fun getRecent(userId: String, limit: Int): List<FeedbackSignal>
}

/** Persists and retrieves [Goal] records for a user. */
interface GoalStore {
    /** Returns all goals for the given user, in any order. */
    suspend fun listAsync(userId: String): List<Goal>

    /** Returns the goal with the given [id], or null if it does not exist. */
    suspend fun getAsync(id: String): Goal?

    /** Inserts or replaces the goal. Returns the stored goal. */
    suspend fun upsertAsync(goal: Goal): Goal

    /** Deletes the goal with the given [id]. No-op if not found. */
    suspend fun deleteAsync(id: String)

    /** Returns all goals for [userId] where [Goal.status] is [GoalStatus.Active]. */
    suspend fun getActiveAsync(userId: String): List<Goal>
}
