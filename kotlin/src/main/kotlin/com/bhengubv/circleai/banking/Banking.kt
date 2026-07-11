// Banking.kt
//
// Kotlin port of CircleAI.Banking (Contracts.cs + InMemoryBanking.cs +
// NullImplementations.cs) — the C# reference is the EXACT spec. Real
// in-memory banking primitives: account store, ledger writer, and a payment
// processor with balance checks + double-entry bookkeeping (debit source,
// credit destination). Hosts needing durability swap in a database-backed
// implementation behind the same contracts.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `decimal` -> `java.math.BigDecimal` (money is never Double).
//   * C# `DateTimeOffset` -> `java.time.Instant`.
//   * C# `ValueTask<T>` -> `suspend fun`.
//   * C# `ConcurrentDictionary<string,_>` (Ordinal) -> `ConcurrentHashMap`.
//   * C# `Guid.NewGuid().ToString("n")` -> UUID hex without dashes.
//   * C# `Guid.Empty.ToString()` -> the all-zeros UUID string.
//   * `_txLock` is reentrant (JVM monitors are): `processPayment` holds it and
//     calls `append`, which re-acquires the same monitor — safe, exactly as the
//     C# `lock (_txLock)` does.
//   * `read` orders by AtUtc DESC then takes `limit`; `append` mutates balance
//     and appends — reproduced exactly.
//   * Currency comparison is case-insensitive (OrdinalIgnoreCase).

package com.bhengubv.circleai.banking

import java.math.BigDecimal
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** A bank account. Mirrors C# `Account`. */
data class Account(val accountId: String, val ownerId: String, val currency: String, val balance: BigDecimal)

/** A single ledger entry. Mirrors C# `LedgerEntry`. */
data class LedgerEntry(
    val txId: String,
    val accountId: String,
    val amount: BigDecimal,
    val memo: String,
    val atUtc: Instant,
)

/** A payment instruction. Mirrors C# `PaymentRequest`. */
data class PaymentRequest(
    val fromAccount: String,
    val toAccount: String,
    val amount: BigDecimal,
    val currency: String,
    val memo: String,
)

/** The outcome of a payment attempt. Mirrors C# `PaymentResult`. */
data class PaymentResult(val txId: String, val accepted: Boolean, val failureReason: String?)

/** Read-only account access, backend-identified. Mirrors C# `IAccountReader`. */
interface IAccountReader {
    val backendId: String
    suspend fun getAccountAsync(accountId: String): Account?
    suspend fun listForOwnerAsync(ownerId: String): List<Account>
}

/** Append-only ledger access, backend-identified. Mirrors C# `ILedgerWriter`. */
interface ILedgerWriter {
    val backendId: String
    suspend fun appendAsync(entry: LedgerEntry): LedgerEntry
    suspend fun readAsync(accountId: String, limit: Int = 100): List<LedgerEntry>
}

/** Payment execution, backend-identified. Mirrors C# `IPaymentProcessor`. */
interface IPaymentProcessor {
    val backendId: String
    suspend fun processAsync(req: PaymentRequest): PaymentResult
}

// =====================================================================
// InMemoryBank (InMemoryBanking.cs)
// =====================================================================

private fun newTxId(): String = UUID.randomUUID().toString().replace("-", "")

/** Concurrent in-memory bank shared by reader/ledger/payment. Mirrors C# `InMemoryBank`. */
class InMemoryBank {
    private val accounts = ConcurrentHashMap<String, Account>()
    private val ledger = ConcurrentHashMap<String, MutableList<LedgerEntry>>()
    private val txLock = Any()

    fun seedAccount(account: Account) { accounts[account.accountId] = account }

    fun get(id: String): Account? = accounts[id]

    fun listForOwner(ownerId: String): List<Account> =
        accounts.values.filter { it.ownerId == ownerId }

    fun append(entry: LedgerEntry): LedgerEntry = synchronized(txLock) {
        val acct = accounts[entry.accountId]
            ?: throw IllegalStateException("Unknown account ${entry.accountId}")
        accounts[entry.accountId] = acct.copy(balance = acct.balance + entry.amount)
        ledger.getOrPut(entry.accountId) { mutableListOf() }.add(entry)
        entry
    }

