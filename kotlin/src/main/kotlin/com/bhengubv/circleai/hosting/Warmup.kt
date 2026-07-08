// Warmup.kt
//
// Kotlin port of the CircleAI.Hosting.Warmup predictive pre-warm surface — the
// C# reference is the EXACT spec (IRequestPredictor.cs, HistogramRequestPredictor.cs,
// PredictiveWarmupOptions.cs, PredictiveWarmupController.cs).
//
// (RT-07) Local-only request-timeline learner + a background loop that pre-warms
// the generator before a predicted spike. The histogram EWMA + Poisson-tail
// arithmetic is byte-identical to the C# reference. All counting is in-process.

package com.bhengubv.circleai.hosting

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.time.Duration
import java.time.Instant
import java.time.ZoneOffset
import java.util.concurrent.atomic.AtomicLong
import kotlin.math.ceil
import kotlin.math.exp
import kotlin.math.min

// =====================================================================
// IRequestPredictor (IRequestPredictor.cs)
// =====================================================================

/**
 * (RT-07) Forecast of inbound requests over a window. Mirrors C# `ArrivalForecast`.
 *
 * @param probabilityOfArrival 0.0 .. 1.0.
 * @param expectedCount Best estimate of how many arrivals to expect.
 * @param confidence 0.0 .. 1.0. Cold-start histograms return ~0.
 */
data class ArrivalForecast(
    val probabilityOfArrival: Double,
    val expectedCount: Double,
    val confidence: Double,
)

/**
 * (RT-07) Local-only predictor that learns request arrival timing and forecasts
 * whether a spike is coming. Mirrors C# `IRequestPredictor`.
 */
interface IRequestPredictor {
    /** Record one arrival at [utc]. */
    fun recordArrival(utc: Instant)

    /**
     * Forecast arrivals in [forecastWindow] starting at [utcNow]. Returns a
     * forecast with confidence 0 when the learner has no signal yet.
     */
    fun predict(utcNow: Instant, forecastWindow: Duration): ArrivalForecast

    /** Total arrivals observed since construction. */
    val observedArrivals: Long
}

// =====================================================================
// HistogramRequestPredictor (HistogramRequestPredictor.cs)
// =====================================================================

/**
 * (RT-07) Default [IRequestPredictor] — keeps a histogram of per-minute arrival
 * rates over a rolling window of recent days, then forecasts the next-window rate
 * from that histogram. Thread-safe. Mirrors C# `HistogramRequestPredictor`
 * (EWMA + Poisson-tail arithmetic byte-identical).
 */
class HistogramRequestPredictor(
    private val historyDays: Int = 7,
) : IRequestPredictor {

    private val perMinuteRate = DoubleArray(MINUTES_PER_DAY) // avg arrivals/minute observed
    private val perMinuteCount = IntArray(MINUTES_PER_DAY)
    private val gate = Any()
    private val observed = AtomicLong(0)

    init {
        require(historyDays > 0) { "historyDays must be positive." }
    }

    override val observedArrivals: Long get() = observed.get()

    override fun recordArrival(utc: Instant) {
        val z = utc.atZone(ZoneOffset.UTC)
        val minute = z.hour * 60 + z.minute
        synchronized(gate) {
            val cnt = ++perMinuteCount[minute]
            // EWMA over the last `historyDays` of observations at this slot.
            val alpha = 2.0 / (min(cnt, historyDays) + 1)
            perMinuteRate[minute] = (alpha * 1.0) + ((1 - alpha) * perMinuteRate[minute])
        }
        observed.incrementAndGet()
    }

    override fun predict(utcNow: Instant, forecastWindow: Duration): ArrivalForecast {
        if (forecastWindow <= Duration.ZERO) return ArrivalForecast(0.0, 0.0, 0.0)
        val obs = observedArrivals
        if (obs == 0L) return ArrivalForecast(0.0, 0.0, 0.0)

        val z = utcNow.atZone(ZoneOffset.UTC)
        val minute = z.hour * 60 + z.minute
        val minutes = maxOf(1, ceil(forecastWindow.toMillis() / 60_000.0).toInt())
        var expected = 0.0
        var coveredSamples = 0
        synchronized(gate) {
            for (i in 0 until minutes) {
                val idx = (minute + i) % MINUTES_PER_DAY
                expected += perMinuteRate[idx]
                coveredSamples += perMinuteCount[idx]
            }
        }

        // Poisson tail: P(>=1 arrival) = 1 - exp(-lambda).
        val probability = 1.0 - exp(-expected)
        // Confidence rises as the per-minute slots accumulate samples.
        val confidence = min(
            WARM_CONFIDENCE,
            coveredSamples.toDouble() / (MIN_SAMPLES_FOR_FULL_CONFIDENCE * minutes),
        )
        return ArrivalForecast(probability, expected, confidence)
    }

    /** Test-only — wipe state. */
    fun resetForTests() {
        synchronized(gate) {
            perMinuteRate.fill(0.0)
            perMinuteCount.fill(0)
        }
        observed.set(0)
    }

    private companion object {
        const val MINUTES_PER_DAY = 24 * 60
        const val WARM_CONFIDENCE = 1.0
        const val MIN_SAMPLES_FOR_FULL_CONFIDENCE = 25
    }
}

