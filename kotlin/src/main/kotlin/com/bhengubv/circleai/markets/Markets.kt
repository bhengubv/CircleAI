// Markets.kt
//
// Kotlin port of CircleAI.Markets (Contracts.cs + InMemoryMarkets.cs +
// NullImplementations.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory market-data feed + instrument catalog + order
// router. The feed supports subscribe/broadcast quote pushes; the order
// router accepts/rejects on simple rules (positive quantity, known
// instrument, valid limit price for limit orders).
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; C# `enum` -> Kotlin `enum class`.
//   * C# `decimal` -> `java.math.BigDecimal`; `DateTimeOffset` -> `Instant`.
//   * C# `ValueTask<T>` async members -> `suspend fun`.
//   * Catalog/feed maps keyed `OrdinalIgnoreCase` -> case-insensitive keys
//     (lower-cased) preserving the original symbol on the stored value.
//   * `SubscribeQuotes` returns an `AutoCloseable` subscription; `Publish`
//     snapshots the subscriber list under the gate before invoking, so a
//     handler that disposes mid-broadcast cannot deadlock (snapshot+unlock).
//   * Subscriber exceptions are swallowed (a bad subscriber must not break
//     the publish path).
//   * `InMemoryOrderRouter` sequences order ids atomically ("ord-N").
//   * Null implementations are fail-closed singletons.

package com.bhengubv.circleai.markets

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import java.math.BigDecimal
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** Side of an order. Mirrors C# `OrderSide`. */
enum class OrderSide { Buy, Sell }

/** Order execution type. Mirrors C# `OrderType`. */
enum class OrderType { Market, Limit }

/** A tradable instrument. Mirrors C# `Instrument`. */
data class Instrument(val symbol: String, val exchange: String, val currency: String, val assetClass: String)

/** A market quote. Mirrors C# `Quote`. */
data class Quote(val symbol: String, val bid: BigDecimal, val ask: BigDecimal, val last: BigDecimal, val atUtc: Instant)

/** A request to route an order. Mirrors C# `OrderRequest`. */
data class OrderRequest(
    val symbol: String,
    val side: OrderSide,
    val type: OrderType,
    val quantity: BigDecimal,
    val limitPrice: BigDecimal?,
)

/** The outcome of routing an order. Mirrors C# `OrderResult`. */
data class OrderResult(val orderId: String, val accepted: Boolean, val failureReason: String?)

/** Live/last-quote feed with subscription. Mirrors C# `IMarketDataFeed`. */
interface IMarketDataFeed {
    val backendId: String
    suspend fun getQuoteAsync(symbol: String): Quote?
    fun subscribeQuotes(symbol: String, handler: suspend (Quote) -> Unit): AutoCloseable
}

/** Instrument reference data. Mirrors C# `IInstrumentCatalog`. */
interface IInstrumentCatalog {
    val backendId: String
    suspend fun getAsync(symbol: String): Instrument?
    suspend fun searchAsync(query: String, topK: Int = 20): List<Instrument>
}

/** Order submission gateway. Mirrors C# `IOrderRouter`. */
interface IOrderRouter {
    val backendId: String
    suspend fun submitAsync(req: OrderRequest): OrderResult
}

// =====================================================================
// In-memory implementations (InMemoryMarkets.cs)
// =====================================================================

/** In-memory [IInstrumentCatalog] with case-insensitive symbol keying. Mirrors C# `InMemoryInstrumentCatalog`. */
class InMemoryInstrumentCatalog : IInstrumentCatalog {
    // OrdinalIgnoreCase keying: lower-cased key, original symbol preserved on the value.
    private val items = ConcurrentHashMap<String, Instrument>()
    override val backendId: String get() = "in-memory"

    fun add(item: Instrument) {
        items[item.symbol.lowercase()] = item
    }

    override suspend fun getAsync(symbol: String): Instrument? {
        if (symbol.isBlank()) throw IllegalArgumentException("symbol required")
        return items[symbol.lowercase()]
    }

    override suspend fun searchAsync(query: String, topK: Int): List<Instrument> {
        if (topK <= 0) throw IllegalArgumentException("topK must be positive")
        return items.values
            .filter { it.symbol.contains(query, ignoreCase = true) }
            .sortedBy { it.symbol }
            .take(topK)
    }
}

/** In-memory [IMarketDataFeed] with subscribe/broadcast pushes. Mirrors C# `InMemoryMarketDataFeed`. */
class InMemoryMarketDataFeed : IMarketDataFeed {
    // Case-insensitive symbol keying (lower-cased keys); latest quote and subscriber lists.
    private val quotes = ConcurrentHashMap<String, Quote>()
    private val subs = ConcurrentHashMap<String, MutableList<suspend (Quote) -> Unit>>()
    private val gate = Any()
    // Owns fire-and-forget subscriber dispatch; SupervisorJob so one failing
    // handler cannot cancel siblings or the scope.
    private val dispatchScope = CoroutineScope(Dispatchers.Default + SupervisorJob())
    override val backendId: String get() = "in-memory"

