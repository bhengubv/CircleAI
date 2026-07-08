// ProactiveBriefingServiceTest.kt
//
// Verifies ProactiveBriefingService against the C# reference: fireOnce assembles
// calendar/email/news/weather context, summarises via the LLM, and delivers to
// notifiers; no signals -> no delivery; a failing connector is skipped; and the
// next-fire schedule always lands more than 30s out.

package com.bhengubv.circleai.companion

import com.bhengubv.circleai.inference.GenerationOptions
import com.bhengubv.circleai.inference.IChatGenerator
import com.bhengubv.circleai.models.ChatMessage
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import java.time.LocalTime
import java.time.ZoneOffset
import java.time.ZonedDateTime
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class ProactiveBriefingServiceTest {

    private class CapturingNotifier : IBriefingNotifier {
        var headline: String? = null
        var body: String? = null
        var address: String? = null
        var calls = 0
        override suspend fun deliverAsync(headline: String, body: String, address: String?) {
            this.headline = headline
            this.body = body
            this.address = address
            calls++
        }
    }

    private class EchoGenerator : IChatGenerator {
        var lastPrompt: String? = null
        override suspend fun generateAsync(messages: List<ChatMessage>, opts: GenerationOptions): String {
            lastPrompt = messages.last().content
            return "SUMMARY"
        }
        override fun streamAsync(messages: List<ChatMessage>, opts: GenerationOptions): Flow<String> = emptyFlow()
        override fun close() {}
    }

    private fun calendar(vararg events: BriefingCalendarEvent) = object : ICalendarConnector {
        override val providerId = "cal"
        override val isConfigured = true
        override suspend fun listEventsAsync(fromUtc: Instant, toUtc: Instant) = events.toList()
    }

    @Test
    fun `fireOnce assembles context, summarises, and delivers`() = runTest {
        val notifier = CapturingNotifier()
        val gen = EchoGenerator()
        val cal = calendar(
            BriefingCalendarEvent(Instant.now().plus(Duration.ofHours(2)), "Dentist", "Clinic"),
        )
        val svc = ProactiveBriefingService(
            opts = ProactiveBriefingOptions(headline = "Morning", deliveryAddress = "+2799"),
            calendars = listOf(cal),
            notifiers = listOf(notifier),
            ai = gen,
        )
        svc.fireOnceAsync()

        assertEquals(1, notifier.calls)
        assertEquals("Morning", notifier.headline)
        assertEquals("SUMMARY", notifier.body)
        assertEquals("+2799", notifier.address)
        assertTrue(gen.lastPrompt!!.contains("Dentist"))
        assertTrue(gen.lastPrompt!!.contains("### Calendar (cal)"))
    }

    @Test
    fun `fireOnce with no signals delivers nothing`() = runTest {
        val notifier = CapturingNotifier()
        val svc = ProactiveBriefingService(
            opts = ProactiveBriefingOptions(),
            notifiers = listOf(notifier),
            ai = EchoGenerator(),
        )
        svc.fireOnceAsync()
        assertEquals(0, notifier.calls)
    }

    @Test
    fun `a failing connector is skipped, others still deliver`() = runTest {
        val notifier = CapturingNotifier()
        val throwing = object : IEmailConnector {
            override val providerId = "mail"
            override val isConfigured = true
            override suspend fun listUnreadAsync(max: Int): List<BriefingEmail> = throw RuntimeException("imap down")
        }
        val news = object : INewsSource {
            override val sourceId = "hn"
            override val isConfigured = true
            override suspend fun fetchLatestAsync(max: Int) = listOf(BriefingNewsItem("Kotlin 2.1 released"))
        }
        val svc = ProactiveBriefingService(
            opts = ProactiveBriefingOptions(),
            emails = listOf(throwing),
            news = listOf(news),
            notifiers = listOf(notifier),
            ai = null, // no AI -> raw context delivered
        )
        svc.fireOnceAsync()
        assertEquals(1, notifier.calls)
        assertTrue(notifier.body!!.contains("Kotlin 2.1 released"))
        assertTrue(notifier.body!!.contains("### News (hn)"))
    }

    @Test
    fun `weather is included when a provider and coordinates are configured`() = runTest {
        val notifier = CapturingNotifier()
        val weather = object : IWeatherProvider {
            override val providerId = "owm"
            override suspend fun currentAsync(lat: Double, lon: Double) =
                BriefingWeather(tempC = 21.4, condition = "Clear", feelsLikeC = 20.0, windKph = 12.0)
        }
        val svc = ProactiveBriefingService(
            opts = ProactiveBriefingOptions(latitude = -26.2, longitude = 28.0),
            weather = weather,
            notifiers = listOf(notifier),
            ai = null,
        )
        svc.fireOnceAsync()
        assertEquals(1, notifier.calls)
        assertTrue(notifier.body!!.contains("21°C Clear"))
        assertTrue(notifier.body!!.contains("### Weather (owm)"))
    }

    @Test
    fun `timeUntilNextFire always lands more than 30 seconds out`() {
        val svc = ProactiveBriefingService(
            opts = ProactiveBriefingOptions(fireTimesUtc = listOf(LocalTime.of(6, 30), LocalTime.of(18, 0))),
        )
        // now = 06:29:00 UTC -> next fire 06:30 is ~60s away (> 30s).
        val now = ZonedDateTime.of(2026, 7, 8, 6, 29, 0, 0, ZoneOffset.UTC).toInstant()
        val gap = svc.timeUntilNextFire(now)
        assertTrue(gap > Duration.ofSeconds(30), "gap was $gap")

        // now = exactly 06:30:00 -> must roll forward (candidate <= now+30s), next is 18:00.
        val atFire = ZonedDateTime.of(2026, 7, 8, 6, 30, 0, 0, ZoneOffset.UTC).toInstant()
        val gap2 = svc.timeUntilNextFire(atFire)
        assertEquals(Duration.ofHours(11).plus(Duration.ofMinutes(30)), gap2)
    }
}
