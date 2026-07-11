// Biosignals.kt
//
// Kotlin port of CircleAI.Wearable.Biosignals (BiosignalKind.cs +
// BiosignalSample.cs + IBiosignalSource.cs + NullBiosignalSource.cs +
// RecordedBiosignalSource.cs + BiosignalAggregator.cs + BiosignalAffectMapper.cs)
// — the C# reference is the EXACT spec. The wearable biosignal layer: canonical
// signal taxonomy, samples, a streaming source contract with null + recorded
// implementations, a sliding-window aggregator, and a deterministic affect mapper.
//
// Fidelity notes:
//   * C# `enum BiosignalKind` (explicit values 0..8) -> Kotlin `enum class` in the
//     SAME order — ordinals are the stable wire values; do not reorder.
//   * C# `sealed record BiosignalSample` + static `Create` -> `data class` +
//     `companion object create(...)`; `Guid` -> `UUID`, `float` -> `Float`,
//     `DateTimeOffset` -> `Instant`; confidence clamped to [0, 1].
//   * C# `IAsyncEnumerable<BiosignalSample>` -> `Flow<BiosignalSample>`;
//     `Task<bool>` -> `suspend fun`.
//   * `RecordedBiosignalSource` replays samples in order, honouring the replay
//     delay and cancellation; supported kinds = distinct kinds present.
//   * `BiosignalAggregator.snapshotAsync(window)` is a single-shot, time-bound
//     read: it drops samples older than `now - window`, accumulates min/max/mean/
//     count per kind, and stops when the stream ends, the deadline passes, or the
//     window elapses (via `withTimeoutOrNull`). `window <= 0` throws.
//   * `BiosignalAffectMapper.apply(sample, affect)` reproduces the C# rule sheet
//     byte-for-byte (confidence gate 0.5; HR/HRV/SpO2 thresholds and deltas; all
//     mutations clamped to [0, 1]); sets `lastUpdatedUtc` on any processed sample.
//   * The C# `[Experimental]` / `[CircleAIVerificationStatus]` attributes are
//     compile-time metadata with no runtime behaviour and have no Kotlin analogue
//     in this tree — omitted, as with other ports.

package com.bhengubv.circleai.wearable.biosignals

import com.bhengubv.circleai.memory.AffectState
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withTimeoutOrNull
import java.time.Duration
import java.time.Instant
import java.util.UUID

// =====================================================================
// BiosignalKind (BiosignalKind.cs)
// =====================================================================

/**
 * Canonical kinds of biosignal samples. Ordinals mirror the C# explicit values
 * (0..8) and are stable across ports — do not reorder. Mirrors C# `BiosignalKind`.
 */
enum class BiosignalKind {
    /** Heart rate, beats per minute. (0) */
    HeartRate,

    /** Heart rate variability, RMSSD in milliseconds. (1) */
    HeartRateVariability,

    /** Peripheral oxygen saturation, percent (0-100). (2) */
    OxygenSaturation,

    /** Accelerometer magnitude, m/s^2. (3) */
    Accelerometer,

    /** Body temperature, degrees Celsius. (4) */
    BodyTemperature,

    /** Sleep stage: 0=awake, 1=light, 2=deep, 3=REM. (5) */
    SleepStage,

    /** Step count (cumulative or delta — see [BiosignalSample.isCumulative]). (6) */
    Steps,

    /** Galvanic skin response, microsiemens. (7) */
    GalvanicSkinResponse,

    /** Catch-all for vendor-specific or future signals. (8) */
    Unknown,
}

// =====================================================================
// BiosignalSample (BiosignalSample.cs)
// =====================================================================

/**
 * A single biosignal measurement. Mirrors C# `BiosignalSample`.
 *
 * @param id Stable identifier for this sample.
 * @param kind The kind of signal.
 * @param value Numeric value in the canonical unit for the kind.
 * @param unit Canonical unit string ("bpm", "ms", "%", "m/s^2", "celsius", "stage", "count", "uS").
 * @param confidence Sensor-reported confidence in [0, 1]; samples below 0.5 are ignored by the mapper.
 * @param isCumulative True when [kind] is [BiosignalKind.Steps] and the value is total-since-epoch.
 * @param measuredAt UTC time the sample was captured.
 */
