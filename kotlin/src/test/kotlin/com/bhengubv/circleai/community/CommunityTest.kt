// CommunityTest.kt — verifies the CircleAI.Community port against the C# reference.

package com.bhengubv.circleai.community

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class CommunityTest {

    @Test
    fun `groups announcements and opportunities`() {
        val b = InMemoryCommunityBoard()
        b.create(CommunityGroup("g1", "Garden Club", "greening", listOf("m1", "m2")))
        b.create(CommunityGroup("g2", "Book Club", "reading", listOf("m3")))
        assertEquals(listOf("g1"), b.groupsForMember("m1").map { it.groupId })

        val t0 = Instant.parse("2026-01-01T00:00:00Z")
        b.post(Announcement("a1", "g1", "Meet", "Sat 9am", t0))
        b.post(Announcement("a2", "g1", "Update", "note", t0.plusSeconds(60)))
        assertEquals(listOf("a2", "a1"), b.announcementsFor("g1").map { it.announcementId }) // newest-first
        assertEquals(listOf("a2"), b.announcementsFor("g1", 1).map { it.announcementId })

        val future = Instant.now().plusSeconds(86_400)
        b.list(VolunteerOpportunity("o1", "g1", "plant trees", 10, future))
        b.list(VolunteerOpportunity("o2", "g1", "old", 5, Instant.now().minusSeconds(3600)))
        assertEquals(listOf("o1"), b.opportunities().map { it.oppId }) // future ASC
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(CommunityDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Community]"))
        assertTrue("NPO_Act" in CommunityDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = CommunityCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Community]"))
        a.designVolunteerCampaignAsync("cleanup", 20, "Saturday")
        assertTrue(fake.lastMessage!!.contains("Design a volunteer drive: need cleanup, 20 people, Saturday"))
    }
}
