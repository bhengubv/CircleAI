// WarmupTest.kt
//
// Verifies HistogramRequestPredictor + PredictiveWarmupController against the C#
// reference: cold-start zero-confidence, EWMA rate learning driving a Poisson
// probability, threshold + min-interval gating of pre-warm, and the disabled
// default.

package com.bhengubv.circleai.hosting

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import java.time.ZoneOffset
import java.time.ZonedDateTime
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class WarmupTest {

    private fun utc(h: Int, mi: Int): Instant =
        ZonedDateTime.of(2026, 7, 8, h, mi, 0, 0, ZoneOffset.UTC).toInstant()

    @Test
    fun `cold start returns zero forecast`() {
        val p = HistogramRequestPredictor()
        val f = p.predict(utc(9, 0), Duration.ofSeconds(60))
        assertEquals(0.0, f.probabilityOfArrival)
        assertEquals(0.0, f.confidence)
        assertEquals(0L, p.observedArrivals)
    }

    @Test
    fun `repeated arrivals at a slot raise probability and confidence`() {
        val p = HistogramRequestPredictor()
        // Hammer the 09:00 slot many times over "different days" (same minute-of-day).
        repeat(60) { p.recordArrival(utc(9, 0)) }
        assertEquals(60L, p.observedArrivals)

        val f = p.predict(utc(9, 0), Duration.ofMinutes(1))
        assertTrue(f.expectedCount > 0.0)
        assertTrue(f.probabilityOfArrival > 0.0)
        assertTrue(f.confidence > 0.0)

        // A quiet slot (03:00) forecasts ~nothing.
        val quiet = p.predict(utc(3, 0), Duration.ofMinutes(1))
        assertEquals(0.0, quiet.expectedCount)
    }

    @Test
    fun `controller warms when score crosses threshold and respects min interval`() = runTest {
        val service = FakeAIService()
        val predictor = HistogramRequestPredictor()
        // Prime a strong signal across the 09:00 and 09:06 minute-of-day slots
        // (the histogram is keyed by minute-of-day, so both tick times need signal).
        repeat(80) { predictor.recordArrival(utc(9, 0)) }
        repeat(80) { predictor.recordArrival(utc(9, 6)) }

        var now = utc(9, 0)
        val controller = PredictiveWarmupController(
            service = service,
            predictor = predictor,
            options = PredictiveWarmupOptions(
                enabled = true,
                warmupThreshold = 0.3,
                minTimeBetweenWarmups = Duration.ofMinutes(5),
            ),
            clock = { now },
        )

        assertTrue(controller.tickAsync())
        assertEquals(1, service.prewarmCount)

        // Immediate second tick is gated by min-interval.
        assertFalse(controller.tickAsync())
        assertEquals(1, service.prewarmCount)

        // After the interval, it fires again.
        now = utc(9, 6)
        assertTrue(controller.tickAsync())
        assertEquals(2, service.prewarmCount)

        controller.close()
    }

    @Test
    fun `disabled controller never warms`() = runTest {
        val service = FakeAIService()
        val predictor = HistogramRequestPredictor()
        repeat(80) { predictor.recordArrival(utc(9, 0)) }
        val controller = PredictiveWarmupController(
            service, predictor,
            PredictiveWarmupOptions(enabled = false, warmupThreshold = 0.0),
            clock = { utc(9, 0) },
        )
        controller.startAsync() // no-op when disabled
        assertEquals(0, service.prewarmCount)
        controller.close()
    }
}
