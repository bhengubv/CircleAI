// ContextWindowBudgetManager.kt
//
// Kotlin port of CircleAI.Inference.ContextWindowBudgetManager (C# is the
// EXACT spec). Tracks token usage against a fixed context window and signals
// when the KV cache should be partially evicted to keep inference latency
// manageable.

package com.bhengubv.circleai.inference

/**
 * Tracks token usage against a fixed context window and signals when the KV
 * cache should be partially evicted.
 *
 * @param contextSize Total context window size in tokens. Must be > 0.
 * @param evictionThreshold Fill ratio (0–1) that triggers eviction. Defaults to
 *   0.85 (85%).
 */
class ContextWindowBudgetManager(
    val contextSize: Int,
    val evictionThreshold: Double = 0.85,
) {
    init {
        require(contextSize > 0) { "Context size must be greater than zero." }
        require(evictionThreshold in 0.0..1.0) { "Eviction threshold must be in the range [0, 1]." }
    }

    /** Cumulative tokens consumed so far (prompt + completion). */
    var usedTokens: Int = 0
        private set

    /** Tokens still available before the context window is full. */
    val remainingTokens: Int
        get() = contextSize - usedTokens

    /** Proportion of the context window that is currently occupied (0–1). */
    val fillRatio: Double
        get() = usedTokens.toDouble() / contextSize

    /**
     * `true` when the fill ratio has reached or exceeded [evictionThreshold]
     * and older context should be dropped.
     */
    val shouldEvict: Boolean
        get() = fillRatio >= evictionThreshold

    /**
     * Records the token cost of one exchange (a prompt + its completion).
     *
     * @param promptTokens Number of tokens in the prompt.
     * @param completionTokens Number of tokens in the model's reply.
     */
    fun recordExchange(promptTokens: Int, completionTokens: Int) {
        require(promptTokens >= 0) { "Token counts must not be negative." }
        require(completionTokens >= 0) { "Token counts must not be negative." }
        usedTokens += promptTokens + completionTokens
    }

    /**
     * Calculates how many of the oldest tokens should be dropped so that
     * [fillRatio] returns to [targetFillRatio]. Returns 0 when the fill ratio
     * is already at or below the target.
     *
     * @param targetFillRatio Desired fill ratio after eviction. Defaults to 0.50.
     */
    fun calculateEvictionCount(targetFillRatio: Double = 0.50): Int {
        require(targetFillRatio in 0.0..1.0) { "Target fill ratio must be in the range [0, 1]." }
        val targetUsed = (contextSize * targetFillRatio).toInt()
        val evict = usedTokens - targetUsed
        return if (evict > 0) evict else 0
    }

    /** Resets the used-token counter to zero. Call this after clearing the KV cache. */
    fun reset() {
        usedTokens = 0
    }
}
