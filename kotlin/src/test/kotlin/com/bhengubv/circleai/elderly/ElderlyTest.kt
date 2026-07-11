// ElderlyTest.kt
//
// Verifies the CircleAI.Elderly port against the C# reference:
//   - setPlan/get; addReminder/deactivate (throws on unknown); activeRemindersFor
//     filters resident + Active; recordCheckIn + latestCheckIn newest;
//     missedCheckIn true when none or latest predates since
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.elderly

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ElderlyTest {

    private val t0 = Instant.parse("2026-07-01T00:00:00Z")

    @Test
    fun `plans reminders and check-ins`() {
        val b = InMemoryElderlyCareBoard()
        b.setPlan(CarePlan("pl1", "Gogo", listOf("diabetes"), listOf("penicillin"), "gentle"))
        assertEquals("gentle", b.getPlan("Gogo")!!.carerNotes)
        assertNull(b.getPlan("Unknown"))

        b.addReminder(MedReminder("r1", "Gogo", "Metformin", Duration.ofHours(8), true))
        b.addReminder(MedReminder("r2", "Gogo", "Aspirin", Duration.ofHours(20), false)) // inactive
        b.addReminder(MedReminder("r3", "Mkhulu", "Statin", Duration.ofHours(21), true)) // other resident
        assertEquals(listOf("r1"), b.activeRemindersFor("Gogo").map { it.reminderId })

        b.deactivateReminder("r1")
        assertTrue(b.activeRemindersFor("Gogo").isEmpty())
        assertFailsWith<IllegalStateException> { b.deactivateReminder("ghost") }

        // No check-in yet -> missed.
        assertTrue(b.missedCheckIn("Gogo", t0))
        b.recordCheckIn(CheckIn("c1", "Gogo", t0, "OK", null))
        b.recordCheckIn(CheckIn("c2", "Gogo", t0.plusSeconds(3600), "OK", "later")) // newer
        assertEquals("c2", b.latestCheckIn("Gogo")!!.checkInId)
        assertFalse(b.missedCheckIn("Gogo", t0))                       // latest >= since
        assertTrue(b.missedCheckIn("Gogo", t0.plusSeconds(7200)))      // latest predates since
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(ElderlyDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Elderly]"))
        assertTrue("Older_Persons_Act_13_2006" in ElderlyDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = ElderlyCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Elderly]"))
        a.summariseCarerHandoverAsync("ate well, slept 8h")
        assertTrue(fake.lastMessage!!.contains("SBAR"))
    }
}
