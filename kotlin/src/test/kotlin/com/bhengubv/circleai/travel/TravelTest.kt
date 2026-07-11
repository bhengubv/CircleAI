// TravelTest.kt — verifies the CircleAI.Travel port against the C# reference.

package com.bhengubv.circleai.travel

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class TravelTest {

    @Test
    fun `trip cost sums flights and stays and upcoming filters`() {
        val b = InMemoryTravelBoard()
        b.add(Flight("f1", "JNB", "CPT", Instant.parse("2026-06-01T06:00:00Z"), Instant.parse("2026-06-01T08:00:00Z"), "FA", "Y", BigDecimal("1500"), "ZAR"))
        b.add(HotelStay("s1", "Hotel X", "CPT", Instant.parse("2026-06-01T00:00:00Z"), Instant.parse("2026-06-04T00:00:00Z"), BigDecimal("1000"), "ZAR"))
        b.plan(TravelTrip("t1", "CPT trip", Instant.parse("2026-06-01T00:00:00Z"), Instant.parse("2026-06-04T00:00:00Z"), listOf("f1"), listOf("s1")))

        // flight 1500 + 3 nights * 1000 = 4500
        assertEquals(0, BigDecimal("4500").compareTo(b.tripCost("t1")))
        assertFailsWith<IllegalStateException> { b.tripCost("nope") }

        b.plan(TravelTrip("tPast", "old", Instant.parse("2020-01-01T00:00:00Z"), Instant.parse("2020-01-05T00:00:00Z"), emptyList(), emptyList()))
        val now = Instant.parse("2026-01-01T00:00:00Z")
        assertEquals(listOf("t1"), b.upcomingTrips(now).map { it.tripId }) // future only, ASC
    }

    @Test
    fun `single-night minimum applies`() {
        val b = InMemoryTravelBoard()
        // check-in == check-out -> 0 days -> max(1, 0) = 1 night.
        val d = Instant.parse("2026-06-01T00:00:00Z")
        b.add(HotelStay("s1", "H", "C", d, d, BigDecimal("700"), "ZAR"))
        b.plan(TravelTrip("t1", "x", d, d, emptyList(), listOf("s1")))
        assertEquals(0, BigDecimal("700").compareTo(b.tripCost("t1")))
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(TravelDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Travel]"))
        assertTrue("Consumer_Protection_Act" in TravelDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = TravelCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Travel]"))
        a.handleVisaQueryAsync("ZA", "UK", "tourism")
        assertTrue(fake.lastMessage!!.contains("Outline visa requirements: ZA → UK for tourism"))
    }
}
