// FeedbackAnalyser.kt
//
// Analyses a window of FeedbackSignal records and produces PersonaAdaptation
// deltas. Kotlin port of CircleAI.Memory.FeedbackAnalyser (the C# reference),
// mirroring the verified TypeScript pilot (memory/feedback_analyser.ts) 1:1.
//
// Rules (applied to the most-recent N signals, default N=20):
//   - >70% negative signals → verbosityDelta = -0.1f
//   - >70% positive signals → verbosityDelta = +0.05f
//   - formalityDelta is always 0f (reserved for future heuristics)
//   - preferredTopics is always empty — FeedbackSignal carries no topic tags
//
// The deltas are `Float` (native 32-bit), matching the C# `float` literals
// (-0.1f, +0.05f) so the cross-language fixture contract stays byte-identical.
//
// NOTE — a NEW brain-local FeedbackSignal / FeedbackPolarity pair, deliberately
// separate from com.bhengubv.circleai.memory's existing types (which carry
// userId/turnId and a Neutral polarity). The memory-brain needs the C#/TS
// reference shape: userText/assistantText/polarity/recordedAtUtc plus a
// Correction polarity that is ignored in the positive/negative ratio. The two
// coexist rather than one being bent to the other.

package com.bhengubv.circleai.memory.brain

import java.time.Instant

// ---------------------------------------------------------------------------
// FeedbackPolarity
// ---------------------------------------------------------------------------

/** Polarity of a [FeedbackSignal] (memory-brain shape). */
enum class FeedbackPolarity {
    /** User explicitly approved / up-voted the response. */
    Positive,

    /** User explicitly rejected / down-voted the response. */
    Negative,

    /**
     * User provided a correction. Neutral in the positive/negative ratio — it
     * carries the preferred text in [FeedbackSignal.correctedText] instead.
     */
    Correction,
}

// ---------------------------------------------------------------------------
// FeedbackSignal
// ---------------------------------------------------------------------------

/**
 * A single user-feedback event tied to a specific B! response (memory-brain
 * shape). Accumulated signals feed [FeedbackAnalyser] and, eventually, on-device
 * adaptation.
 */
data class FeedbackSignal(
    /** Stable identifier for the signal. */
    val id: String,
    /** User's rating. */
    val polarity: FeedbackPolarity,
    /** UTC time when the user provided the signal. */
    val recordedAtUtc: Instant = Instant.now(),
    /** The user's original message. */
    val userText: String = "",
    /** B!'s response that is being rated. */
    val assistantText: String = "",
    /** For [FeedbackPolarity.Correction] signals — the user's preferred response. */
    val correctedText: String? = null,
    /** Free-text comment the user optionally attached to the signal. */
    val comment: String? = null,
)

// ---------------------------------------------------------------------------
// PersonaAdaptation
// ---------------------------------------------------------------------------

/** Deltas to apply to persona state after analysing feedback signals. */
data class PersonaAdaptation(
    val verbosityDelta: Float,
    val formalityDelta: Float,
    val preferredTopics: List<String>,
)

// ---------------------------------------------------------------------------
// FeedbackAnalyser
// ---------------------------------------------------------------------------

/**
 * Analyses recent [FeedbackSignal] records and produces [PersonaAdaptation]
 * adjustments.
 *
 * @param windowSize Number of most-recent signals to consider. Must be at least
 *   1. Default 20.
 */
class FeedbackAnalyser(private val windowSize: Int = 20) {

    init {
        require(windowSize >= 1) { "Window size must be at least 1." }
    }

    /**
     * Compute persona adaptation from the provided [signals].
     *
     * `verbosityDelta` is:
     *   - -0.1f  when more than 70% of the window is negative
     *   - +0.05f when more than 70% of the window is positive
     *   - 0f     otherwise
     *
     * `formalityDelta` is always 0f and `preferredTopics` is always empty
     * because [FeedbackSignal] carries no topic metadata.
     */
    fun analyse(signals: Iterable<FeedbackSignal>): PersonaAdaptation {
        val window = signals
            .sortedByDescending { it.recordedAtUtc }
            .take(windowSize)

        if (window.isEmpty()) return PersonaAdaptation(0f, 0f, emptyList())

        val positiveCount = window.count { it.polarity == FeedbackPolarity.Positive }
        val negativeCount = window.count { it.polarity == FeedbackPolarity.Negative }
        val total = window.size

        var verbosityDelta = 0f
        val negativeRatio = negativeCount.toFloat() / total
        val positiveRatio = positiveCount.toFloat() / total

        if (negativeRatio > 0.70f) verbosityDelta = -0.1f
        else if (positiveRatio > 0.70f) verbosityDelta = 0.05f

        // FeedbackSignal has no tags — topic extraction is deferred.
        return PersonaAdaptation(verbosityDelta, 0f, emptyList())
    }
}

// ---------------------------------------------------------------------------
// IFeedbackStore + InMemoryFeedbackStore
// ---------------------------------------------------------------------------

/** Persists user feedback signals for later analysis and on-device adaptation. */
interface IFeedbackStore {
    /** Records a new feedback signal. */
    suspend fun addAsync(signal: FeedbackSignal)

    /** Returns the most recent [count] signals, newest-first. */
    suspend fun getRecentAsync(count: Int = 50): List<FeedbackSignal>

    /** Total number of signals currently stored. */
    suspend fun countAsync(): Int

    /**
     * Fraction of stored signals that are [FeedbackPolarity.Positive], or null
     * when the store is empty.
     */
    suspend fun positiveRatioAsync(): Double?
}

/**
 * In-memory [IFeedbackStore]. Kotlin port of CircleAI.Memory
 * (InMemoryFeedbackStore) — the C# reference — mirroring the TS pilot
 * (memory/stores.ts). Data is lost on process exit; for tests and headless CLI
 * use. Capacity is capped (FIFO eviction). Thread-safe via a monitor.
 *
 * @param maxSignals Cap on stored signals; when exceeded the oldest are evicted
 *   (FIFO). Default 10000. Must be positive.
 */
class InMemoryFeedbackStore(private val maxSignals: Int = 10_000) : IFeedbackStore {

    init {
        require(maxSignals > 0) { "maxSignals must be positive" }
    }

    private val lock = Any()
    private val signals = ArrayList<FeedbackSignal>()

    override suspend fun addAsync(signal: FeedbackSignal) {
        synchronized(lock) {
            signals.add(signal)
            while (signals.size > maxSignals) signals.removeAt(0)
        }
    }

    override suspend fun getRecentAsync(count: Int): List<FeedbackSignal> {
        val snapshot = synchronized(lock) { signals.toList() }
        return snapshot
            .sortedByDescending { it.recordedAtUtc }
            .take(count)
    }

    override suspend fun countAsync(): Int = synchronized(lock) { signals.size }

    override suspend fun positiveRatioAsync(): Double? = synchronized(lock) {
        if (signals.isEmpty()) return@synchronized null
        val pos = signals.count { it.polarity == FeedbackPolarity.Positive }
        pos.toDouble() / signals.size
    }
}
