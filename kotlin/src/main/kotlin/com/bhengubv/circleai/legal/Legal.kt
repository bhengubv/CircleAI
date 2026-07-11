// Legal.kt
//
// Kotlin port of CircleAI.Legal (LegalPrimitives.cs + LegalDomainContext.cs +
// LegalCompanionAdapter.cs) — the C# reference is the EXACT spec. Real domain
// types + in-memory store for the Legal vertical: matters, contracts,
// deadlines, and a clause library.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `DateTimeOffset` (Matter.OpenedAtUtc) -> `java.time.Instant`.
//   * C# `DateTime` (contract/deadline dates) -> `java.time.LocalDate`.
//   * C# `IReadOnlyList<string>` (Counterparties, Tags) -> `List<String>`.
//   * C# `ConcurrentDictionary<string,_>` (Ordinal) -> `ConcurrentHashMap`.
//   * `ActiveMatters` filters Open==true, orders by OpenedAtUtc DESC.
//   * `ContractsExpiringBefore` keeps contracts whose ExpiryDate is set and <=
//     the cutoff, ordered by ExpiryDate ASC.
//   * `UpcomingDeadlines` keeps DueOn >= now, ordered ASC.
//   * `ClausesByTag` rejects a blank tag and matches tags case-insensitively.

package com.bhengubv.circleai.legal

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.time.LocalDate
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (LegalPrimitives.cs)
// =====================================================================

/** A legal matter/case. Mirrors C# `Matter`. */
data class Matter(
    val matterId: String,
    val title: String,
    val jurisdiction: String,
    val client: String,
    val openedAtUtc: Instant,
    val open: Boolean,
)

/** A contract tied to a matter. Mirrors C# `Contract`. */
data class Contract(
    val contractId: String,
    val matterId: String,
    val title: String,
    val effectiveDate: LocalDate,
    val expiryDate: LocalDate?,
    val counterparties: List<String>,
)

/** A calendared legal deadline. Mirrors C# `LegalDeadline`. */
data class LegalDeadline(
    val deadlineId: String,
    val matterId: String,
    val description: String,
    val dueOn: LocalDate,
)

/** A reusable clause. Mirrors C# `Clause`. */
data class Clause(
    val clauseId: String,
    val title: String,
    val body: String,
    val tags: List<String>,
)

/** Deterministic legal-practice board. Mirrors C# `ILegalBoard`. */
interface ILegalBoard {
    fun open(m: Matter)
    fun close(matterId: String)
    fun getMatter(id: String): Matter?
    val activeMatters: List<Matter>
    fun addContract(c: Contract)
    fun contractsExpiringBefore(date: LocalDate): List<Contract>
    fun add(d: LegalDeadline)
    fun upcomingDeadlines(now: LocalDate): List<LegalDeadline>
    fun addClause(c: Clause)
    fun clausesByTag(tag: String): List<Clause>
}

/** In-memory [ILegalBoard]. Mirrors C# `InMemoryLegalBoard`. */
class InMemoryLegalBoard : ILegalBoard {
    private val matters = ConcurrentHashMap<String, Matter>()
    private val contracts = ConcurrentHashMap<String, Contract>()
    private val deadlines = ConcurrentHashMap<String, LegalDeadline>()
    private val clauses = ConcurrentHashMap<String, Clause>()

    override fun open(m: Matter) { matters[m.matterId] = m }

    override fun close(matterId: String) {
        val m = matters[matterId] ?: throw IllegalStateException("Unknown matter $matterId")
        matters[matterId] = m.copy(open = false)
    }

    override fun getMatter(id: String): Matter? = matters[id]

    override val activeMatters: List<Matter>
        get() = matters.values.filter { it.open }.sortedByDescending { it.openedAtUtc }

    override fun addContract(c: Contract) { contracts[c.contractId] = c }

    override fun contractsExpiringBefore(date: LocalDate): List<Contract> =
        contracts.values
            .filter { it.expiryDate != null && it.expiryDate <= date }
            .sortedBy { it.expiryDate }

    override fun add(d: LegalDeadline) { deadlines[d.deadlineId] = d }

    override fun upcomingDeadlines(now: LocalDate): List<LegalDeadline> =
        deadlines.values.filter { it.dueOn >= now }.sortedBy { it.dueOn }

    override fun addClause(c: Clause) { clauses[c.clauseId] = c }

    override fun clausesByTag(tag: String): List<Clause> {
        require(tag.isNotBlank()) { "tag required" }
        return clauses.values.filter { c -> c.tags.any { it.equals(tag, ignoreCase = true) } }
    }
}

// =====================================================================
// DomainContext (LegalDomainContext.cs)
// =====================================================================

/** Static domain context for the Legal vertical. Mirrors C# `LegalDomainContext`. */
object LegalDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Legal] You are a legal knowledge and compliance assistant. Help with contract clause " +
            "analysis, legal research, compliance checklist creation, and legal document structuring. " +
            "IMPORTANT: This is not legal advice. Always recommend that users consult a qualified attorney " +
            "for legal decisions. Compliance: Legal Practice Act, LPA 28/2014, Attorneys Act, POPIA."

    val complianceFlags: List<String> =
        listOf("Legal_Practice_Act_28_2014", "Attorneys_Act", "POPIA", "Professional_Legal_Privilege")

    val suggestedTools: List<String> =
        listOf("legal_research", "document_editor", "contract_analyser")
}

// =====================================================================
// CompanionAdapter (LegalCompanionAdapter.cs)
// =====================================================================

/**
 * Wraps an [ICompanionSession] with the Legal domain snippet + domain helpers.
 * Mirrors C# `LegalCompanionAdapter`.
 */
class LegalCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${LegalDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun reviewContractClausesAsync(contractText: String, focusArea: String): String =
        inner.agentAsync("Review the following contract for $focusArea issues. Identify risky clauses, missing protections, and suggest improvements:\n$contractText")

    suspend fun draftContractSummaryAsync(contractText: String): String =
        inner.agentAsync("Summarise this contract in plain language. Highlight key obligations, payment terms, IP ownership, termination, and dispute resolution:\n$contractText")

    suspend fun generateComplianceChecklistAsync(businessType: String, jurisdiction: String): String =
        inner.agentAsync("Generate a compliance checklist for a $businessType operating in $jurisdiction. Cover company registration, tax, labour, data protection, and sector-specific regulations.")

    suspend fun summariseContractAsync(contractText: String, clientRole: String): String =
        inner.agentAsync("Summarise this contract from the $clientRole's perspective: $contractText. Highlight obligations, rights, risks, deadlines.")

    suspend fun draftClauseAsync(clauseType: String, position: String, jurisdiction: String): String =
        inner.agentAsync("Draft a $clauseType clause favouring the $position in $jurisdiction. Plain-English notes alongside.")

    suspend fun assessMatterStrengthAsync(matterSummary: String): String =
        inner.agentAsync("Assess this matter's merits: $matterSummary. Cover liability theory, likely defences, evidence gaps, settlement range. Not legal advice.")

    suspend fun trackDeadlineAsync(matterType: String, keyDate: String, jurisdiction: String): String =
        inner.agentAsync("Identify all deadlines triggered by $keyDate for a $matterType matter in $jurisdiction. List date, action, statute reference.")
}
