// RealEstateTest.kt
//
// Verifies the CircleAI.RealEstate port against the C# reference:
//   - registerProperty/list/close; close throws on unknown; activeInSuburb
//     filters active + suburb (case-insensitive), ordered by ListedUtc DESC
//   - suburbAverage = mean asking price, null when none
//   - domain-context constants; adapter enrichment + currency-formatted helper

package com.bhengubv.circleai.realestate

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class RealEstateTest {

    private val t0 = Instant.parse("2026-07-01T00:00:00Z")

    @Test
    fun `active listings by suburb and average`() {
        val b = InMemoryRealEstateBoard()
        b.registerProperty(Property("p1", "Sandton", PropertyKind.Apartment, 2, 2, 90.0))
        b.registerProperty(Property("p2", "Sandton", PropertyKind.House, 4, 3, 250.0))
        b.registerProperty(Property("p3", "Rosebank", PropertyKind.Townhouse, 3, 2, 150.0))

        b.list(Listing("l1", "p1", BigDecimal("1000000"), "ZAR", t0, true))
        b.list(Listing("l2", "p2", BigDecimal("3000000"), "ZAR", t0.plusSeconds(3600), true)) // newer
        b.list(Listing("l3", "p3", BigDecimal("2000000"), "ZAR", t0, true))

        // ordered by ListedUtc DESC: l2 (newer) before l1
        assertEquals(listOf("l2", "l1"), b.activeInSuburb("sandton").map { it.listingId })
        assertTrue(BigDecimal("2000000").compareTo(b.suburbAverage("SANDTON")!!) == 0) // (1M+3M)/2

        b.close("l2")
        assertEquals(listOf("l1"), b.activeInSuburb("Sandton").map { it.listingId })
        assertNull(b.suburbAverage("Nowhere"))
        assertFailsWith<IllegalStateException> { b.close("ghost") }
        assertFailsWith<IllegalArgumentException> { b.activeInSuburb(" ") }
    }

    @Test
    fun `valuations and viewings do not throw`() {
        val b = InMemoryRealEstateBoard()
        b.value(Valuation("p1", BigDecimal("999"), "AVM", t0))
        b.scheduleViewing(Viewing("vw1", "l1", "Alice", t0))
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(RealEstateDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: RealEstate]"))
        assertTrue("FICA" in RealEstateDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = RealEstateCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: RealEstate]"))
        a.draftLeaseAsync("LL", "TT", "1 Main St", BigDecimal("12000"), 12)
        assertTrue(fake.lastMessage!!.contains("residential lease agreement"))
        assertTrue(fake.lastMessage!!.contains("/month"))
    }
}
