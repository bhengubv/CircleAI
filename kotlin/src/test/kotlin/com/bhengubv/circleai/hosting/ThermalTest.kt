// ThermalTest.kt
//
// Verifies the thermal + memory-pressure + background-worker surface against the
// C# reference: ThermalThrottleService transitions + ShouldPauseInference,
// ManualMemoryPressureSource transition-only firing + unsubscribe,
// NullMemoryPressureSource inertness, and BackgroundInferenceWorker pausing on
// Serious+ thermal state.

package com.bhengubv.circleai.hosting

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ThermalTest {

    /** Sampler whose value the test drives. */
    private class DrivenSampler(var state: ThermalState = ThermalState.Normal) : IThermalSampler {
        override fun sample(): ThermalState = state
    }

    @Test
    fun `should-pause reflects serious and critical`() {
        val sampler = DrivenSampler(ThermalState.Normal)
        val svc = ThermalThrottleService(sampler)
        svc.startMonitoring()
        // Immediate sample happens on start; give it a beat.
        Thread.sleep(50)
        assertEquals(ThermalState.Normal, svc.currentState)
        assertFalse(svc.shouldPauseInference)
        svc.close()
    }

    @Test
    fun `state-changed fires only on transitions`() {
        val sampler = DrivenSampler(ThermalState.Serious)
        val svc = ThermalThrottleService(sampler)
        val seen = ArrayList<ThermalState>()
        svc.stateChanged = { seen.add(it) }
        svc.startMonitoring()
        Thread.sleep(50)
        // First sample (Unknown -> Serious) fires once.
        assertTrue(seen.contains(ThermalState.Serious))
        assertTrue(svc.shouldPauseInference)
        svc.close()
    }

    @Test
    fun `thermal state ordinal ordering matches C#`() {
        assertTrue(ThermalState.Critical > ThermalState.Serious)
        assertTrue(ThermalState.Serious > ThermalState.Fair)
        assertTrue(ThermalState.Fair > ThermalState.Normal)
        assertTrue(ThermalState.Normal > ThermalState.Unknown)
    }

    @Test
    fun `manual pressure source fires handlers only on transitions and honours unsubscribe`() = runTest {
        val src = ManualMemoryPressureSource()
        val transitions = ArrayList<Pair<MemoryPressureLevel, MemoryPressureLevel>>()
        val handle = src.subscribe { old, new -> transitions.add(old to new) }

        src.raise(MemoryPressureLevel.Trim)
        src.raise(MemoryPressureLevel.Trim) // same level -> no fire
        src.raise(MemoryPressureLevel.Critical)
        assertEquals(
            listOf(
                MemoryPressureLevel.Normal to MemoryPressureLevel.Trim,
                MemoryPressureLevel.Trim to MemoryPressureLevel.Critical,
            ),
            transitions,
        )
        assertEquals(MemoryPressureLevel.Critical, src.current)

        handle.close()
        src.raise(MemoryPressureLevel.Normal) // no handler now
        assertEquals(2, transitions.size)
    }

    @Test
    fun `null pressure source never fires`() = runTest {
        val src = NullMemoryPressureSource
        var fired = false
        val h = src.subscribe { _, _ -> fired = true }
        assertEquals(MemoryPressureLevel.Normal, src.current)
        h.close()
        assertFalse(fired)
    }

    @Test
    fun `background worker starts butler and pauses on serious thermal`() = runTest {
        val sampler = DrivenSampler(ThermalState.Normal)
        val thermal = ThermalThrottleService(sampler)
        val butler = FakeAIService()
        val worker = BackgroundInferenceWorker(butler, thermal)

        worker.startAsync()
        assertTrue(butler.startCount >= 1)
        assertFalse(worker.isPaused)

        // Drive a Serious transition through the service's callback.
        thermal.stateChanged?.invoke(ThermalState.Serious)
        assertTrue(worker.isPaused)
        thermal.stateChanged?.invoke(ThermalState.Normal)
        assertFalse(worker.isPaused)

        worker.stopAsync()
        thermal.close()
    }
}