    /**
     * Store the latest quote for its symbol and push it to all subscribers.
     * Subscribers are snapshotted under [gate] and released before invocation
     * so a handler that disposes its subscription mid-broadcast cannot
     * re-enter the lock. A throwing subscriber is swallowed.
     */
    fun publish(q: Quote) {
        val key = q.symbol.lowercase()
        quotes[key] = q
        val list = subs[key] ?: return
        val snap: List<suspend (Quote) -> Unit> = synchronized(gate) { list.toList() }
        for (s in snap) {
            // Fire-and-forget parity with the C# `_ = s(q)`: dispatch onto an
            // isolated scope so a slow/failing subscriber never blocks publish.
            dispatchScope.launch {
                try {
                    s(q)
                } catch (_: Throwable) {
                    // Swallow: a bad subscriber must not break the publish path.
                }
            }
        }
    }

    override suspend fun getQuoteAsync(symbol: String): Quote? {
        if (symbol.isBlank()) throw IllegalArgumentException("symbol required")
        return quotes[symbol.lowercase()]
    }

    override fun subscribeQuotes(symbol: String, handler: suspend (Quote) -> Unit): AutoCloseable {
        if (symbol.isBlank()) throw IllegalArgumentException("symbol required")
        val key = symbol.lowercase()
        val list = subs.getOrPut(key) { mutableListOf() }
        // Register synchronously before returning the handle.
        synchronized(gate) { list.add(handler) }
        return Subscription(key, handler)
    }

    private inner class Subscription(
        private val key: String,
        private val h: suspend (Quote) -> Unit,
    ) : AutoCloseable {
        override fun close() {
            val list = subs[key] ?: return
            synchronized(gate) { list.remove(h) }
        }
    }
}

/** In-memory [IOrderRouter] with simple accept/reject rules. Mirrors C# `InMemoryOrderRouter`. */
class InMemoryOrderRouter(private val catalog: IInstrumentCatalog) : IOrderRouter {
    private val seq = AtomicLong(0)
    override val backendId: String get() = "in-memory"

    override suspend fun submitAsync(req: OrderRequest): OrderResult {
        if (req.quantity <= BigDecimal.ZERO) {
            return OrderResult(nextId(), false, "Quantity must be positive")
        }
        if (req.type == OrderType.Limit && (req.limitPrice == null || req.limitPrice <= BigDecimal.ZERO)) {
            return OrderResult(nextId(), false, "Limit order requires positive LimitPrice")
        }
        val inst = catalog.getAsync(req.symbol)
        if (inst == null) {
            return OrderResult(nextId(), false, "Unknown symbol")
        }
        return OrderResult(nextId(), true, null)
    }

    private fun nextId(): String = "ord-${seq.incrementAndGet()}"
}

// =====================================================================
// Null implementations (NullImplementations.cs) — fail-closed
// =====================================================================

/** Fail-closed [IMarketDataFeed]. Mirrors C# `NullMarketDataFeed`. */
class NullMarketDataFeed private constructor() : IMarketDataFeed {
    override val backendId: String get() = "null"
    override suspend fun getQuoteAsync(symbol: String): Quote? = null
    override fun subscribeQuotes(symbol: String, handler: suspend (Quote) -> Unit): AutoCloseable = EmptyDisposable

    private object EmptyDisposable : AutoCloseable {
        override fun close() {}
    }

    companion object {
        val Instance = NullMarketDataFeed()
    }
}

/** Fail-closed [IInstrumentCatalog]. Mirrors C# `NullInstrumentCatalog`. */
class NullInstrumentCatalog private constructor() : IInstrumentCatalog {
    override val backendId: String get() = "null"
    override suspend fun getAsync(symbol: String): Instrument? = null
    override suspend fun searchAsync(query: String, topK: Int): List<Instrument> = emptyList()

    companion object {
        val Instance = NullInstrumentCatalog()
    }
}

/** Fail-closed [IOrderRouter]. Mirrors C# `NullOrderRouter`. */
class NullOrderRouter private constructor() : IOrderRouter {
    override val backendId: String get() = "null"
    override suspend fun submitAsync(req: OrderRequest): OrderResult =
        OrderResult("00000000-0000-0000-0000-000000000000", false, "NullOrderRouter — fail-closed.")

    companion object {
        val Instance = NullOrderRouter()
    }
}
