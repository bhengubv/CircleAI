// CommerceTest.kt
//
// Verifies the CircleAI.Commerce port against the C# reference semantics:
//   - addCustomer/getCustomer; place + updateStatus (unknown order throws)
//   - addLine/linesFor filter by order; ordersFor orders by atUtc DESC
//   - lifetimeValue sums order totals
//   - domain-context constants; adapter enrichment + a domain helper

package com.bhengubv.circleai.commerce

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class CommerceTest {

    private fun money(s: String) = BigDecimal(s)
    private fun assertMoney(expected: String, actual: BigDecimal) =
        assertTrue(BigDecimal(expected).compareTo(actual) == 0, "expected $expected but was $actual")

    private fun order(id: String, cust: String, total: String, at: Instant, status: String = "new") =
        CommerceOrder(id, cust, money(total), "ZAR", status, at)

    @Test
    fun `customers orders ordering and lifetime value`() {
        val b = InMemoryCommerceBoard()
        b.addCustomer(CommerceCustomer("c1", "Ada", "ada@x.io", Instant.EPOCH))
        assertEquals("Ada", b.getCustomer("c1")!!.name)
        assertNull(b.getCustomer("x"))

        b.place(order("o1", "c1", "100", Instant.parse("2026-07-01T00:00:00Z")))
        b.place(order("o2", "c1", "50", Instant.parse("2026-07-03T00:00:00Z")))
        b.place(order("o3", "c2", "999", Instant.parse("2026-07-02T00:00:00Z")))
        assertEquals(listOf("o2", "o1"), b.ordersFor("c1").map { it.orderId })
        assertMoney("150", b.lifetimeValue("c1"))
        assertMoney("0", b.lifetimeValue("nobody"))
    }

    @Test
    fun `status update and line items`() {
        val b = InMemoryCommerceBoard()
        b.place(order("o1", "c1", "10", Instant.EPOCH))
        b.updateStatus("o1", "shipped")
        assertEquals("shipped", b.ordersFor("c1").single().status)
        assertFailsWith<IllegalStateException> { b.updateStatus("ghost", "x") }

        b.addLine(CommerceLineItem("li1", "o1", "SKU1", 2, money("5")))
        b.addLine(CommerceLineItem("li2", "o1", "SKU2", 1, money("3")))
        b.addLine(CommerceLineItem("li3", "o2", "SKU3", 1, money("9")))
        assertEquals(setOf("li1", "li2"), b.linesFor("o1").map { it.lineId }.toSet())
        assertEquals(1, b.linesFor("o2").size)
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(CommerceDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Commerce]"))
        assertTrue("Consumer_Protection_Act" in CommerceDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = CommerceCompanionAdapter(fake)
        a.sendAsync("hey")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Commerce]"))
        a.analysePricingAsync("Widget", money("199.99"))
        assertTrue(fake.lastMessage!!.contains("Analyse pricing for: Widget at"))
        assertTrue(fake.lastMessage!!.contains("199.99"))
    }
}
