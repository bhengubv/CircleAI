// FaithTest.kt — verifies the CircleAI.Faith port against the C# reference.

package com.bhengubv.circleai.faith

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class FaithTest {

    @Test
    fun `services prayers and scripture`() {
        val b = InMemoryFaithBoard()
        val start = Instant.parse("2026-04-01T00:00:00Z")
        val end = Instant.parse("2026-04-30T00:00:00Z")
        b.schedule(FaithService("s2", "Comm", "Evening", Instant.parse("2026-04-10T18:00:00Z"), "Hall"))
        b.schedule(FaithService("s1", "Comm", "Morning", Instant.parse("2026-04-05T09:00:00Z"), "Hall"))
        b.schedule(FaithService("sOut", "Comm", "May", Instant.parse("2026-05-05T09:00:00Z"), "Hall"))
        assertEquals(listOf("s1", "s2"), b.servicesBetween(start, end).map { it.serviceId }) // ASC, inclusive

        b.submitPrayer(PrayerRequest("p1", "Amy", "peace", Instant.parse("2026-01-01T00:00:00Z"), false))
        b.submitPrayer(PrayerRequest("p2", "Bea", "health", Instant.parse("2026-02-01T00:00:00Z"), true))
        assertEquals(listOf("p2", "p1"), b.recentPrayers().map { it.requestId }) // newest-first

        b.addScripture(ScriptureReference("r1", "Christian", "John", 3, 16, "For God so loved..."))
        assertEquals("r1", b.lookup("Christian", "John", 3, 16)!!.referenceId)
        assertNull(b.lookup("Christian", "John", 3, 17))
        assertEquals(listOf("r1"), b.byTradition("christian").map { it.referenceId }) // case-insensitive
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(FaithDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Faith]"))
        assertTrue("Non_Denominational_Respect" in FaithDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = FaithCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Faith]"))
        a.draftServiceOrderAsync("Christian", "Sunday", 60)
        assertTrue(fake.lastMessage!!.contains("Draft a 60-minute Sunday order of service in the Christian"))
    }
}