data class BiosignalSample(
    val id: UUID,
    val kind: BiosignalKind,
    val value: Float,
    val unit: String,
    val confidence: Float,
    val isCumulative: Boolean,
    val measuredAt: Instant,
) {
    companion object {
        /**
         * Creates a fresh sample with a new [UUID], current UTC timestamp, and
         * confidence clamped to [0, 1]. Mirrors C# `BiosignalSample.Create`.
         */
        fun create(
            kind: BiosignalKind,
            value: Float,
            unit: String,
            confidence: Float = 1.0f,
            isCumulative: Boolean = false,
        ): BiosignalSample = BiosignalSample(
            UUID.randomUUID(),
            kind,
            value,
            unit,
            confidence.coerceIn(0f, 1f),
            isCumulative,
            Instant.now(),
        )
    }
}

// =====================================================================
// IBiosignalSource (IBiosignalSource.cs)
// =====================================================================

/**
 * A streaming source of biosignal samples — a wearable, a health API, or a
 * simulator for tests. Mirrors C# `IBiosignalSource`.
 */
interface IBiosignalSource {
    /** The kinds of signals this source can emit. May be empty. */
    val supportedKinds: Array<BiosignalKind>

    /** Streams samples until the coroutine is cancelled or the device disconnects. */
    fun streamAsync(): Flow<BiosignalSample>

    /** Reports whether this source can produce samples of the given kind. */
    suspend fun isSupportedAsync(kind: BiosignalKind): Boolean
}

// =====================================================================
// NullBiosignalSource (NullBiosignalSource.cs)
// =====================================================================

/**
 * A biosignal source that supports nothing and emits nothing — the "no wearable
 * connected" case. Mirrors C# `NullBiosignalSource`.
 */
class NullBiosignalSource : IBiosignalSource {
    override val supportedKinds: Array<BiosignalKind> = emptyArray()
    override suspend fun isSupportedAsync(kind: BiosignalKind): Boolean = false
    override fun streamAsync(): Flow<BiosignalSample> = flow { /* yields nothing */ }
}

// =====================================================================
// RecordedBiosignalSource (RecordedBiosignalSource.cs)
// =====================================================================

/**
 * Replays a recorded biosignal stream — useful for tests, training data, and
 * host integration when no live wearable is connected. Mirrors C#
 * `RecordedBiosignalSource`.
 */
class RecordedBiosignalSource(
    private val samples: List<BiosignalSample>,
    replayDelay: Duration? = null,
) : IBiosignalSource {
    private val replayDelay: Duration = replayDelay ?: Duration.ZERO
    private val kinds: Array<BiosignalKind> = samples.map { it.kind }.toSet().toTypedArray()

    override val supportedKinds: Array<BiosignalKind> get() = kinds

    override suspend fun isSupportedAsync(kind: BiosignalKind): Boolean = kinds.any { it == kind }

    override fun streamAsync(): Flow<BiosignalSample> = flow {
        for (s in samples) {
            currentCoroutineContext().ensureActive() // honours cancellation, like ThrowIfCancellationRequested
            if (replayDelay > Duration.ZERO) delay(replayDelay.toMillis())
            emit(s)
        }
    }
}

// =====================================================================
// BiosignalAggregator (BiosignalAggregator.cs)
// =====================================================================

/**
 * Per-kind aggregate statistics over a sliding window. Mirrors C# `BiosignalStats`.
 */
data class BiosignalStats(val sampleCount: Int, val min: Float, val max: Float, val mean: Float)

/**
 * A snapshot of biosignal aggregates across all observed kinds. Kinds with no
 * samples in the window are absent. Mirrors C# `BiosignalSnapshot`.
 */
data class BiosignalSnapshot(val stats: Map<BiosignalKind, BiosignalStats>, val generatedAt: Instant)

/**
 * Sliding-window aggregator over an [IBiosignalSource]. Mirrors C#
 * `BiosignalAggregator`.
 */
class BiosignalAggregator(private val source: IBiosignalSource) {

    /**
     * Consumes samples until the source completes or the elapsed time exceeds
     * [window], then returns a snapshot over samples within the window (relative
     * to UTC now at call time). Single-shot — call repeatedly for continuous
     * aggregation. Mirrors C# `BiosignalAggregator.SnapshotAsync`.
     */
    suspend fun snapshotAsync(window: Duration): BiosignalSnapshot {
        if (window <= Duration.ZERO) throw IllegalArgumentException("Window must be positive.")

        val generatedAt = Instant.now()
        val cutoff = generatedAt.minus(window)
        val deadline = generatedAt.plus(window)
        val accumulator = HashMap<BiosignalKind, Accumulator>()

        // Time-bound the read so a never-completing source still yields a snapshot.
        // The collect stops on: stream completion, the window timeout, or the
        // deadline (signalled by DeadlineReached and caught here). All three
        // fall through to build the snapshot from whatever was accumulated.
        try {
            withTimeoutOrNull(window.toMillis()) {
                source.streamAsync().collect { sample ->
                    if (sample.measuredAt < cutoff) return@collect
                    val acc = accumulator.getOrPut(sample.kind) { Accumulator() }
                    acc.add(sample.value)
                    if (!Instant.now().isBefore(deadline)) throw DeadlineReached()
                }
            }
        } catch (_: DeadlineReached) {
            // Deadline passed before the source completed — expected; fall through.
        }

        val stats = HashMap<BiosignalKind, BiosignalStats>(accumulator.size)
        for ((kind, acc) in accumulator) stats[kind] = acc.toStats()
        return BiosignalSnapshot(stats, generatedAt)
    }

