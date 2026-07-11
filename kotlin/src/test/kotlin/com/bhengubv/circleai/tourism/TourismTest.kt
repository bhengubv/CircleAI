// TourismTest.kt — verifies the CircleAI.Tourism port against the C# reference.

package com.bhengubv.circleai.tourism

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class TourismTest {

    @Test
    fun `attractions itineraries and bookings`() {
        val b = InMemoryTourismBoard()
        b.add(Attraction("a2", "Table Mountain", "Cape Town", "ZA", -33.9, 18.4, listOf("nature", "hike")))
        b.add(Attraction("a1", "Aquarium", "Cape Town", "ZA", -33.9, 18.4, listOf("family")))
        b.add(Attraction("a3", "Union Bldgs", "Pretoria", "ZA", -25.7, 28.2, listOf("history")))
        assertEquals(listOf("Aquarium", "Table Mountain"), b.attractionsInCity("cape town").map { it.name }) // ASC
        assertEquals(listOf("a2"), b.byTag("HIKE").map { it.attractionId }) // case-insensitive
        assertFailsWith<IllegalArgumentException> { b.attractionsInCity(" ") }
        assertFailsWith<IllegalArgumentException> { b.byTag("") }

        b.plan(Itinerary("it1", "CT 3 days", listOf(ItineraryItem(0, Duration.ofHours(9), Duration.ofHours(12), "a1", "morning"))))
        assertEquals("CT 3 days", b.getItinerary("it1")!!.title)

        b.book(TourismBooking("bk1", "it1", Instant.parse("2026-05-01T00:00:00Z"), 2, BigDecimal("5000"), "ZAR"))
        assertEquals(listOf("bk1"), b.bookings.map { it.bookingId })
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(TourismDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Tourism]"))
        assertTrue("SATSA" in TourismDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = TourismCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Tourism]"))
        a.estimateBudgetAsync("Cape Town", 2, 5, "mid-range")
        assertTrue(fake.lastMessage!!.contains("Estimate budget for 2 pax, 5 days in Cape Town, mid-range standard"))
    }
}
