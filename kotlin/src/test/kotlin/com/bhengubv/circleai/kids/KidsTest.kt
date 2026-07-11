// KidsTest.kt — verifies the CircleAI.Kids port against the C# reference.

package com.bhengubv.circleai.kids

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class KidsTest {

    private val now = Instant.parse("2026-03-04T15:00:00Z")

    @Test
    fun `content limits usage and over-limit`() {
        val b = InMemoryKidsBoard()
        b.addContent(KidsContent("c2", "Zebra Facts", AgeAppropriateness.EarlyPrimary, "article", listOf("animals")))
        b.addContent(KidsContent("c1", "Ants", AgeAppropriateness.EarlyPrimary, "video", listOf("bugs")))
        b.addContent(KidsContent("c3", "Teen Thing", AgeAppropriateness.Teen, "article", emptyList()))
        assertEquals(listOf("Ants", "Zebra Facts"), b.contentFor(AgeAppropriateness.EarlyPrimary).map { it.title }) // ASC by title

        b.setLimits(DailyTime("Kai", Duration.ofMinutes(60), Duration.ofMinutes(30)))
        b.recordTime(TimeLog("Kai", "screen", Duration.ofMinutes(40), now))
        b.recordTime(TimeLog("Kai", "screen", Duration.ofMinutes(30), now.plusSeconds(60)))
        b.recordTime(TimeLog("Kai", "screen", Duration.ofMinutes(999), now.minusSeconds(2 * 86_400))) // other day
        assertEquals(Duration.ofMinutes(70), b.usedToday("Kai", "screen", now))
        assertTrue(b.overLimit("Kai", "screen", now)) // 70 > 60
        assertFalse(b.overLimit("Kai", "reading", now)) // 0 <= 30
        assertFalse(b.overLimit("NoLimits", "screen", now)) // no limits set -> false
        assertFalse(b.overLimit("Kai", "other", now)) // unknown kind effectively uncapped
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(KidsDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Kids]"))
        assertTrue("COPPA_principles" in KidsDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = KidsCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Kids]"))
        a.explainHardConceptAsync("gravity", "EarlyPrimary")
        assertTrue(fake.lastMessage!!.contains("Explain 'gravity' to EarlyPrimary"))
    }
}
