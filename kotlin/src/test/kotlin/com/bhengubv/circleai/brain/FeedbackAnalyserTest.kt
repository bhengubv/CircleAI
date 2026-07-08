// FeedbackAnalyserTest.kt
//
// Exercises FeedbackAnalyser (persona-adaptation deltas from a window of
// signals) and the InMemoryFeedbackStore. Mirrors the verified TypeScript suite
// (tests/feedback_analyser.test.ts) and the C# FeedbackAnalyser rules +
// FeedbackStoreTests.

package com.bhengubv.circleai.brain

import com.bhengubv.circleai.memory.brain.FeedbackAnalyser
import com.bhengubv.circleai.memory.brain.FeedbackPolarity
import com.bhengubv.circleai.memory.brain.FeedbackSignal
import com.bhengubv.circleai.memory.brain.InMemoryFeedbackStore
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import java.util.UUID
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class FeedbackAnalyserTest {

    // FP32 delta constants — must equal the C# `float` literals exactly.
    private val VERBOSITY_DOWN = -0.1f
    private val VERBOSITY_UP = 0.05f

    private var seq = 0L
    private fun make(
        polarity: FeedbackPolarity,
        at: Instant? = null,
        user: String = "user",
    ): FeedbackSignal = FeedbackSignal(
        id = UUID.randomUUID().toString(),
        polarity = polarity,
        // Monotonic default timestamps so window ordering is deterministic per call.
        recordedAtUtc = at ?: Instant.ofEpochMilli(1_700_000_000_000 + seq++ * 1000),
        userText = user,
        assistantText = "response",
    )

    // ══════════════════════════════════════════════════════════════════════
    // FeedbackAnalyser
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `rejects a window size below 1`() {
        assertFailsWith<IllegalArgumentException> { FeedbackAnalyser(0) }
    }

    @Test
    fun `returns zero deltas for an empty signal set`() {
        val a = FeedbackAnalyser().analyse(emptyList())
        assertEquals(0f, a.verbosityDelta)
        assertEquals(0f, a.formalityDelta)
        assertEquals(emptyList(), a.preferredTopics)
    }

    @Test
    fun `drops verbosity by -0_1 when over 70 percent of the window is negative`() {
        val analyser = FeedbackAnalyser()
        // 8 negative + 2 positive = 80% negative.
        val signals = buildList {
            repeat(8) { add(make(FeedbackPolarity.Negative)) }
            repeat(2) { add(make(FeedbackPolarity.Positive)) }
        }

        val a = analyser.analyse(signals)
        assertEquals(VERBOSITY_DOWN, a.verbosityDelta)
        assertEquals(0f, a.formalityDelta)
        assertEquals(emptyList(), a.preferredTopics)
    }

    @Test
    fun `raises verbosity by +0_05 when over 70 percent of the window is positive`() {
        val analyser = FeedbackAnalyser()
        val signals = buildList {
            repeat(8) { add(make(FeedbackPolarity.Positive)) }
            repeat(2) { add(make(FeedbackPolarity.Negative)) }
        }

        val a = analyser.analyse(signals)
        assertEquals(VERBOSITY_UP, a.verbosityDelta)
    }

    @Test
    fun `leaves verbosity at 0 for a balanced window`() {
        val analyser = FeedbackAnalyser()
        val signals = buildList {
            repeat(5) { add(make(FeedbackPolarity.Positive)) }
            repeat(5) { add(make(FeedbackPolarity.Negative)) }
        }
        assertEquals(0f, analyser.analyse(signals).verbosityDelta)
    }

    @Test
    fun `treats exactly 70 percent as NOT crossing the threshold (strict gt)`() {
        val analyser = FeedbackAnalyser(10)
        // Exactly 7/10 negative — 0.70 is not > 0.70.
        val signals = buildList {
            repeat(7) { add(make(FeedbackPolarity.Negative)) }
            repeat(3) { add(make(FeedbackPolarity.Positive)) }
        }
        assertEquals(0f, analyser.analyse(signals).verbosityDelta)
    }

    @Test
    fun `only considers the most-recent windowSize signals (newest-first)`() {
        val analyser = FeedbackAnalyser(3)
        // Older bulk is positive; the 3 newest are negative → window is 100% negative.
        val older = buildList {
            repeat(10) { i -> add(make(FeedbackPolarity.Positive, Instant.ofEpochMilli(1000L + i))) }
        }
        val newest = buildList {
            repeat(3) { i -> add(make(FeedbackPolarity.Negative, Instant.ofEpochMilli(9_000_000L + i))) }
        }

        val a = analyser.analyse(older + newest)
        assertEquals(VERBOSITY_DOWN, a.verbosityDelta)
    }

    @Test
    fun `ignores Correction signals in the ratio (neither positive nor negative)`() {
        val analyser = FeedbackAnalyser()
        // 8 negative + 2 correction = 8/10 = 80% negative → down.
        val signals = buildList {
            repeat(8) { add(make(FeedbackPolarity.Negative)) }
            repeat(2) { add(make(FeedbackPolarity.Correction)) }
        }
        assertEquals(VERBOSITY_DOWN, analyser.analyse(signals).verbosityDelta)
    }

    // ══════════════════════════════════════════════════════════════════════
    // InMemoryFeedbackStore
    // ══════════════════════════════════════════════════════════════════════

    @Test
    fun `add increments the count`() = runTest {
        val store = InMemoryFeedbackStore()
        store.addAsync(make(FeedbackPolarity.Positive))
        assertEquals(1, store.countAsync())
    }

    @Test
    fun `getRecent on an empty store returns empty`() = runTest {
        val store = InMemoryFeedbackStore()
        assertEquals(emptyList(), store.getRecentAsync(10))
    }

    @Test
    fun `getRecent returns newest-first`() = runTest {
        val store = InMemoryFeedbackStore()
        val now = Instant.now()
        store.addAsync(make(FeedbackPolarity.Positive, now.minusSeconds(600), "old"))
        store.addAsync(make(FeedbackPolarity.Negative, now, "new"))

        val result = store.getRecentAsync(10)
        assertEquals(2, result.size)
        assertEquals("new", result[0].userText)
    }

    @Test
    fun `positiveRatio returns null with no signals`() = runTest {
        val store = InMemoryFeedbackStore()
        assertNull(store.positiveRatioAsync())
    }

    @Test
    fun `positiveRatio returns 1_0 when all positive`() = runTest {
        val store = InMemoryFeedbackStore()
        store.addAsync(make(FeedbackPolarity.Positive))
        store.addAsync(make(FeedbackPolarity.Positive))
        assertEquals(1.0, store.positiveRatioAsync())
    }

    @Test
    fun `positiveRatio returns the right fraction for mixed signals`() = runTest {
        val store = InMemoryFeedbackStore()
        store.addAsync(make(FeedbackPolarity.Positive))
        store.addAsync(make(FeedbackPolarity.Positive))
        store.addAsync(make(FeedbackPolarity.Negative))
        val ratio = store.positiveRatioAsync()
        assertTrue(ratio != null && ratio > 0.66 && ratio < 0.68) // 2/3
    }

    @Test
    fun `evicts the oldest when maxSignals is exceeded (FIFO)`() = runTest {
        val store = InMemoryFeedbackStore(3)
        repeat(5) { i -> store.addAsync(make(FeedbackPolarity.Positive, null, "u$i")) }
        assertEquals(3, store.countAsync())
    }

    @Test
    fun `rejects a non-positive maxSignals`() {
        assertFailsWith<IllegalArgumentException> { InMemoryFeedbackStore(0) }
    }
}
