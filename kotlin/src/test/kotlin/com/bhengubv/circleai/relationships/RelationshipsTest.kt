// RelationshipsTest.kt — verifies the CircleAI.Relationships port against the C# reference.

package com.bhengubv.circleai.relationships

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class RelationshipsTest {

    @Test
    fun `contacts touchpoints and not-contacted-since`() {
        val b = InMemoryRelationshipsBoard()
        b.addContact(PersonContact("c2", "Zara", "friend", null))
        b.addContact(PersonContact("c1", "Ada", "sister", "notes"))
        assertEquals(listOf("Ada", "Zara"), b.contacts.map { it.name }) // ASC by name

        assertNull(b.lastContact("c1"))
        b.recordTouchpoint(ContactEvent("c1", "call", Instant.parse("2026-01-01T00:00:00Z"), null))
        b.recordTouchpoint(ContactEvent("c1", "text", Instant.parse("2026-02-01T00:00:00Z"), null))
        assertEquals(Instant.parse("2026-02-01T00:00:00Z"), b.lastContact("c1")) // newest

        val cutoff = Instant.parse("2026-03-01T00:00:00Z")
        // c1 last-contacted 2026-02-01 (< cutoff), c2 never -> both returned.
        assertEquals(setOf("c1", "c2"), b.notContactedSince(cutoff).map { it.contactId }.toSet())
    }

    @Test
    fun `upcoming this month uses current month`() {
        val b = InMemoryRelationshipsBoard()
        val now = Instant.now().atZone(java.time.ZoneOffset.UTC)
        val thisMonth = now.withDayOfMonth(15).toInstant()
        val otherMonth = now.plusMonths(1).withDayOfMonth(10).toInstant()
        b.addImportantDate(ImportantDate("d1", "c1", "birthday", thisMonth))
        b.addImportantDate(ImportantDate("d2", "c2", "anniv", otherMonth))
        assertEquals(listOf("d1"), b.upcomingThisMonth().map { it.dateId })
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(RelationshipsDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Relationships]"))
        assertTrue("Not_Therapy" in RelationshipsDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = RelationshipsCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Relationships]"))
        a.resolveTensionAsync("we argued", "repair")
        assertTrue(fake.lastMessage!!.contains("Help resolve tension: we argued. Desired outcome: repair"))
    }
}
