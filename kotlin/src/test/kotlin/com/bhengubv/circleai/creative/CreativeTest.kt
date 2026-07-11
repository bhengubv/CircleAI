// CreativeTest.kt — verifies the CircleAI.Creative port against the C# reference.

package com.bhengubv.circleai.creative

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class CreativeTest {

    private val t0 = Instant.parse("2026-01-01T00:00:00Z")

    @Test
    fun `works inspiration and critique average`() {
        val b = InMemoryCreativeBoard()
        b.addWork(CreativeWork("w1", "Poem", "text", "Amy", t0, listOf("poetry", "draft")))
        assertEquals("Poem", b.getWork("w1")!!.title)
        assertEquals(listOf("w1"), b.worksByTag("POETRY").map { it.workId }) // case-insensitive

        b.recordInspiration(Inspiration("i1", "sky", "http://a", t0))
        b.recordInspiration(Inspiration("i2", "sea", "http://b", t0.plusSeconds(60)))
        assertEquals(listOf("i2", "i1"), b.recentInspiration().map { it.inspirationId }) // newest-first

        assertEquals(0.0, b.avgScore("w1"), 1e-9) // none yet
        b.addCritique(Critique("c1", "w1", "R1", "good", 8))
        b.addCritique(Critique("c2", "w1", "R2", "great", 10))
        assertEquals(9.0, b.avgScore("w1"), 1e-9)
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(CreativeDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Creative]"))
        assertTrue("Copyright_Act_98_1978" in CreativeDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = CreativeCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Creative]"))
        a.suggestStyleReferencesAsync("noir", "film")
        assertTrue(fake.lastMessage!!.contains("Suggest 5 style references for noir in film"))
    }
}
