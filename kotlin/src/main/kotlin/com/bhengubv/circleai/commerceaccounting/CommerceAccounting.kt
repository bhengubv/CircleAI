// CommerceAccounting.kt
//
// Kotlin port of CircleAI.Commerce.Accounting (AccountingPrimitives.cs +
// CommerceAccountingDomainContext.cs + CommerceAccountingCompanionAdapter.cs)
// — the C# reference is the EXACT spec. Deterministic in-memory
// double-entry accounting board.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `decimal` -> `java.math.BigDecimal`.
//   * C# `DateTime AtUtc` is used for `.Year`/`.Month` + ordering -> mapped to
//     `java.time.LocalDateTime` (a wall-clock timestamp, not a date-only value).
//   * C# `ConcurrentDictionary<string,_>` (Ordinal) -> `ConcurrentHashMap`;
//     entries live in a plain list behind a lock (mirrors `List` + `lock`).
//   * `Post` rejects negative debit/credit amounts.
//   * `AccountBalance` / `Sum` compute Σ(debit − credit); `Sum` filters by
//     year+month; `ForAccount` filters likewise and orders by AtUtc ASC.
//   * `NetProfit` = Sum(revenue, period) − Sum(expense, period).

package com.bhengubv.circleai.commerceaccounting

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.math.BigDecimal
import java.time.LocalDateTime
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (AccountingPrimitives.cs)
// =====================================================================

/** A single posted double-entry line. Mirrors C# `AccountingEntry`. */
data class AccountingEntry(
    val entryId: String,
    val atUtc: LocalDateTime,
    val accountCode: String,
    val debitAmount: BigDecimal,
    val creditAmount: BigDecimal,
    val memo: String,
)

/** A named tax rate. Mirrors C# `TaxRate`. */
data class TaxRate(val code: String, val percentage: Double)

/** A reporting period (year + month). Mirrors C# `Period`. */
data class Period(val year: Int, val month: Int)

/** Deterministic accounting board. Mirrors C# `IAccountingBoard`. */
interface IAccountingBoard {
    fun post(e: AccountingEntry)
    fun defineTax(r: TaxRate)
    fun getTax(code: String): TaxRate?
    fun accountBalance(accountCode: String): BigDecimal
    fun sum(accountCode: String, p: Period): BigDecimal
    fun forAccount(accountCode: String, p: Period): List<AccountingEntry>
    fun netProfit(p: Period, revenueAccount: String, expenseAccount: String): BigDecimal
}

/** In-memory [IAccountingBoard]. Mirrors C# `InMemoryAccountingBoard`. */
class InMemoryAccountingBoard : IAccountingBoard {
    private val entries = mutableListOf<AccountingEntry>()
    private val tax = ConcurrentHashMap<String, TaxRate>()
    private val lock = Any()

    override fun post(e: AccountingEntry) {
        require(e.debitAmount >= BigDecimal.ZERO && e.creditAmount >= BigDecimal.ZERO) {
            "amounts must be non-negative"
        }
        synchronized(lock) { entries.add(e) }
    }

    override fun defineTax(r: TaxRate) { tax[r.code] = r }
    override fun getTax(code: String): TaxRate? = tax[code]

    override fun accountBalance(accountCode: String): BigDecimal = synchronized(lock) {
        entries.filter { it.accountCode == accountCode }
            .fold(BigDecimal.ZERO) { acc, e -> acc + (e.debitAmount - e.creditAmount) }
    }

    override fun sum(accountCode: String, p: Period): BigDecimal = synchronized(lock) {
        entries.filter { it.accountCode == accountCode && it.atUtc.year == p.year && it.atUtc.monthValue == p.month }
            .fold(BigDecimal.ZERO) { acc, e -> acc + (e.debitAmount - e.creditAmount) }
    }

    override fun forAccount(accountCode: String, p: Period): List<AccountingEntry> = synchronized(lock) {
        entries.filter { it.accountCode == accountCode && it.atUtc.year == p.year && it.atUtc.monthValue == p.month }
            .sortedBy { it.atUtc }
    }

    override fun netProfit(p: Period, revenueAccount: String, expenseAccount: String): BigDecimal =
        sum(revenueAccount, p) - sum(expenseAccount, p)
}

// =====================================================================
// DomainContext (CommerceAccountingDomainContext.cs)
// =====================================================================

/** Static domain context for Commerce.Accounting. Mirrors C# `CommerceAccountingDomainContext`. */
object CommerceAccountingDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Commerce.Accounting] You are an expert accounting assistant. Help with bookkeeping, " +
            "bank reconciliation, VAT calculations (SA 15% standard rate), financial statement preparation, " +
            "cash flow analysis, and audit trail documentation. Cite relevant IFRS or GAAP standards. " +
            "Compliance: Companies Act 71 of 2008, SARS regulations, IFRS for SMEs."

    val complianceFlags: List<String> = listOf("IFRS", "SARS", "Companies_Act_71_2008", "VAT_Act")

    val suggestedTools: List<String> = listOf("accounting_software", "spreadsheet", "document_editor")
}

/** Formats like .NET `{value:C}` under the US culture. */
internal fun fmtC(value: BigDecimal): String =
    java.text.NumberFormat.getCurrencyInstance(java.util.Locale.US).format(value)

// =====================================================================
// CompanionAdapter (CommerceAccountingCompanionAdapter.cs)
// =====================================================================

/**
 * Wraps an [ICompanionSession] with the Commerce.Accounting snippet + helpers.
 * Mirrors C# `CommerceAccountingCompanionAdapter`.
 */
class CommerceAccountingCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${CommerceAccountingDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun reconcileAsync(bankStatement: String, ledger: String): String =
        inner.agentAsync("Reconcile these records and identify discrepancies.\n\nBank statement:\n$bankStatement\n\nLedger:\n$ledger")

    suspend fun prepareVatReturnAsync(period: String, salesTotal: BigDecimal, purchasesTotal: BigDecimal): String =
        inner.agentAsync("Prepare a VAT201 return summary for $period. Output VAT on sales ${fmtC(salesTotal)}, Input VAT on purchases ${fmtC(purchasesTotal)}. Show net payable/refundable and filing checklist.")

    suspend fun draftManagementAccountsAsync(financialData: String, period: String): String =
        inner.agentAsync("Draft management accounts for $period from this data:\n$financialData\nInclude P&L, balance sheet summary, cash flow, and key ratio analysis.")

    suspend fun explainJournalEntryAsync(entryDescription: String): String =
        inner.agentAsync("Translate this transaction into double-entry journal lines: $entryDescription. Show debits/credits, account codes, narrative.")

    suspend fun reconcileVarianceAsync(accountCode: String, bookBalance: BigDecimal, statementBalance: BigDecimal): String =
        inner.agentAsync("Reconcile $accountCode: book $bookBalance vs statement $statementBalance. List likely variance causes + the journal to fix each.")

    suspend fun generateTrialBalanceCommentaryAsync(period: String, topMovements: String): String =
        inner.agentAsync("Comment on the trial balance for $period. Top movements: $topMovements. Explain abnormal swings.")

    suspend fun draftVatReturnNarrativeAsync(period: String, outputVat: BigDecimal, inputVat: BigDecimal): String =
        inner.agentAsync("Draft VAT return narrative for $period: output $outputVat, input $inputVat. Cover net payable, anomalies, supporting documents.")
}