// =====================================================================
// PredictiveWarmupOptions (PredictiveWarmupOptions.cs)
// =====================================================================

/** (RT-07) Configuration for [PredictiveWarmupController]. Mirrors C# `PredictiveWarmupOptions`. */
data class PredictiveWarmupOptions(
    /** When false (default), the controller does not pre-warm. Opt-in. */
    val enabled: Boolean = false,
    /** How often the controller asks the predictor about the upcoming window. Default 30 s. */
    val pollInterval: Duration = Duration.ofSeconds(30),
    /** How far ahead to forecast. Default 60 s. */
    val forecastWindow: Duration = Duration.ofSeconds(60),
    /** Pre-warm when forecast `probabilityOfArrival × confidence` >= this threshold. Default 0.5. */
    val warmupThreshold: Double = 0.5,
    /** Minimum delay between consecutive pre-warm calls. Default 5 minutes. */
    val minTimeBetweenWarmups: Duration = Duration.ofMinutes(5),
)

// =====================================================================
// PredictiveWarmupController (PredictiveWarmupController.cs)
// =====================================================================

/**
 * (RT-07) Async background loop that polls an [IRequestPredictor] and triggers
 * [IAIService] pre-warm before predicted spikes. Mirrors C#
 * `PredictiveWarmupController`.
 */
class PredictiveWarmupController(
    private val service: IAIService,
    private val predictor: IRequestPredictor,
    private val options: PredictiveWarmupOptions,
    private val clock: () -> Instant = { Instant.now() },
) : AutoCloseable {

    private var scope: CoroutineScope? = null
    private var loop: Job? = null
    private var lastWarmup: Instant = Instant.MIN
    private var disposed = false

    /** Begin polling on a background loop. No-op when [PredictiveWarmupOptions.enabled] is false. */
    fun startAsync() {
        check(!disposed) { "PredictiveWarmupController is disposed." }
        if (!options.enabled || loop != null) return
        val s = CoroutineScope(Dispatchers.Default + Job())
        scope = s
        loop = s.launch { runLoop() }
    }

    /** Convenience — record a request arrival on the underlying predictor at "now". */
    fun notifyArrival() = predictor.recordArrival(clock())

    /**
     * Run one prediction + decide-and-maybe-warm cycle. Returns true when warmup
     * was triggered. Public for tests + manual poking. Mirrors C# `TickAsync`.
     */
    suspend fun tickAsync(): Boolean {
        val now = clock()
        val forecast = predictor.predict(now, options.forecastWindow)
        val score = forecast.probabilityOfArrival * forecast.confidence
        if (score < options.warmupThreshold) return false
        if (Duration.between(lastWarmup, now) < options.minTimeBetweenWarmups) return false

        return try {
            lastWarmup = now
            service.prewarmAsync()
            true
        } catch (ce: CancellationException) {
            throw ce
        } catch (_: Exception) {
            false
        }
    }

    private suspend fun runLoop() {
        val self = scope ?: return
        try {
            tickAsync()
            while (self.isActive) {
                delay(options.pollInterval.toMillis())
                tickAsync()
            }
        } catch (_: CancellationException) {
            // normal
        } catch (_: Exception) {
            // loop crashed — logged in C#.
        }
    }

    override fun close() {
        if (disposed) return
        disposed = true
        loop?.cancel()
        scope?.coroutineContext?.get(Job)?.cancel()
        loop = null
        scope = null
    }
}
