// IotTest.kt
//
// Verifies the CircleAI.IoT port against the C# reference:
//   - board: register/get; Devices ordered by name; latestValue newest / NaN;
//     history newest-first + limit guard; commandsFor newest-first
//   - pipeline: constructs over the Null voice stack + a fake session, accepts an
//     AudioReady listener, starts/stops without throwing, and closes cleanly
//     (closing the underlying session).

package com.bhengubv.circleai.iot

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import com.bhengubv.circleai.voice.NullTtsEngine
import com.bhengubv.circleai.voice.NullVoiceTranscriber
import com.bhengubv.circleai.voice.NullWakeWordDetector
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class IotTest {

    private val t0 = Instant.parse("2026-07-01T00:00:00Z")

    @Test
    fun `devices telemetry and commands`() {
        val b = InMemoryIoTBoard()
        b.register(IoTDevice("d2", "Thermostat", "hvac", "1.0", t0))
        b.register(IoTDevice("d1", "Camera", "cam", "2.0", t0))
        assertEquals(listOf("Camera", "Thermostat"), b.devices.map { it.name }) // ordered
        assertEquals("Thermostat", b.getDevice("d2")!!.name)

        assertTrue(b.latestValue("d2", "temp").isNaN()) // none
        b.recordTelemetry(IoTTelemetry("d2", "temp", 20.0, t0))
        b.recordTelemetry(IoTTelemetry("d2", "temp", 22.0, t0.plusSeconds(60))) // newer
        assertEquals(22.0, b.latestValue("d2", "temp"))
        assertEquals(listOf(22.0, 20.0), b.history("d2", "temp").map { it.value }) // newest-first
        assertEquals(1, b.history("d2", "temp", limit = 1).size)
        assertFailsWith<IllegalArgumentException> { b.history("d2", "temp", limit = 0) }

        b.sendCommand(IoTCommand("cm1", "d2", "setTemp", "{\"t\":21}", t0))
        b.sendCommand(IoTCommand("cm2", "d2", "reboot", "{}", t0.plusSeconds(60))) // newer
        assertEquals(listOf("cm2", "cm1"), b.commandsFor("d2").map { it.commandId }) // newest-first
    }

    @Test
    fun `pipeline lifecycle wires session and closes it`() = runTest {
        val session = FakeCompanionSession()
        val pipeline = IoTCompanionPipeline(
            session = session,
            wakeWord = NullWakeWordDetector(),
            transcriber = NullVoiceTranscriber(),
            audioCapture = null,
            tts = NullTtsEngine(),
        )
        var audioReadyRegistered = false
        pipeline.onAudioReady { audioReadyRegistered = true }
        // Start/stop the wake listener — Null detector makes this a no-op that must not throw.
        pipeline.startAsync()
        pipeline.stopAsync()
        // Closing the pipeline closes the underlying session.
        pipeline.close()
        assertTrue(session.closed)
        // The listener was accepted (never fired here since no wake occurs).
        assertTrue(!audioReadyRegistered)
    }
}
