// PredictiveEngine.kt
//
// Kotlin port of the CircleAI.Companion predictive-engine contract and its two
// concrete implementations — the C# reference is the exact spec:
//   IPredictiveEngine         (HerJarvisContracts.cs, contract 14)
//   AnticipatedNeed           (record)
//   HistogramPredictiveEngine (HerJarvisRealImplementations.cs, impl 14)
//   SequencePredictiveEngine  (SequencePredictiveEngine.cs, Phase E4)
//
// A predictive engine anticipates the user's upcoming needs/events within a
// time horizon. HistogramPredictiveEngine models a time-of-day-and-weekday
// histogram; SequencePredictiveEngine is a variable-order Markov chain over the
// observed event timeline. Both are in-memory, deterministic, thread-safe.

package com.bhengubv.circleai.companion.reasoning

import java.time.Instant
import java.time.ZoneOffset
import java.util.concurrent.ConcurrentHashMap
import kotlin.math.pow

// ---------------------------------------------------------------------------
// AnticipatedNeed
// ---------------------------------------------------------------------------

/**
 * A need/event the engine expects to occur by [expectedByUtc] with the given
 * [probability]. Mirrors the C# `AnticipatedNeed` record.
 */
data class AnticipatedNeed(
    val description: String,
    val expectedByUtc: Instant,
    val probability: Double,
)

// ---------------------------------------------------------------------------
// IPredictiveEngine
// ---------------------------------------------------------------------------

/** Predictive engine: anticipate upcoming needs within [horizonMinutes]. */
interface IPredictiveEngine {
    suspend fun anticipateAsync(horizonMinutes: Int): List<AnticipatedNeed>
}

// ---------------------------------------------------------------------------
// UTC slot helper
// ---------------------------------------------------------------------------

/**
 * The 0..167 histogram slot for an instant: `dayOfWeek * 24 + hour`, where
 * dayOfWeek is Sunday=0 .. Saturday=6 (matching C# `DateTimeOffset.DayOfWeek`),
 * and hour is the UTC hour. Java's `DayOfWeek` is Monday=1..Sunday=7, so
 * `value % 7` remaps Sunday(7)→0 and leaves Mon..Sat as 1..6.
 */
private fun utcSlot(instant: Instant): Int {
    val dt = instant.atZone(ZoneOffset.UTC)
    val dow = dt.dayOfWeek.value % 7 // Sunday=0 .. Saturday=6
    return dow * 24 + dt.hour
}

// ---------------------------------------------------------------------------
// HistogramPredictiveEngine
// ---------------------------------------------------------------------------

/**
 * Time-of-day (× weekday) histogram of recurring events. Each observed need
 * increments a 24×7 = 168-slot counter for its (weekday, hour). Anticipation
 * scans the histogram from now to now+horizon in 30-minute steps and scores
 * each need by (upcoming hits) / (total hits), expecting it at now+horizon/2.
 */
class HistogramPredictiveEngine : IPredictiveEngine {
    private val hist = ConcurrentHashMap<String, LongArray>()
    private val lock = Any()

    /** Tell the engine: this need occurred at this UTC time. */
    fun observe(description: String, atUtc: Instant) {
        require(description.isNotBlank()) { "description required" }
        val arr = hist.getOrPut(description) { LongArray(24 * 7) }
        val slot = utcSlot(atUtc)
        synchronized(lock) { arr[slot]++ }
    }

    override suspend fun anticipateAsync(horizonMinutes: Int): List<AnticipatedNeed> {
        require(horizonMinutes > 0) { "horizonMinutes must be > 0" }
        val now = Instant.now()
        val results = ArrayList<AnticipatedNeed>()
        for ((desc, arr) in hist) {
            val total: Long
            var upcoming: Long
            synchronized(lock) {
                total = arr.sum()
                upcoming = 0
                var m = 0
                while (m <= horizonMinutes) {
                    val slot = utcSlot(now.plusSeconds(m.toLong() * 60))
                    upcoming += arr[slot]
                    m += 30
                }
            }
            if (total == 0L || upcoming == 0L) continue
            results.add(
                AnticipatedNeed(
                    description = desc,
                    // horizonMinutes / 2 is integer division, matching C#.
                    expectedByUtc = now.plusSeconds((horizonMinutes / 2).toLong() * 60),
                    probability = upcoming.toDouble() / total,
                ),
            )
        }
        return results.sortedByDescending { it.probability }
    }
}

