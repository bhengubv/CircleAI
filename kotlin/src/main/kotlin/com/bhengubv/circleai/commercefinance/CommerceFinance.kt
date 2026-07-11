// CommerceFinance.kt
//
// Kotlin port of CircleAI.Commerce.Finance (FinancePrimitives.cs +
// CommerceFinanceDomainContext.cs + CommerceFinanceCompanionAdapter.cs) — the
// C# reference is the EXACT spec. Deterministic in-memory invoicing board:
// invoices, payments, overdue detection, outstanding balances.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `decimal` -> `java.math.BigDecimal`.
//   * C# `DateTime` (issue/due dates) -> `java.time.LocalDate`.
//   * C# `DateTimeOffset` (FinancePayment.AtUtc) -> `java.time.Instant`.
//   * C# `ConcurrentDictionary<string,_>` (Ordinal) -> `ConcurrentHashMap`;
//     payments live in a plain list behind a lock (mirrors `List` + `lock`).
//   * `RemainingOn` bills Σ(line.Amount × (1 + taxPct/100)) then subtracts
//     recorded payments — the multiplier is computed in Double exactly as the
//     C# `(decimal)(1 + l.TaxPct / 100.0)`, then applied in BigDecimal.
//   * `MarkOverdue` flips DueDate<asOf & not "Paid" (case-insensitive) to
//     "Overdue".
//   * `TotalOutstanding` sums RemainingOn over every invoice; `Overdue`
//     returns invoices whose status is "Overdue" (case-insensitive).

package com.bhengubv.circleai.commercefinance

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.math.BigDecimal
import java.time.Instant
import java.time.LocalDate
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (FinancePrimitives.cs)
// =====================================================================

/** A single invoice line (net amount + tax %). Mirrors C# `InvoiceLine`. */
data class InvoiceLine(val description: String, val amount: BigDecimal, val taxPct: Double)

/** An invoice. Mirrors C# `Invoice`. */
data class Invoice(
    val invoiceId: String,
    val customerId: String,
    val issueDate: LocalDate,
    val dueDate: LocalDate,
    val lines: List<InvoiceLine>,
    val currency: String,
    val status: String,
)

/** A payment recorded against an invoice. Mirrors C# `FinancePayment`. */
data class FinancePayment(val paymentId: String, val invoiceId: String, val amount: BigDecimal, val atUtc: Instant)

/** Deterministic invoicing board. Mirrors C# `IInvoiceBoard`. */
interface IInvoiceBoard {
    fun issue(i: Invoice)
    fun get(invoiceId: String): Invoice?
    fun recordPayment(p: FinancePayment)
    fun markOverdue(asOf: LocalDate)
    fun remainingOn(invoiceId: String): BigDecimal
    fun totalOutstanding(): BigDecimal
    fun overdue(): List<Invoice>
}

/** In-memory [IInvoiceBoard]. Mirrors C# `InMemoryInvoiceBoard`. */
class InMemoryInvoiceBoard : IInvoiceBoard {
    private val invoices = ConcurrentHashMap<String, Invoice>()
    private val payments = mutableListOf<FinancePayment>()
    private val lock = Any()

    override fun issue(i: Invoice) { invoices[i.invoiceId] = i }
    override fun get(invoiceId: String): Invoice? = invoices[invoiceId]
    override fun recordPayment(p: FinancePayment) { synchronized(lock) { payments.add(p) } }

    override fun markOverdue(asOf: LocalDate) {
        invoices.values
            .filter { it.dueDate < asOf && !it.status.equals("Paid", ignoreCase = true) }
            .forEach { invoices[it.invoiceId] = it.copy(status = "Overdue") }
    }

    override fun remainingOn(invoiceId: String): BigDecimal {
        val inv = invoices[invoiceId] ?: return BigDecimal.ZERO
        val billed = inv.lines.fold(BigDecimal.ZERO) { acc, l ->
            acc + l.amount * BigDecimal.valueOf(1 + l.taxPct / 100.0)
        }
        val paid = synchronized(lock) {
            payments.filter { it.invoiceId == invoiceId }.fold(BigDecimal.ZERO) { acc, p -> acc + p.amount }
        }
        return billed - paid
    }

    override fun totalOutstanding(): BigDecimal =
        invoices.keys.fold(BigDecimal.ZERO) { acc, id -> acc + remainingOn(id) }

    override fun overdue(): List<Invoice> =
        invoices.values.filter { it.status.equals("Overdue", ignoreCase = true) }
}

// =====================================================================
// DomainContext (CommerceFinanceDomainContext.cs)
// =====================================================================

/** Static domain context for Commerce.Finance. Mirrors C# `CommerceFinanceDomainContext`. */
object CommerceFinanceDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Commerce.Finance] You are a commercial finance expert. Help with working capital " +
            "optimisation, cash flow forecasting, business credit applications, debt structuring, and " +
            "treasury policy. Ground advice in the cash conversion cycle and credit profile. " +
            "Compliance: NCA (National Credit Act 34 of 2005), SARB prudential rules, POPIA."

    val complianceFlags: List<String> = listOf("NCA_34_2005", "SARB_aware", "POPIA", "IFRS")

    val suggestedTools: List<String> = listOf("cash_flow_model", "spreadsheet", "web_search")
}

/** Formats like .NET `{value:C}` under the US culture. */
internal fun fmtC(value: BigDecimal): String =
    java.text.NumberFormat.getCurrencyInstance(java.util.Locale.US).format(value)

// =====================================================================
// CompanionAdapter (CommerceFinanceCompanionAdapter.cs)
// =====================================================================

/**
 * Wraps an [ICompanionSession] with the Commerce.Finance snippet + helpers.
 * Mirrors C# `CommerceFinanceCompanionAdapter`.
 */
class CommerceFinanceCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${CommerceFinanceDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun forecastCashFlowAsync(financials: String, weeksAhead: Int): String =
        inner.agentAsync("Forecast cash flow for $weeksAhead weeks based on:\n$financials\nIdentify liquidity risks and recommend mitigation actions.")

    suspend fun structureDebtAsync(context: String, amount: BigDecimal): String =
        inner.agentAsync("Recommend a debt structure for a business needing ${fmtC(amount)}. Context:\n$context\nCompare term loans, revolving credit, and invoice financing.")

    suspend fun reviewCreditApplicationAsync(applicationData: String): String =
        inner.agentAsync("Review this credit application and identify strengths, weaknesses, and risk factors:\n$applicationData")

    suspend fun generateAgingReportAsync(outstandingInvoices: String): String =
        inner.agentAsync("Generate an aging report from: $outstandingInvoices. Bucket 0-30/31-60/61-90/90+, name the worst offenders, suggest collection actions.")

    suspend fun prepareInvoiceFollowUpAsync(customerName: String, amount: BigDecimal, daysOverdue: Int): String =
        inner.agentAsync("Draft a follow-up message to $customerName for $amount due $daysOverdue days. Tone: firm but relationship-preserving.")

    suspend fun evaluateCreditAsync(customerSummary: String, proposedLimit: BigDecimal): String =
        inner.agentAsync("Evaluate credit-worthiness of $customerSummary for a $proposedLimit limit. Recommend approve/decline + conditions.")

    suspend fun forecastCashFlowAsync(outstandingInvoices: String, upcomingExpenses: String, horizonDays: Int): String =
        inner.agentAsync("Forecast cash flow for next $horizonDays days from invoices: $outstandingInvoices and expenses: $upcomingExpenses. Flag squeeze points.")
}
