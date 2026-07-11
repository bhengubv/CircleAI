// RetailTest.kt
//
// Verifies the CircleAI.Retail port against the C# reference:
//   - addProduct/get; setStock/stock (0 default); recordSale throws on unknown
//     SKU and decrements stock
//   - revenueToday sums same-UTC-date sales; topSellersSince groups + orders + caps
//   - domain-context constants; adapter enrichment + currency-formatted helper

package com.bhengubv.circleai.retail

import com.bhengubv.circleai.companion.support.FakeCompanionSession
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class RetailTest {

    private fun assertMoney(expected: String, actual: BigDecimal) =
        assertTrue(BigDecimal(expected).compareTo(actual) == 0, "expected $expected but was $actual")

    private val now = Instant.parse("2026-07-15T12:00:00Z")

    @Test
    fun `stock and sales mutate correctly`() {
        val b = InMemoryRetailBoard()
        b.addProduct(Product("sku1", "Widget", BigDecimal("20"), "ZAR", "tools"))
        assertEquals("Widget", b.getProduct("sku1")!!.name)
        assertEquals(0, b.stock("sku1")) // default
        b.setStock(StockLevel("sku1", 10))
        assertEquals(10, b.stock("sku1"))

        b.recordSale(Sale("s1", "sku1", 3, BigDecimal("20"), now))
        assertEquals(7, b.stock("sku1")) // decremented
        assertFailsWith<IllegalStateException> { b.recordSale(Sale("s2", "ghost", 1, BigDecimal("1"), now)) }
    }

    @Test
    fun `revenue today and top sellers`() {
        val b = InMemoryRetailBoard()
        b.addProduct(Product("a", "A", BigDecimal("10"), "ZAR", null))
        b.addProduct(Product("bp", "B", BigDecimal("5"), "ZAR", null))
        b.setStock(StockLevel("a", 100))
        b.setStock(StockLevel("bp", 100))

        b.recordSale(Sale("s1", "a", 2, BigDecimal("10"), now))            // 20 today
        b.recordSale(Sale("s2", "bp", 5, BigDecimal("5"), now.plusSeconds(3600))) // 25 today (same date)
        b.recordSale(Sale("s3", "a", 1, BigDecimal("10"), now.minusSeconds(86400 * 2))) // different day

        assertMoney("45", b.revenueToday(now)) // 20 + 25, excludes the 2-days-ago sale

        // top sellers since 3 days ago: B sold 5, A sold 3 (2 today + 1 two-days-ago)
        val top = b.topSellersSince(now.minusSeconds(86400 * 3))
        assertEquals(listOf(TopSeller("bp", 5), TopSeller("a", 3)), top)
        assertEquals(1, b.topSellersSince(now.minusSeconds(86400 * 3), topK = 1).size)
        assertFailsWith<IllegalArgumentException> { b.topSellersSince(now, topK = 0) }
    }

    @Test
    fun `domain context and adapter`() = runTest {
        assertTrue(RetailDomainContext.SYSTEM_PROMPT_SNIPPET.startsWith("[DOMAIN: Retail]"))
        assertTrue("POPIA" in RetailDomainContext.complianceFlags)

        val fake = FakeCompanionSession()
        val a = RetailCompanionAdapter(fake)
        a.sendAsync("hi")
        assertTrue(fake.lastMessage!!.startsWith("[DOMAIN: Retail]"))
        a.analyseStockHealthAsync("sku1", 10, 3)
        assertTrue(fake.lastMessage!!.contains("stock health for SKU sku1"))
        a.designPromotionAsync("clearance", "winter", BigDecimal("5000"))
        assertTrue(fake.lastMessage!!.contains("clearance promotion for winter"))
    }
}
