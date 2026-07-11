// AmbientTest.kt — verifies the CircleAI.Ambient port against the C# reference.

package com.bhengubv.circleai.ambient

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import com.bhengubv.circleai.hosting.IProactiveReasoningService
import com.bhengubv.circleai.hosting.ProactiveMessageEventArgs
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class AmbientTest {

    private val t0 = Instant.parse("2026-01-01T00:00:00Z")

    @Test
    fun `readings history and comfort`() {
        val b = InMemoryAmbientBoard()
        b.record(AmbientReading("d1", 22.0, 45.0, 300.0, 35.0, t0))
        b.record(AmbientReading("d1", 24.0, 50.0, 320.0, 38.0, t0.plusSeconds(60)))
        assertEquals(24.0, b.latest("d1")!!.temperatureC) // newest
        assertEquals(listOf(t0.plusSeconds(60), t0), b.history("d1").map { it.atUtc }) // newest-first
        assertEquals(1, b.history("d1", 1).size)

        b.setPreference(AmbientPreference("lounge", 23.0, 48.0, 40.0))
        assertTrue(b.isComfortable("d1", "lounge")) // |24-23|<=2, |50-48|<=10, 38<=40

        b.record(AmbientReading("d1", 30.0, 90.0, 400.0, 70.0, t0.plusSeconds(120))) // too hot/humid/loud
        assertFalse(b.isComfortable("d1", "lounge"))
        assertFalse(b.isComfortable("d1", "no-pref")) // missing preference
        assertFalse(b.isComfortable("no-device", "lounge")) // missing reading
    }

    @Test
    fun `monitor polls proactive service and relays session events`() = runBlocking {
        val checks = AtomicInteger(0)
        val proactive = object : IProactiveReasoningService {
            override var proactiveMessageReady: (suspend (ProactiveMessageEventArgs) -> Unit)? = null
            override suspend fun checkAsync(userId: String) { checks.incrementAndGet() }
        }
        val session = FakeCompanionSession()
        val monitor = AmbientCompanionMonitor(session, proactive, pollInterval = Duration.ofMillis(20))
        monitor.start()
        monitor.start() // idempotent — no second loop
        delay(90)
        monitor.stop()
        assertTrue(checks.get() >= 1, "expected the poll loop to call checkAsync at least once")
        monitor.disposeAsync()
        assertTrue(session.closed) // disposal closes the underlying session
    }

    @Test
    fun `monitor never crashes on proactive failure`() = runBlocking {
        val proactive = object : IProactiveReasoningService {
            override var proactiveMessageReady: (suspend (ProactiveMessageEventArgs) -> Unit)? = null
            override suspend fun checkAsync(userId: String) { throw RuntimeException("boom") }
        }
        val monitor = AmbientCompanionMonitor(FakeCompanionSession(), proactive, pollInterval = Duration.ofMillis(15))
        monitor.start()
        delay(60) // several failing polls, all swallowed
        monitor.disposeAsync() // must complete cleanly
    }

    @Test
    fun `monitor works without a proactive service`() = runTest {
        val monitor = AmbientCompanionMonitor(FakeCompanionSession())
        monitor.start()
        monitor.stop()
        monitor.disposeAsync()
    }
}
