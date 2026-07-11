// WearableTest.kt — verifies the CircleAI.Wearable port against the C# reference.

package com.bhengubv.circleai.wearable

import com.bhengubv.circleai.companion.InterfaceKind
import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class WearableTest {

    private val t0 = Instant.parse("2026-01-01T00:00:00Z")

    @Test
    fun `devices telemetry latest and average`() {
        val b = InMemoryWearableBoard()
        b.add(WearableDevice("d2", WearableKind.FitnessBand, "Zephyr", "1.0", 80.0))
        b.add(WearableDevice("d1", WearableKind.Smartwatch, "Acme", "2.0", 55.0))
        assertEquals(listOf("Acme", "Zephyr"), b.devices.map { it.vendor }) // ASC by vendor

        b.record(WearableSample("d1", WearableTelemetryKind.HeartRate, 60.0, t0))
        b.record(WearableSample("d1", WearableTelemetryKind.HeartRate, 80.0, t0.plusSeconds(60)))
        assertEquals(80.0, b.latestValue("d1", WearableTelemetryKind.HeartRate)) // newest
        assertEquals(70.0, b.averageValue("d1", WearableTelemetryKind.HeartRate, t0), 1e-9)
        assertEquals(2, b.readSince("d1", WearableTelemetryKind.HeartRate, t0).size)
        assertTrue(b.averageValue("d1", WearableTelemetryKind.Steps, t0).isNaN()) // empty -> NaN

        assertFailsWith<IllegalStateException> {
            b.record(WearableSample("ghost", WearableTelemetryKind.Steps, 1.0, t0)) // unknown device
        }
    }

    @Test
    fun `adapter fixes interface and injects biometrics`() = runTest {
        val fake = FakeCompanionSession()
        val a = WearableCompanionAdapter(fake)
        assertEquals(InterfaceKind.Wearable, a.interfaceKind)

        // No context -> message passes through unchanged.
        a.sendAsync("plain")
        assertEquals("plain", fake.lastMessage)

        a.currentContext = WearableContext(
            heartRateBpm = 72.4,
            stepCountToday = 8000,
            spO2Percent = 98.6,
            skinTempCelsius = 33.0,
            isWorkoutActive = true,
            capturedAt = t0,
        )
        a.sendAsync("status?")
        val msg = fake.lastMessage!!
        assertTrue(msg.startsWith("status?"))
        assertTrue(msg.contains("[Biometrics]"))
        assertTrue(msg.contains("HR:72bpm"))
        assertTrue(msg.contains("Steps:8000"))
        assertTrue(msg.contains("SpO₂:99%"))
        assertTrue(msg.contains("Workout:active"))
        assertFalse(msg.endsWith(" ")) // trailing trim
    }

    @Test
    fun `adapter helper prompts`() = runTest {
        val fake = FakeCompanionSession()
        val a = WearableCompanionAdapter(fake)
        a.interpretReadingsAsync("HRV", "45,50,42", "48")
        assertTrue(fake.lastMessage!!.contains("Interpret wearable HRV from samples: 45,50,42 vs baseline: 48"))
    }
}
