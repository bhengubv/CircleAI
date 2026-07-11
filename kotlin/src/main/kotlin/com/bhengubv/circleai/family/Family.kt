// Family.kt
//
// Kotlin port of CircleAI.Family (FamilyPrimitives.cs +
// FamilyDomainContext.cs + FamilyCompanionAdapter.cs) — the C# reference
// is the EXACT spec. A deterministic in-memory family board: members,
// events, and shared expenses.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTime`/`DateTimeOffset` -> `Instant`;
//     `decimal` -> `BigDecimal`.
//   * `Members` orders by Name ASC.
//   * `EventsForMember` returns events whose MemberIds contains the id, ordered
//     by AtUtc ASC.
//   * `TotalPaidBy` sums expenses paid by the member with AtUtc >= since.
//   * `SpendByCategory` sums expenses whose category matches (OrdinalIgnoreCase)
//     with AtUtc >= since.

package com.bhengubv.circleai.family

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.math.BigDecimal
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (FamilyPrimitives.cs)
// =====================================================================

/** A family member. Mirrors C# `FamilyMember`. */
data class FamilyMember(val memberId: String, val name: String, val role: String, val dateOfBirth: Instant)

/** A shared family event. Mirrors C# `FamilyEvent`. */
data class FamilyEvent(val eventId: String, val title: String, val atUtc: Instant, val memberIds: List<String>)

/** A shared expense. Mirrors C# `SharedExpense`. */
data class SharedExpense(
    val expenseId: String,
    val paidById: String,
    val amount: BigDecimal,
    val currency: String,
    val category: String,
    val atUtc: Instant,
)

/** Deterministic family board. Mirrors C# `IFamilyBoard`. */
interface IFamilyBoard {
    fun add(m: FamilyMember)
    fun getMember(id: String): FamilyMember?
    val members: List<FamilyMember>
    fun schedule(e: FamilyEvent)
    fun eventsForMember(memberId: String): List<FamilyEvent>
    fun record(e: SharedExpense)
    fun totalPaidBy(memberId: String, since: Instant): BigDecimal
    fun spendByCategory(category: String, since: Instant): BigDecimal
}

/** In-memory [IFamilyBoard]. Mirrors C# `InMemoryFamilyBoard`. */
class InMemoryFamilyBoard : IFamilyBoard {
    private val members_ = ConcurrentHashMap<String, FamilyMember>()
    private val events = ConcurrentHashMap<String, FamilyEvent>()
    private val expenses = mutableListOf<SharedExpense>()
    private val lock = Any()

    override fun add(m: FamilyMember) { members_[m.memberId] = m }
    override fun getMember(id: String): FamilyMember? = members_[id]
    override val members: List<FamilyMember>
        get() = members_.values.sortedBy { it.name }

    override fun schedule(e: FamilyEvent) { events[e.eventId] = e }
    override fun eventsForMember(memberId: String): List<FamilyEvent> =
        events.values.filter { it.memberIds.contains(memberId) }.sortedBy { it.atUtc }

    override fun record(e: SharedExpense) { synchronized(lock) { expenses.add(e) } }

    override fun totalPaidBy(memberId: String, since: Instant): BigDecimal = synchronized(lock) {
        expenses.filter { it.paidById == memberId && !it.atUtc.isBefore(since) }
            .fold(BigDecimal.ZERO) { acc, e -> acc + e.amount }
    }

    override fun spendByCategory(category: String, since: Instant): BigDecimal = synchronized(lock) {
        expenses.filter { it.category.equals(category, ignoreCase = true) && !it.atUtc.isBefore(since) }
            .fold(BigDecimal.ZERO) { acc, e -> acc + e.amount }
    }
}

// =====================================================================
// DomainContext (FamilyDomainContext.cs)
// =====================================================================

/** Static domain context for Family. Mirrors C# `FamilyDomainContext`. */
object FamilyDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Family] Warm family life assistant. Help with shared calendar management, family budget " +
            "tracking, activity planning, milestone documentation, and family communication strategies. Respect " +
            "privacy boundaries — each family member's data is their own. Compliance: POPIA, Children's Act."

    val complianceFlags: List<String> = listOf("POPIA", "Childrens_Act_38_2005")

    val suggestedTools: List<String> = listOf("shared_calendar", "family_budget", "document_editor", "task_manager")
}

// =====================================================================
// CompanionAdapter (FamilyCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Family snippet + helpers. Mirrors C# `FamilyCompanionAdapter`. */
class FamilyCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${FamilyDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun planFamilyActivityAsync(ages: String, budget: String, interests: String): String =
        inner.agentAsync("Plan a family activity for children aged $ages. Budget: $budget. Interests: $interests. Include indoor and outdoor options with estimated cost and age-appropriateness.")

    suspend fun createFamilyBudgetAsync(income: String, expenses: String, goals: String): String =
        inner.agentAsync("Create a family budget. Combined income: $income. Expenses: $expenses. Goals: $goals. Allocate to categories and identify savings opportunities.")

    suspend fun planFamilyMealsAsync(familySize: String, dietaryNotes: String, daysCount: Int): String =
        inner.agentAsync("Plan $daysCount days of family meals for $familySize people, dietary notes: $dietaryNotes. Include shopping list grouped by aisle.")

    suspend fun mediateSiblingDisputeAsync(ages: String, dispute: String): String =
        inner.agentAsync("Mediate a sibling dispute between ages $ages: $dispute. Step-by-step script honouring each child's perspective.")

    suspend fun designHouseholdChoreRotaAsync(members: String, chores: String): String =
        inner.agentAsync("Design a fair, age-appropriate chore rota. Members: $members. Chores: $chores. Cover frequency and ownership.")

    suspend fun celebrateMilestoneAsync(milestone: String, memberName: String, budget: String): String =
        inner.agentAsync("Plan a $budget milestone celebration for $memberName: $milestone. Ideas across activity / food / memento / message.")
}