    /** Internal control-flow signal to break collection at the deadline. */
    private class DeadlineReached : RuntimeException() {
        override fun fillInStackTrace(): Throwable = this
    }

    private class Accumulator {
        private var count = 0
        private var min = Float.POSITIVE_INFINITY
        private var max = Float.NEGATIVE_INFINITY
        private var sum = 0.0

        fun add(v: Float) {
            count++
            if (v < min) min = v
            if (v > max) max = v
            sum += v
        }

        fun toStats(): BiosignalStats =
            BiosignalStats(count, min, max, if (count == 0) 0f else (sum / count).toFloat())
    }
}

// =====================================================================
// BiosignalAffectMapper (BiosignalAffectMapper.cs)
// =====================================================================

/**
 * Maps biosignal samples to [AffectState] mutations using deterministic,
 * fixture-validated rules. Mirrors C# `BiosignalAffectMapper`.
 *
 * Rule sheet (all mutations clamped to [0, 1]):
 *  - HeartRate > 130 bpm (conf >= 0.5): Energy += 0.10, Uncertainty += 0.05.
 *  - HeartRate > 100 bpm (conf >= 0.5): Energy += 0.05.
 *  - HeartRate < 50 bpm  (conf >= 0.5): Energy -= 0.05.
 *  - HRV < 20 ms (conf >= 0.5): Uncertainty += 0.05, Rapport -= 0.02.
 *  - HRV > 60 ms (conf >= 0.5): Engagement += 0.02.
 *  - SpO2 < 90 % (conf >= 0.5): Uncertainty += 0.10.
 *  - SleepStage / other kinds: no mutation.
 *  - Confidence < 0.5 on any signal: no mutation.
 */
object BiosignalAffectMapper {
    private const val MIN_CONFIDENCE = 0.5f

    /**
     * Applies the rule for [sample] to [affect], mutating it in place. Safe to
     * call repeatedly — all field values are clamped to [0, 1]. Mirrors C# `Apply`.
     */
    fun apply(sample: BiosignalSample, affect: AffectState) {
        // Confidence gate — low-confidence samples never mutate state.
        if (sample.confidence < MIN_CONFIDENCE) return

        when (sample.kind) {
            BiosignalKind.HeartRate -> applyHeartRate(sample.value, affect)
            BiosignalKind.HeartRateVariability -> applyHrv(sample.value, affect)
            BiosignalKind.OxygenSaturation -> applySpO2(sample.value, affect)
            BiosignalKind.SleepStage -> { /* user not interacting; no mutation */ }
            else -> { /* Accelerometer, Temperature, Steps, GSR, Unknown — no affect rule */ }
        }

        affect.lastUpdatedUtc = Instant.now()
    }

    private fun applyHeartRate(bpm: Float, a: AffectState) {
        when {
            bpm > 130f -> {
                a.energy = clamp01(a.energy + 0.10f)
                a.uncertainty = clamp01(a.uncertainty + 0.05f)
            }
            bpm > 100f -> a.energy = clamp01(a.energy + 0.05f)
            bpm < 50f -> a.energy = clamp01(a.energy - 0.05f)
        }
    }

    private fun applyHrv(rmssdMs: Float, a: AffectState) {
        when {
            rmssdMs < 20f -> {
                a.uncertainty = clamp01(a.uncertainty + 0.05f)
                a.rapport = clamp01(a.rapport - 0.02f)
            }
            rmssdMs > 60f -> a.engagement = clamp01(a.engagement + 0.02f)
        }
    }

    private fun applySpO2(percent: Float, a: AffectState) {
        if (percent < 90f) a.uncertainty = clamp01(a.uncertainty + 0.10f)
    }

    private fun clamp01(v: Float): Float = v.coerceIn(0f, 1f)
}
