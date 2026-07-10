// DefaultAnomalyEventDispatcherTest.kt
//
// Verifies verify -> dedup -> dispatch: below-threshold ignored, duplicate id
// deduped, cancellation reported, and accepted signals reach the watchdog with
// the response surfaced.

package com.bhengubv.circleai.security

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertSame
import kotlin.test.assertTrue

class DefaultAnomalyEventDispatcherTest {

    private class CountingWatchdog : ISecurityWatchdog {
        val calls = AtomicInteger()
        var lastResponse: SecurityResponse? = null
        override suspend fun onAnomalyDetected(
            signal: AnomalySignal,
            checkpoint: SecurityCheckpoint?,
        ): SecurityResponse {
            calls.incrementAndGet()
            val r = SecurityResponse.forKeyRotation(signal.id, "rotated")
            lastResponse = r
            return r
        }

        override fun streamSignals(): Flow<AnomalySignal> = emptyFlow()
    }

    private fun signal(confidence: Float, id: java.util.UUID? = null): AnomalySignal {
        val base = AnomalySignal.create(ThreatVector.MemoryAnomaly, confidence, "mod", "a")
        return if (id == null) base else base.copy(id = id)
    }

    @Test
    fun `below threshold is not dispatched`() = runTest {
        val wd = CountingWatchdog()
        val d = DefaultAnomalyEventDispatcher(wd, minimumConfidence = 0.30)
        val result = d.verifyAndDispatch(signal(0.10f))
        assertEquals(AnomalyDispatchOutcome.BelowThreshold, result.outcome)
        assertNull(result.response)
        assertEquals(0, wd.calls.get())
    }

    @Test
    fun `at-or-above threshold dispatches and surfaces the response`() = runTest {
        val wd = CountingWatchdog()
        val d = DefaultAnomalyEventDispatcher(wd, minimumConfidence = 0.30)
        val result = d.verifyAndDispatch(signal(0.30f))
        assertEquals(AnomalyDispatchOutcome.Dispatched, result.outcome)
        assertEquals(1, wd.calls.get())
        assertSame(wd.lastResponse, result.response)
    }

    @Test
    fun `duplicate signal id is deduped`() = runTest {
        val wd = CountingWatchdog()
        val d = DefaultAnomalyEventDispatcher(wd)
        val id = java.util.UUID.randomUUID()
        val first = d.verifyAndDispatch(signal(0.90f, id))
        val second = d.verifyAndDispatch(signal(0.90f, id))
        assertEquals(AnomalyDispatchOutcome.Dispatched, first.outcome)
        assertEquals(AnomalyDispatchOutcome.Duplicate, second.outcome)
        assertNull(second.response)
        assertEquals(1, wd.calls.get(), "watchdog invoked once for the id")
    }

    @Test
    fun `distinct ids are both dispatched`() = runTest {
        val wd = CountingWatchdog()
        val d = DefaultAnomalyEventDispatcher(wd)
        assertEquals(AnomalyDispatchOutcome.Dispatched, d.verifyAndDispatch(signal(0.90f)).outcome)
        assertEquals(AnomalyDispatchOutcome.Dispatched, d.verifyAndDispatch(signal(0.90f)).outcome)
        assertEquals(2, wd.calls.get())
    }

    @Test
    fun `checkpoint is forwarded to the watchdog`() = runTest {
        val seen = CompletableDeferred<SecurityCheckpoint?>()
        val wd = object : ISecurityWatchdog {
            override suspend fun onAnomalyDetected(
                signal: AnomalySignal,
                checkpoint: SecurityCheckpoint?,
            ): SecurityResponse {
                seen.complete(checkpoint)
                return SecurityResponse.noAction(signal.id, "ok")
            }
            override fun streamSignals(): Flow<AnomalySignal> = emptyFlow()
        }
        val d = DefaultAnomalyEventDispatcher(wd, minimumConfidence = 0.0)
        val cp = SecurityCheckpoint.create("uhid-1", "mod", "x".toByteArray())
        d.verifyAndDispatch(signal(0.5f), cp)
        assertSame(cp, seen.await())
    }

    @Test
    fun `minimumConfidence is clamped into range`() = runTest {
        val wd = CountingWatchdog()
        // Out-of-range 2.0 clamps to 1.0 -> only confidence >= 1.0 passes.
        val d = DefaultAnomalyEventDispatcher(wd, minimumConfidence = 2.0)
        assertEquals(AnomalyDispatchOutcome.BelowThreshold, d.verifyAndDispatch(signal(0.99f)).outcome)
        assertEquals(AnomalyDispatchOutcome.Dispatched, d.verifyAndDispatch(signal(1.0f)).outcome)
    }
}
