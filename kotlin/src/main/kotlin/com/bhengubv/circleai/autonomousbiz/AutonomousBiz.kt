// AutonomousBiz.kt
//
// Kotlin port of CircleAI.AutonomousBiz — the C# reference is the EXACT spec
// (Contracts.cs, InMemoryAutonomousBiz.cs, NullImplementations.cs).
//
// Autonomous-business primitives: a treasury that maintains a running balance
// from revenue events, a revenue loop that is a fan-out pub/sub with a kept
// history, and an append-only decision log.
//
// C# -> Kotlin conventions: decimal -> java.math.BigDecimal,
// DateTimeOffset -> java.time.Instant, ValueTask -> suspend,
// IDisposable (subscribe) -> AutoCloseable.

package com.bhengubv.circleai.autonomousbiz

import java.math.BigDecimal
import java.time.Instant

// ===========================================================================
// Contracts  (Contracts.cs)
// ===========================================================================

data class TreasurySnapshot(val balance: BigDecimal, val currency: String, val atUtc: Instant)

data class RevenueEvent(
    val eventId: String,
    val amount: BigDecimal,
    val currency: String,
    val source: String,
    val atUtc: Instant,
)

data class AutonomousDecision(
    val decisionId: String,
    val rationale: String,
    val chosenAction: String,
    val atUtc: Instant,
)

interface ITreasury {
    val backendId: String
    suspend fun getSnapshot(): TreasurySnapshot
}

interface IRevenueLoop {
    val backendId: String
    fun subscribe(handler: suspend (RevenueEvent) -> Unit): AutoCloseable
    suspend fun read(since: Instant): List<RevenueEvent>
}

interface IDecisionLog {
    val backendId: String
    suspend fun append(d: AutonomousDecision)
    suspend fun read(limit: Int = 100): List<AutonomousDecision>
}

// ===========================================================================
// In-memory implementations  (InMemoryAutonomousBiz.cs)
// ===========================================================================

class InMemoryRevenueLoop : IRevenueLoop {
    private val history = ArrayList<RevenueEvent>()
    private val subs = ArrayList<suspend (RevenueEvent) -> Unit>()
    private val lock = Any()

    override val backendId: String get() = "in-memory"

    suspend fun publish(e: RevenueEvent) {
        val snapshot: List<suspend (RevenueEvent) -> Unit>
        synchronized(lock) {
            history.add(e)
            snapshot = subs.toList()
        }
        for (s in snapshot) {
            try {
                s(e)
            } catch (ex: Exception) {
                // a bad subscriber must not corrupt the loop
            }
        }
    }

    override fun subscribe(handler: suspend (RevenueEvent) -> Unit): AutoCloseable {
        synchronized(lock) { subs.add(handler) }
        return AutoCloseable { synchronized(lock) { subs.remove(handler) } }
    }

    override suspend fun read(since: Instant): List<RevenueEvent> =
        synchronized(lock) { history.filter { !it.atUtc.isBefore(since) } }
}

class InMemoryTreasury(
    private val loop: IRevenueLoop,
    private val currency: String = "ZAR",
) : ITreasury {
    override val backendId: String get() = "in-memory"

    override suspend fun getSnapshot(): TreasurySnapshot {
        val events = loop.read(Instant.MIN)
        val bal = events
            .filter { it.currency.equals(currency, ignoreCase = true) }
            .fold(BigDecimal.ZERO) { acc, e -> acc + e.amount }
        return TreasurySnapshot(bal, currency, Instant.now())
    }
}

class InMemoryDecisionLog : IDecisionLog {
    private val items = ArrayList<AutonomousDecision>()
    private val lock = Any()
    override val backendId: String get() = "in-memory"

    override suspend fun append(d: AutonomousDecision) {
        synchronized(lock) { items.add(d) }
    }

    override suspend fun read(limit: Int): List<AutonomousDecision> {
        require(limit > 0) { "limit must be positive" }
        return synchronized(lock) { items.sortedByDescending { it.atUtc }.take(limit) }
    }
}

// ===========================================================================
// Null implementations  (NullImplementations.cs)
// ===========================================================================

class NullTreasury private constructor() : ITreasury {
    override val backendId: String get() = "null"
    override suspend fun getSnapshot(): TreasurySnapshot =
        TreasurySnapshot(BigDecimal.ZERO, "ZAR", Instant.MIN)

    companion object {
        val Instance = NullTreasury()
    }
}

class NullRevenueLoop private constructor() : IRevenueLoop {
    override val backendId: String get() = "null"
    override fun subscribe(handler: suspend (RevenueEvent) -> Unit): AutoCloseable = AutoCloseable { }
    override suspend fun read(since: Instant): List<RevenueEvent> = emptyList()

    companion object {
        val Instance = NullRevenueLoop()
    }
}

class NullDecisionLog private constructor() : IDecisionLog {
    override val backendId: String get() = "null"
    override suspend fun append(d: AutonomousDecision) {}
    override suspend fun read(limit: Int): List<AutonomousDecision> = emptyList()

    companion object {
        val Instance = NullDecisionLog()
    }
}
