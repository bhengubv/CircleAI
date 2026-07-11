// PersonalFinance.kt
//
// Kotlin port of CircleAI.Personal.Finance (PersonalFinancePrimitives.cs +
// PersonalFinanceDomainContext.cs + PersonalFinanceCompanionAdapter.cs) — the
// C# reference is the EXACT spec. Deterministic in-memory personal-finance
// store: accounts, transactions, budgets, and a monthly summary.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `decimal` -> `java.math.BigDecimal`.
//   * C# `DateTimeOffset` -> `java.time.Instant`.
//   * C# `IReadOnlyDictionary<string,decimal>` -> `Map<String, BigDecimal>`.
//   * Accounts keyed Ordinal; budgets keyed OrdinalIgnoreCase (case-insensitive
//     category) -> a `ConcurrentHashMap` with lower-cased keys preserving the
//     original category on the value.
//   * `Record` throws on an unknown account and mutates the account balance;
//     transactions live in a plain list behind a lock.
//   * `ListForMonth` filters by account + year + month.
//   * `Budgets` orders by category ASC.
//   * `Summarise` groups signed amounts by category; TotalIn = Σ positive,
//     TotalOut = −Σ negative.

package com.bhengubv.circleai.personalfinance

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.math.BigDecimal
import java.time.Instant
import java.time.ZoneOffset
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (PersonalFinancePrimitives.cs)
// =====================================================================

/** A personal-finance account. Mirrors C# `Account`. */
data class Account(val accountId: String, val name: String, val balance: BigDecimal, val currency: String)

/** A recorded transaction (signed amount). Mirrors C# `FinanceTransaction`. */
data class FinanceTransaction(
    val txId: String,
    val accountId: String,
    val amount: BigDecimal,
    val category: String,
    val note: String?,
    val atUtc: Instant,
)

/** A per-category monthly budget cap. Mirrors C# `BudgetLine`. */
data class BudgetLine(val category: String, val monthlyLimit: BigDecimal)

/** A month's rolled-up summary. Mirrors C# `MonthSummary`. */
data class MonthSummary(
    val year: Int,
    val month: Int,
    val totalIn: BigDecimal,
    val totalOut: BigDecimal,
    val byCategory: Map<String, BigDecimal>,
)

/** Deterministic personal-finance board. Mirrors C# `IPersonalFinanceBoard`. */
interface IPersonalFinanceBoard {
    fun upsert(a: Account)
    fun getAccount(id: String): Account?
    fun record(t: FinanceTransaction)
    fun listForMonth(accountId: String, year: Int, month: Int): List<FinanceTransaction>
    fun setBudget(b: BudgetLine)
    val budgets: List<BudgetLine>
    fun summarise(accountId: String, year: Int, month: Int): MonthSummary
}

/** In-memory [IPersonalFinanceBoard]. Mirrors C# `InMemoryPersonalFinanceBoard`. */
class InMemoryPersonalFinanceBoard : IPersonalFinanceBoard {
    private val accounts = ConcurrentHashMap<String, Account>()
    // OrdinalIgnoreCase budget keying: lower-cased key, original category on value.
    private val budgets_ = ConcurrentHashMap<String, BudgetLine>()
    private val txns = mutableListOf<FinanceTransaction>()
    private val lock = Any()

    override fun upsert(a: Account) { accounts[a.accountId] = a }
    override fun getAccount(id: String): Account? = accounts[id]

    override fun record(t: FinanceTransaction) {
        if (!accounts.containsKey(t.accountId)) throw IllegalStateException("Unknown account ${t.accountId}")
        synchronized(lock) {
            txns.add(t)
            val a = accounts.getValue(t.accountId)
            accounts[t.accountId] = a.copy(balance = a.balance + t.amount)
        }
    }

    override fun listForMonth(accountId: String, year: Int, month: Int): List<FinanceTransaction> =
        synchronized(lock) {
            txns.filter {
                it.accountId == accountId &&
                    it.atUtc.atZone(ZoneOffset.UTC).year == year &&
                    it.atUtc.atZone(ZoneOffset.UTC).monthValue == month
            }
        }

    override fun setBudget(b: BudgetLine) { budgets_[b.category.lowercase()] = b }

    override val budgets: List<BudgetLine>
        get() = budgets_.values.sortedBy { it.category }

    override fun summarise(accountId: String, year: Int, month: Int): MonthSummary {
        val rows = listForMonth(accountId, year, month)
        val byCat = rows.groupBy { it.category }
            .mapValues { (_, v) -> v.fold(BigDecimal.ZERO) { acc, t -> acc + t.amount } }
        val inSum = rows.filter { it.amount > BigDecimal.ZERO }
            .fold(BigDecimal.ZERO) { acc, t -> acc + t.amount }
        val outSum = rows.filter { it.amount < BigDecimal.ZERO }
            .fold(BigDecimal.ZERO) { acc, t -> acc + t.amount }.negate()
        return MonthSummary(year, month, inSum, outSum, byCat)
    }
}

// =====================================================================
// DomainContext (PersonalFinanceDomainContext.cs)
// =====================================================================

/** Static domain context for Personal.Finance. Mirrors C# `PersonalFinanceDomainContext`. */
object PersonalFinanceDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Personal.Finance] Personal finance coach. Help with monthly budgeting, emergency fund " +
            "planning, debt snowball/avalanche strategy, savings goals, retirement planning basics, and " +
            "investment options education. IMPORTANT: This is financial education, not advice. Recommend a " +
            "registered financial planner for personalised investment advice. Compliance: FAIS Act, NCA, POPIA."

    val complianceFlags: List<String> = listOf("FAIS_Act_37_2002", "NCA", "POPIA", "Not_Financial_Advice")

    val suggestedTools: List<String> = listOf("budget_tracker", "spreadsheet", "calculator", "web_search")
}

// =====================================================================
// CompanionAdapter (PersonalFinanceCompanionAdapter.cs)
// =====================================================================

/**
 * Wraps an [ICompanionSession] with the Personal.Finance snippet + helpers.
 * Mirrors C# `PersonalFinanceCompanionAdapter`.
 */
class PersonalFinanceCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${PersonalFinanceDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun buildBudgetAsync(income: String, expenses: String): String =
        inner.agentAsync("Build a monthly budget. Income: $income. Expenses: $expenses. Apply the 50/30/20 rule, identify savings opportunities, and flag over-spending categories.")

    suspend fun createDebtPlanAsync(debts: String): String =
        inner.agentAsync("Create a debt elimination plan using the avalanche method (highest interest first):\n$debts\nShow monthly payment schedule, total interest saved, and debt-free date.")

    suspend fun analyseSpendingAsync(categoryBreakdown: String, monthlyIncome: String): String =
        inner.agentAsync("Analyse spending $categoryBreakdown against income $monthlyIncome. Identify 2 leaks + a realistic redirect target.")

    suspend fun designSavingsGoalAsync(goal: String, targetAmount: BigDecimal, monthsAvailable: Int): String =
        inner.agentAsync("Plan to save $targetAmount for '$goal' in $monthsAvailable months. Monthly target + behavioural commitment device.")

    suspend fun explainTaxImpactAsync(scenario: String, jurisdiction: String): String =
        inner.agentAsync("Explain tax impact of: $scenario in $jurisdiction. Likely treatment, paperwork, optimisation lever. Not tax advice.")

    suspend fun reviewInvestmentMixAsync(portfolio: String, riskAppetite: String, horizonYears: Int): String =
        inner.agentAsync("Review investment mix: $portfolio against $riskAppetite appetite, $horizonYears-year horizon. Coverage, concentration, fee drag.")
}
