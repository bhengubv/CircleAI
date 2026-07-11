// FamilyTest.kt
//
// Verifies the CircleAI.Family port against the C# reference:
//   - add/get; Members ordered by name; eventsForMember filters by MemberIds
//     membership, ordered by AtUtc; record + totalPaidBy / spendByCategory with
//     since filter (category case-insensitive)
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.family

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class FamilyTest {

    private fun assertMoney(expected: String, actual: BigDecimal) =
        assertTrue(BigDecimal(expected).compareTo(actual) == 0, "expected $expected but was $actual")

    private val t0 = Instant.parse("2026-07-01T00:00:00Z")

    @Test
    fun `members events and expenses`() {
        val b = InMemoryFamilyBoard()
        b.add(FamilyMember("m2", "Zoe", "child", t0))
        b.add(FamilyMember("m1", "Ann", "parent", t0))
        assertEquals(listOf("Ann", "Zoe"), b.members.map { it.name }) // ordered
        assertEquals("Zoe", b.getMember("m2")!!.name)

        b.schedule(FamilyEvent("e1", "Dinner", t0.plusSeconds(7200), listOf("m1", "m2")))
        b.schedule(FamilyEvent("e2", "Soccer", t0.plusSeconds(3600), listOf("m2"))) // earlier
        b.schedule(FamilyEvent("e3", "Work", t0, listOf("m1")))
        // events for m2 ordered by AtUtc ASC: e2 (earlier) before e1
        assertEquals(listOf("e2", "e1"), b.eventsForMember("m2").map { it.eventId })

        b.record(SharedExpense("x1", "m1", BigDecimal("300"), "ZAR", "Food", t0.plusSeconds(10)))
        b.record(SharedExpense("x2", "m1", BigDecimal("150"), "ZAR", "food", t0.plusSeconds(20)))
        b.record(SharedExpense("x3", "m2", BigDecimal("50"), "ZAR", "Toys", t0.minusSeconds(9999))) // before since
        val since = t0
        assertMoney("450", b.totalPaidBy("m1", since))      // 300 + 150
        assertMoney("450", b.spendByCategory("FOOD", since)) // case-insensitive, both food
        assertMoney("0", b.totalPaidBy("m2", since))         // m2's only expense predates since
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(FamilyDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Family]"))
        assertTrue("Childrens_Act_38_2005" in FamilyDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = FamilyCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Family]"))
        a.mediateSiblingDisputeAsync("6 and 9", "toy sharing")
        assertTrue(fake.lastMessage!!.contains("sibling dispute"))
    }
}
