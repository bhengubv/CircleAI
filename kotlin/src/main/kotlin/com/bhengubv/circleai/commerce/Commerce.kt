// Commerce.kt
//
// Kotlin port of CircleAI.Commerce (CommercePrimitives.cs +
// CommerceDomainContext.cs + CommerceCompanionAdapter.cs) — the C# reference
// is the EXACT spec. Deterministic in-memory commerce board: customers,
// orders, line items, lifetime value.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `decimal` -> `java.math.BigDecimal`.
//   * C# `DateTimeOffset` -> `java.time.Instant`.
//   * C# `ConcurrentDictionary<string,_>` (Ordinal) -> `ConcurrentHashMap`.
//   * Line items live in a plain list behind a lock (mirrors the C# `List` +
//     `lock (_lock)`); `AddLine` / `LinesFor` reproduced exactly.
//   * `OrdersFor` orders by AtUtc DESC; `LifetimeValue` sums those order totals.
//   * C# `{price:C}` currency formatting in the adapter is reproduced via a
//     US-culture currency helper — the value is only ever embedded in a prompt
//     string, so exact glyphs are not load-bearing, but intent is preserved.

package com.bhengubv.circleai.commerce

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.math.BigDecimal
import java.text.NumberFormat
import java.time.Instant
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (CommercePrimitives.cs)
// =====================================================================

/** A commerce customer. Mirrors C# `CommerceCustomer`. */
data class CommerceCustomer(val customerId: String, val name: String, val email: String?, val createdUtc: Instant)

/** A placed order. Mirrors C# `CommerceOrder`. */
data class CommerceOrder(
    val orderId: String,
    val customerId: String,
    val total: BigDecimal,
    val currency: String,
    val status: String,
    val atUtc: Instant,
)

/** A single order line. Mirrors C# `CommerceLineItem`. */
data class CommerceLineItem(
    val lineId: String,
    val orderId: String,
    val sku: String,
    val quantity: Int,
    val unitPrice: BigDecimal,
)

/** Deterministic commerce board. Mirrors C# `ICommerceBoard`. */
interface ICommerceBoard {
    fun addCustomer(c: CommerceCustomer)
    fun getCustomer(id: String): CommerceCustomer?
    fun place(o: CommerceOrder)
    fun addLine(l: CommerceLineItem)
    fun updateStatus(orderId: String, status: String)
    fun ordersFor(customerId: String): List<CommerceOrder>
    fun linesFor(orderId: String): List<CommerceLineItem>
    fun lifetimeValue(customerId: String): BigDecimal
}

/** In-memory [ICommerceBoard]. Mirrors C# `InMemoryCommerceBoard`. */
class InMemoryCommerceBoard : ICommerceBoard {
    private val customers = ConcurrentHashMap<String, CommerceCustomer>()
    private val orders = ConcurrentHashMap<String, CommerceOrder>()
    private val lines = mutableListOf<CommerceLineItem>()
    private val lock = Any()

    override fun addCustomer(c: CommerceCustomer) { customers[c.customerId] = c }
    override fun getCustomer(id: String): CommerceCustomer? = customers[id]
    override fun place(o: CommerceOrder) { orders[o.orderId] = o }
    override fun addLine(l: CommerceLineItem) { synchronized(lock) { lines.add(l) } }

    override fun updateStatus(orderId: String, status: String) {
        val o = orders[orderId] ?: throw IllegalStateException("Unknown order $orderId")
        orders[orderId] = o.copy(status = status)
    }

    override fun ordersFor(customerId: String): List<CommerceOrder> =
        orders.values.filter { it.customerId == customerId }.sortedByDescending { it.atUtc }

    override fun linesFor(orderId: String): List<CommerceLineItem> =
        synchronized(lock) { lines.filter { it.orderId == orderId } }

    override fun lifetimeValue(customerId: String): BigDecimal =
        ordersFor(customerId).fold(BigDecimal.ZERO) { acc, o -> acc + o.total }
}

// =====================================================================
// DomainContext (CommerceDomainContext.cs)
// =====================================================================

/** Static domain context for the Commerce vertical. Mirrors C# `CommerceDomainContext`. */
object CommerceDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Commerce] You are an e-commerce and trading expert. Help with product listings, " +
            "pricing strategy, order management, supplier negotiations, marketplace analytics, and sales " +
            "optimisation. Apply margin-aware thinking to every recommendation. Compliance: Consumer Protection Act, POPIA."

    val complianceFlags: List<String> = listOf("POPIA", "Consumer_Protection_Act", "GDPR_aware")

    val suggestedTools: List<String> = listOf("inventory", "pricing_engine", "order_management", "analytics")
}

/** Formats like .NET `{value:C}` under the invariant/US culture. */
internal fun fmtC(value: BigDecimal): String = NumberFormat.getCurrencyInstance(Locale.US).format(value)

// =====================================================================
// CompanionAdapter (CommerceCompanionAdapter.cs)
// =====================================================================

/**
 * Wraps an [ICompanionSession] with the Commerce domain snippet + domain
 * helpers. Mirrors C# `CommerceCompanionAdapter`.
 */
class CommerceCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${CommerceDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun optimiseListingAsync(productDetails: String): String =
        inner.agentAsync("Optimise this product listing for search discovery and conversions:\n$productDetails")

    suspend fun analysePricingAsync(product: String, currentPrice: BigDecimal): String =
        inner.agentAsync("Analyse pricing for: $product at ${fmtC(currentPrice)}. Recommend optimal pricing considering margins, competition, and demand.")

    suspend fun generateSupplierBriefAsync(productRequirements: String): String =
        inner.agentAsync("Write a supplier brief for: $productRequirements. Include quantity, specs, quality standards, delivery terms, and pricing expectations.")

    suspend fun writeProductDescriptionAsync(productName: String, features: String, targetCustomer: String): String =
        inner.agentAsync("Write a product description for $productName aimed at $targetCustomer. Features: $features. Use the 'feature → benefit' pattern, end with a CTA.")

    suspend fun analyseConversionFunnelAsync(funnelMetrics: String): String =
        inner.agentAsync("Analyse this funnel: $funnelMetrics. Identify the biggest drop-off, the likely cause, and the test to validate.")

    suspend fun suggestUpsellAsync(cartContents: String, cartTotal: BigDecimal): String =
        inner.agentAsync("Suggest 1-2 upsells for this cart: $cartContents (total $cartTotal). Justify each with attach rate intuition + margin notes.")

    suspend fun draftReturnPolicyAsync(category: String, region: String): String =
        inner.agentAsync("Draft a return policy for $category sold in $region. Comply with local consumer law, balance customer trust with fraud prevention.")
}