// ---------------------------------------------------------------------------
// SequencePredictiveEngine
// ---------------------------------------------------------------------------

/**
 * A variable-order Markov chain (default 3-gram) over the user's observed event
 * timeline. Anticipation takes the most recent [order] events as context, backs
 * off from the longest context to the shortest — weighting a length-k context by
 * 2^k — and forecasts each candidate's arrival time from its mean inter-arrival
 * interval, dropping any whose mean interval exceeds the horizon.
 */
class SequencePredictiveEngine(private val order: Int = 3) : IPredictiveEngine {

    init {
        require(order in 1..6) { "order must be in 1..6" }
    }

    private data class TimedEvent(val event: String, val atUtc: Instant)
    private data class Inter(val count: Long, val sumSeconds: Double)

    // (previous-n-events joined by '|') -> { next event -> count }.
    private val transitions = ConcurrentHashMap<String, ConcurrentHashMap<String, Long>>()
    // per-event running (count, sum of inter-arrival seconds).
    private val interArrivals = ConcurrentHashMap<String, Inter>()
    private val history = ArrayList<TimedEvent>()
    private val historyLock = Any()

    /** Add one event to the user timeline. */
    fun observe(event: String, atUtc: Instant) {
        require(event.isNotBlank()) { "event required" }
        synchronized(historyLock) {
            history.add(TimedEvent(event, atUtc))
            // Build n-gram contexts up to `order`.
            var k = 1
            while (k <= order && history.size > k) {
                val contextStart = history.size - 1 - k
                if (contextStart < 0) break
                val contextItems = (contextStart until contextStart + k).map { history[it].event }
                val key = contextItems.joinToString("|")
                val bucket = transitions.getOrPut(key) { ConcurrentHashMap() }
                bucket.merge(event, 1L, Long::plus)
                k++
            }
            // Track inter-arrival time for this event (only when the immediately
            // preceding event is the same event — matches the C# reference).
            if (history.size >= 2) {
                val last = history[history.size - 2]
                if (last.event == event) {
                    val gap = (atUtc.toEpochMilli() - last.atUtc.toEpochMilli()) / 1000.0
                    interArrivals.merge(event, Inter(1, gap)) { _, prev ->
                        Inter(prev.count + 1, prev.sumSeconds + gap)
                    }
                }
            }
        }
    }

    override suspend fun anticipateAsync(horizonMinutes: Int): List<AnticipatedNeed> {
        require(horizonMinutes > 0) { "horizonMinutes must be > 0" }

        val snapshot: List<TimedEvent>
        synchronized(historyLock) { snapshot = ArrayList(history) }
        if (snapshot.isEmpty()) return emptyList()

        // Take the most recent `order` events as the prediction context.
        val contextLen = minOf(order, snapshot.size)
        val context = (snapshot.size - contextLen until snapshot.size).map { snapshot[it].event }

        val totalScore = LinkedHashMap<String, Double>()
        // Walk from longest context to shortest (back-off), weighting longer higher.
        var k = context.size
        while (k >= 1) {
            val key = (context.size - k until context.size).joinToString("|") { context[it] }
            val bucket = transitions[key]
            if (bucket != null) {
                val totalForCtx = bucket.values.sum()
                if (totalForCtx != 0L) {
                    val weight = 2.0.pow(k)
                    for ((next, count) in bucket) {
                        val prob = count.toDouble() / totalForCtx
                        totalScore[next] = (totalScore[next] ?: 0.0) + weight * prob
                    }
                }
            }
            k--
        }

        if (totalScore.isEmpty()) return emptyList()

        val totalWeight = totalScore.values.sum()
        val horizonSec = horizonMinutes * 60.0
        val now = Instant.now()
        val anticipated = ArrayList<AnticipatedNeed>()
        for ((ev, raw) in totalScore.entries.sortedByDescending { it.value }) {
            val prob = raw / totalWeight
            if (prob <= 0) continue
            // Use the event's mean inter-arrival to estimate when it'll happen.
            val inter = interArrivals[ev]
            val meanInterval = if (inter != null && inter.count > 0) inter.sumSeconds / inter.count else horizonSec * 0.5
            if (meanInterval > horizonSec) continue // not expected within window
            anticipated.add(
                AnticipatedNeed(
                    description = ev,
                    expectedByUtc = now.plusMillis((meanInterval * 1000).toLong()),
                    probability = prob,
                ),
            )
        }
        return anticipated
    }
}
