// HospitalityTest.kt — verifies the CircleAI.Hospitality port against the C# reference.

package com.bhengubv.circleai.hospitality

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class HospitalityTest {

    @Test
    fun `availability checkout and notes`() {
        val b = InMemoryHospitalityBoard()
        b.addRoom(HotelRoom("r1", "Deluxe", BigDecimal("1200"), "ZAR", true))
        b.addRoom(HotelRoom("r2", "Standard", BigDecimal("800"), "ZAR", true))
        b.addRoom(HotelRoom("r3", "Suite", BigDecimal("2500"), "ZAR", false)) // not clean

        val checkIn = Instant.parse("2026-04-01T14:00:00Z")
        val checkOut = Instant.parse("2026-04-05T10:00:00Z")
        b.reserve(GuestReservation("res1", "Amy", "r1", checkIn, checkOut))

        val onDate = Instant.parse("2026-04-02T12:00:00Z")
        // r1 booked, r3 unclean -> only r2 available.
        assertEquals(listOf("r2"), b.availableOn(onDate).map { it.roomId })

        b.checkOut("res1", roomNeedsCleaning = true)
        assertFalse(b.getRoom("r1")!!.isClean)
        assertFailsWith<IllegalStateException> { b.checkOut("nope", false) }

        b.addNote(FrontDeskNote("n1", "res1", "left umbrella", Instant.parse("2026-04-01T15:00:00Z")))
        b.addNote(FrontDeskNote("n2", "res1", "late checkout", Instant.parse("2026-04-05T09:00:00Z")))
        assertEquals(listOf("n2", "n1"), b.notesFor("res1").map { it.noteId }) // newest-first
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(HospitalityDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Hospitality]"))
        assertTrue("Liquor_Act" in HospitalityDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = HospitalityCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Hospitality]"))
        a.handleComplaintAsync("cold food", "angry")
        assertTrue(fake.lastMessage!!.contains("Handle this guest complaint (angry): cold food"))
    }
}
