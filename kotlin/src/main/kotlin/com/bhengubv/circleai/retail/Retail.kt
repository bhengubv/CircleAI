// Retail.kt
//
// Kotlin port of CircleAI.Retail (RetailPrimitives.cs +
// RetailDomainContext.cs + RetailCompanionAdapter.cs) — the C# reference
// is the EXACT spec. A deterministic in-memory retail board: products,
// stock levels, sales, and a today-revenue + top-sellers rollup.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `decimal` -> `BigDecimal`;
//     `DateTimeOffset` -> `Instant`.
//   * The C# `IReadOnlyList<(string Sku, int Sold)>` named tuple is mapped to a
//     small [TopSeller] data class (Sku, Sold) so callers keep named access.
//   * `RecordSale` throws on an unknown SKU and decrements stock.
//   * `RevenueToday` sums UnitPrice*Quantity for sales on the same UTC date as
//     `now` (parity with C# `s.AtUtc.Date == now.Date`).
//   * `TopSellersSince` groups by SKU, sums quantity, orders by sold DESC, takes topK.
//   * `{value:C}` currency formatting reproduced via [fmtC] (US culture).

package com.bhengubv.circleai.retail

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.math.BigDecimal
import java.text.NumberFormat
import java.time.Instant
import java.time.ZoneOffset
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (RetailPrimitives.cs)
// =====================================================================

/** A sellable product. Mirrors C# `Product`. */
data class Product(val sku: String, val name: String, val price: BigDecimal, val currency: String, val category: String?)

/** A stock level for a SKU. Mirrors C# `StockLevel`. */
data class StockLevel(val sku: String, val quantity: Int)

/** A recorded sale. Mirrors C# `Sale`. */
data class Sale(val saleId: String, val sku: String, val quantity: Int, val unitPrice: BigDecimal, val atUtc: Instant)

/** A top-seller rollup row. Mirrors the C# `(string Sku, int Sold)` tuple. */
data class TopSeller(val sku: String, val sold: Int)

/** Deterministic retail board. Mirrors C# `IRetailBoard`. */
interface IRetailBoard {
    fun addProduct(p: Product)
    fun getProduct(sku: String): Product?
    fun setStock(l: StockLevel)
    fun stock(sku: String): Int
    fun recordSale(s: Sale)
    fun revenueToday(now: Instant): BigDecimal
    fun topSellersSince(since: Instant, topK: Int = 5): List<TopSeller>
}

/** In-memory [IRetailBoard]. Mirrors C# `InMemoryRetailBoard`. */
class InMemoryRetailBoard : IRetailBoard {
    private val products = ConcurrentHashMap<String, Product>()
    private val stockLevels = ConcurrentHashMap<String, Int>()
    private val sales = mutableListOf<Sale>()
    private val lock = Any()

    override fun addProduct(p: Product) { products[p.sku] = p }
    override fun getProduct(sku: String): Product? = products[sku]

    override fun setStock(l: StockLevel) { stockLevels[l.sku] = l.quantity }
    override fun stock(sku: String): Int = stockLevels[sku] ?: 0

    override fun recordSale(s: Sale) {
        if (!products.containsKey(s.sku)) throw IllegalStateException("Unknown SKU ${s.sku}")
        synchronized(lock) {
            sales.add(s)
            stockLevels[s.sku] = stock(s.sku) - s.quantity
        }
    }

    override fun revenueToday(now: Instant): BigDecimal = synchronized(lock) {
        val today = now.atZone(ZoneOffset.UTC).toLocalDate()
        sales.filter { it.atUtc.atZone(ZoneOffset.UTC).toLocalDate() == today }
            .fold(BigDecimal.ZERO) { acc, s -> acc + s.unitPrice.multiply(BigDecimal(s.quantity)) }
    }

