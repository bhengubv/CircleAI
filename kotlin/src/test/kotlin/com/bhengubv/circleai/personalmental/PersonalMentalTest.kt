// PersonalMentalTest.kt
//
// Verifies the CircleAI.Personal.Mental port against the C# reference:
//   - logMood + last7Days (window from now, ASC); entries older than 7 days
//     excluded
//   - addEntry rejects blank id; entries ordered by atUtc DESC
//   - registerStrategy + strategiesByTag (blank rejected, case-insensitive)
//   - avgMood7Day: NaN when empty else mean of ordinals
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.personalmental

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import java.time.temporal.ChronoUnit
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class PersonalMentalTest {

    private fun ago(days: Long) = Instant.now().minus(days, ChronoUnit.DAYS)

    @Test
    fun `last7Days windows and avg mood`() {
        val b = InMemoryMentalHealthBoard()
        assertTrue(b.avgMood7Day().isNaN())

        b.logMood(MoodLog(Mood.Great, ago(1), null)) // ordinal 4
        b.logMood(MoodLog(Mood.Neutral, ago(2), null)) // ordinal 2
        b.logMood(MoodLog(Mood.Low, ago(30), null)) // outside window -> excluded

        val recent = b.last7Days()
        assertEquals(2, recent.size)
        // ordered ascending by time: the 2-days-ago entry precedes the 1-day-ago one
        assertEquals(listOf(Mood.Neutral, Mood.Great), recent.map { it.mood })
        assertEquals(3.0, b.avgMood7Day()) // (4 + 2) / 2
    }

    @Test
    fun `entries reject blank id and order newest first`() {
        val b = InMemoryMentalHealthBoard()
        assertFailsWith<IllegalArgumentException> {
            b.addEntry(JournalEntry("  ", "t", "body", Instant.EPOCH))
        }
        b.addEntry(JournalEntry("e1", "First", "b", Instant.parse("2026-07-01T00:00:00Z")))
        b.addEntry(JournalEntry("e2", "Second", "b", Instant.parse("2026-07-05T00:00:00Z")))
        assertEquals(listOf("e2", "e1"), b.entries.map { it.entryId })
    }

    @Test
    fun `strategies by tag case-insensitive and blank rejected`() {
        val b = InMemoryMentalHealthBoard()
        b.registerStrategy(CopingStrategy("s1", "Box breathing", "…", listOf("Anxiety", "Grounding")))
        b.registerStrategy(CopingStrategy("s2", "Gratitude", "…", listOf("Mood")))
        assertEquals(listOf("s1"), b.strategiesByTag("anxiety").map { it.strategyId })
        assertTrue(b.strategiesByTag("nope").isEmpty())
        assertFailsWith<IllegalArgumentException> { b.strategiesByTag(" ") }
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(PersonalMentalDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Personal.Mental]"))
        assertTrue("Crisis_Protocol" in PersonalMentalDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = PersonalMentalCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Personal.Mental]"))
        a.checkInAsync("anxious")
        assertTrue(fake.lastMessage!!.contains("I am feeling: anxious"))
    }
}
