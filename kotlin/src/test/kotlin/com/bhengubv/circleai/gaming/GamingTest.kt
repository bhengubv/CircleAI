// GamingTest.kt — verifies the CircleAI.Gaming port against the C# reference.

package com.bhengubv.circleai.gaming

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class GamingTest {

    private val t0 = Instant.parse("2026-02-01T00:00:00Z")

    @Test
    fun `titles play time achievements and most played`() {
        val b = InMemoryGamingBoard()
        b.addTitle(GameTitle("g1", "Celeste", "Platformer", "PC"))
        b.addTitle(GameTitle("g2", "Hades", "Roguelike", "PC"))
        assertEquals(listOf("g2"), b.titlesByGenre("roguelike").map { it.titleId }) // case-insensitive

        b.recordSession(PlaySession("s1", "u1", "g1", Duration.ofMinutes(30), t0))
        b.recordSession(PlaySession("s2", "u1", "g1", Duration.ofMinutes(20), t0.plusSeconds(60)))
        b.recordSession(PlaySession("s3", "u1", "g2", Duration.ofMinutes(120), t0))
        assertEquals(Duration.ofMinutes(50), b.totalPlayTime("u1", "g1"))
        assertEquals(listOf("g2", "g1"), b.mostPlayed("u1", 5).map { it.titleId }) // by total time DESC
        assertFailsWith<IllegalArgumentException> { b.mostPlayed("u1", 0) }

        b.unlock(AchievementUnlock("x1", "u1", "g1", "No Deaths", t0))
        b.unlock(AchievementUnlock("x2", "u1", "g2", "Cleared", t0.plusSeconds(300)))
        assertEquals(listOf("x2", "x1"), b.achievementsFor("u1").map { it.unlockId }) // newest-first
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(GamingDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Gaming]"))
        assertTrue("WASPA" in GamingDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = GamingCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Gaming]"))
        a.analysePlayerRetentionAsync("40%", "18%", "8%")
        assertTrue(fake.lastMessage!!.contains("Analyse retention: D1=40%, D7=18%, D30=8%"))
    }
}