    override fun topSellersSince(since: Instant, topK: Int): List<TopSeller> {
        if (topK <= 0) throw IllegalArgumentException("topK must be positive")
        synchronized(lock) {
            return sales.filter { it.atUtc >= since }
                .groupBy { it.sku }
                .map { (sku, group) -> TopSeller(sku, group.sumOf { it.quantity }) }
                .sortedByDescending { it.sold }
                .take(topK)
        }
    }
}

// =====================================================================
// DomainContext (RetailDomainContext.cs)
// =====================================================================

/** Static domain context for Retail. Mirrors C# `RetailDomainContext`. */
object RetailDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Retail] Expert retail operations assistant. Help with stock replenishment, planogram " +
            "optimisation, shrinkage reduction, seasonal promotions, customer loyalty, and sales floor " +
            "management. Ground advice in margin and sell-through rates. Compliance: Consumer Protection Act, POPIA."

    val complianceFlags: List<String> = listOf("Consumer_Protection_Act", "POPIA", "Labour_Relations_Act")

    val suggestedTools: List<String> = listOf("pos_system", "inventory", "analytics", "promotions_engine")
}

// =====================================================================
// CompanionAdapter (RetailCompanionAdapter.cs)
// =====================================================================

/** Formats like .NET `{value:C}` under the US culture. */
internal fun fmtC(value: BigDecimal): String = NumberFormat.getCurrencyInstance(Locale.US).format(value)

/** Wraps an [ICompanionSession] with the Retail snippet + helpers. Mirrors C# `RetailCompanionAdapter`. */
class RetailCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
    override val sessionId: String get() = inner.sessionId
    override val identityId: String get() = inner.identityId
    override val interfaceKind: InterfaceKind get() = inner.interfaceKind
    override val history: List<CompanionTurn> get() = inner.history
    override val proactiveEvents: Flow<CompanionProactiveEvent> get() = inner.proactiveEvents

    override fun getContext(): CompanionContext = inner.getContext()
    override suspend fun refreshContextAsync() = inner.refreshContextAsync()
    override suspend fun signalFeedbackAsync(positive: Boolean, note: String?) =
        inner.signalFeedbackAsync(positive, note)
    override fun close() = inner.close()

    override suspend fun sendAsync(message: String): String = inner.sendAsync(enrich(message))
    override fun streamAsync(message: String): Flow<String> = inner.streamAsync(enrich(message))
    override suspend fun agentAsync(instruction: String): String = inner.agentAsync(enrich(instruction))

    private fun enrich(m: String): String = "${RetailDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun analyseStockHealthAsync(sku: String, onHand: Int, weeklySales: Int): String =
        inner.agentAsync("Analyse stock health for SKU $sku: $onHand units on hand, $weeklySales weekly sales. Recommend reorder point, safety stock, and EOQ.")

    suspend fun planPromotionAsync(objective: String, constraints: String): String =
        inner.agentAsync("Plan a retail promotion. Objective: $objective. Constraints: $constraints. Include mechanics, discount level, marketing channels, and success metrics.")

    suspend fun optimiseProductMixAsync(topSellersJson: String, slowMoversJson: String): String =
        inner.agentAsync("Recommend product mix changes from sellers: $topSellersJson and slow: $slowMoversJson. Cover ranging, replenishment, markdown.")

    suspend fun designPromotionAsync(goal: String, category: String, budget: BigDecimal): String =
        inner.agentAsync("Design a $goal promotion for $category on $budget budget. Mechanic, channel mix, expected lift, guardrails.")

    suspend fun handleStockoutAsync(sku: String, demandSignal: String, leadTimeDays: Int): String =
        inner.agentAsync("Handle stockout of $sku (demand: $demandSignal, lead ${leadTimeDays}d). Recovery options + customer comms.")

    suspend fun reviewDailyTradingAsync(salesByCategory: String, targetRevenue: BigDecimal): String =
        inner.agentAsync("Review today's trading: $salesByCategory vs target $targetRevenue. Wins, misses, tomorrow's adjustments.")
}
