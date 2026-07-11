// MarketsTest.kt
//
// Verifies the CircleAI.Markets port against the C# reference:
//   - instrument catalog: case-insensitive symbol keying, substring search,
//     ordered + topK
//   - market data feed: publish stores latest quote + pushes to subscribers;
//     dispose stops delivery; a throwing subscriber doesn't break publish
//   - order router: rejects non-positive quantity, missing/invalid limit price,
//     unknown symbol; accepts a valid order; ids sequence "ord-N"
//   - null implementations are fail-closed

package com.bhengubv.circleai.markets

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.math.BigDecimal
import java.time.Instant
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class MarketsTest {

    private fun q(sym: String) = Quote(sym, BigDecimal("10"), BigDecimal("11"), BigDecimal("10.5"), Instant.EPOCH)

    @Test
    fun `catalog is case-insensitive with substring search`() = runTest {
        val cat = InMemoryInstrumentCatalog()
        assertEquals("in-memory", cat.backendId)
        cat.add(Instrument("AAPL", "NASDAQ", "USD", "Equity"))
        cat.add(Instrument("ABNB", "NASDAQ", "USD", "Equity"))
        cat.add(Instrument("MSFT", "NASDAQ", "USD", "Equity"))

        assertEquals("NASDAQ", cat.getAsync("aapl")!!.exchange) // case-insensitive get
        val hits = cat.searchAsync("A") // AAPL, ABNB
        assertEquals(listOf("AAPL", "ABNB"), hits.map { it.symbol })
        assertEquals(1, cat.searchAsync("A", topK = 1).size)
        assertFailsWith<IllegalArgumentException> { cat.getAsync(" ") }
        assertFailsWith<IllegalArgumentException> { cat.searchAsync("A", topK = 0) }
    }

    @Test
    fun `feed publish stores latest and pushes to subscribers`() = runTest {
        val feed = InMemoryMarketDataFeed()
        val latch = CountDownLatch(1)
        val received = AtomicInteger(0)
        val sub = feed.subscribeQuotes("AAPL") { received.incrementAndGet(); latch.countDown() }

        feed.publish(q("AAPL"))
        assertTrue(latch.await(2, TimeUnit.SECONDS), "subscriber should have been invoked")
        assertEquals(BigDecimal("10.5"), feed.getQuoteAsync("aapl")!!.last) // case-insensitive get

        // After dispose, no further delivery.
        sub.close()
        feed.publish(q("AAPL"))
        // Give any (erroneous) dispatch a moment; count must stay at 1.
        Thread.sleep(100)
        assertEquals(1, received.get())
    }

    @Test
    fun `feed swallows a throwing subscriber`() = runTest {
        val feed = InMemoryMarketDataFeed()
        val goodLatch = CountDownLatch(1)
        feed.subscribeQuotes("X") { throw RuntimeException("boom") }
        feed.subscribeQuotes("X") { goodLatch.countDown() }
        // publish must not throw and the good subscriber still fires.
        feed.publish(q("X"))
        assertTrue(goodLatch.await(2, TimeUnit.SECONDS))
    }

    @Test
    fun `order router accept and reject rules`() = runTest {
        val cat = InMemoryInstrumentCatalog()
        cat.add(Instrument("AAPL", "NASDAQ", "USD", "Equity"))
        val router = InMemoryOrderRouter(cat)
        assertEquals("in-memory", router.backendId)

        val bad1 = router.submitAsync(OrderRequest("AAPL", OrderSide.Buy, OrderType.Market, BigDecimal.ZERO, null))
        assertFalse(bad1.accepted)
        assertEquals("Quantity must be positive", bad1.failureReason)

        val bad2 = router.submitAsync(OrderRequest("AAPL", OrderSide.Buy, OrderType.Limit, BigDecimal.ONE, null))
        assertEquals("Limit order requires positive LimitPrice", bad2.failureReason)

        val bad3 = router.submitAsync(OrderRequest("NOPE", OrderSide.Sell, OrderType.Market, BigDecimal.ONE, null))
        assertEquals("Unknown symbol", bad3.failureReason)

        val ok = router.submitAsync(OrderRequest("AAPL", OrderSide.Buy, OrderType.Limit, BigDecimal.TEN, BigDecimal("150")))
        assertTrue(ok.accepted)
        assertNull(ok.failureReason)
        assertTrue(ok.orderId.startsWith("ord-"))
    }

    @Test
    fun `null implementations are fail-closed`() = runTest {
        assertNull(NullMarketDataFeed.Instance.getQuoteAsync("AAPL"))
        NullMarketDataFeed.Instance.subscribeQuotes("AAPL") { }.close() // no throw
        assertNull(NullInstrumentCatalog.Instance.getAsync("AAPL"))
        assertTrue(NullInstrumentCatalog.Instance.searchAsync("A").isEmpty())
        val r = NullOrderRouter.Instance.submitAsync(OrderRequest("AAPL", OrderSide.Buy, OrderType.Market, BigDecimal.ONE, null))
        assertFalse(r.accepted)
        assertEquals("NullOrderRouter — fail-closed.", r.failureReason)
    }
}