    fun read(accountId: String, limit: Int): List<LedgerEntry> {
        val list = ledger[accountId] ?: return emptyList()
        return synchronized(txLock) {
            list.sortedByDescending { it.atUtc }.take(limit)
        }
    }

    fun processPayment(req: PaymentRequest): PaymentResult {
        if (req.amount <= BigDecimal.ZERO) return PaymentResult(newTxId(), false, "Amount must be positive")
        return synchronized(txLock) {
            val src = accounts[req.fromAccount]
                ?: return@synchronized PaymentResult(newTxId(), false, "Unknown source account")
            val dst = accounts[req.toAccount]
                ?: return@synchronized PaymentResult(newTxId(), false, "Unknown destination account")
            if (!src.currency.equals(req.currency, ignoreCase = true) ||
                !dst.currency.equals(req.currency, ignoreCase = true)
            ) {
                return@synchronized PaymentResult(newTxId(), false, "Currency mismatch")
            }
            if (src.balance < req.amount) {
                return@synchronized PaymentResult(newTxId(), false, "Insufficient funds")
            }

            val txId = newTxId()
            val now = Instant.now()
            append(LedgerEntry(txId, req.fromAccount, req.amount.negate(), "To ${req.toAccount}: ${req.memo}", now))
            append(LedgerEntry(txId, req.toAccount, req.amount, "From ${req.fromAccount}: ${req.memo}", now))
            PaymentResult(txId, true, null)
        }
    }
}

/** In-memory [IAccountReader] over a shared [InMemoryBank]. Mirrors C# `InMemoryAccountReader`. */
class InMemoryAccountReader(private val bank: InMemoryBank) : IAccountReader {
    override val backendId: String get() = "in-memory"
    override suspend fun getAccountAsync(accountId: String): Account? = bank.get(accountId)
    override suspend fun listForOwnerAsync(ownerId: String): List<Account> = bank.listForOwner(ownerId)
}

/** In-memory [ILedgerWriter] over a shared [InMemoryBank]. Mirrors C# `InMemoryLedgerWriter`. */
class InMemoryLedgerWriter(private val bank: InMemoryBank) : ILedgerWriter {
    override val backendId: String get() = "in-memory"
    override suspend fun appendAsync(entry: LedgerEntry): LedgerEntry = bank.append(entry)
    override suspend fun readAsync(accountId: String, limit: Int): List<LedgerEntry> = bank.read(accountId, limit)
}

/** In-memory [IPaymentProcessor] over a shared [InMemoryBank]. Mirrors C# `InMemoryPaymentProcessor`. */
class InMemoryPaymentProcessor(private val bank: InMemoryBank) : IPaymentProcessor {
    override val backendId: String get() = "in-memory"
    override suspend fun processAsync(req: PaymentRequest): PaymentResult = bank.processPayment(req)
}

// =====================================================================
// Null implementations (NullImplementations.cs) — fail-closed defaults
// =====================================================================

/** Fail-closed [IAccountReader]. Mirrors C# `NullAccountReader`. */
class NullAccountReader private constructor() : IAccountReader {
    override val backendId: String get() = "null"
    override suspend fun getAccountAsync(accountId: String): Account? = null
    override suspend fun listForOwnerAsync(ownerId: String): List<Account> = emptyList()

    companion object { val Instance: NullAccountReader = NullAccountReader() }
}

/** Fail-closed [ILedgerWriter]. Mirrors C# `NullLedgerWriter`. */
class NullLedgerWriter private constructor() : ILedgerWriter {
    override val backendId: String get() = "null"
    override suspend fun appendAsync(entry: LedgerEntry): LedgerEntry = entry
    override suspend fun readAsync(accountId: String, limit: Int): List<LedgerEntry> = emptyList()

    companion object { val Instance: NullLedgerWriter = NullLedgerWriter() }
}

/** Fail-closed [IPaymentProcessor]. Mirrors C# `NullPaymentProcessor`. */
class NullPaymentProcessor private constructor() : IPaymentProcessor {
    override val backendId: String get() = "null"
    override suspend fun processAsync(req: PaymentRequest): PaymentResult =
        PaymentResult(EMPTY_GUID, false, "NullPaymentProcessor.")

    companion object {
        val Instance: NullPaymentProcessor = NullPaymentProcessor()
        private const val EMPTY_GUID = "00000000-0000-0000-0000-000000000000"
    }
}
