// Civic.kt
//
// Kotlin port of CircleAI.Civic (CivicPrimitives.cs + CivicDomainContext.cs +
// CivicCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory civic board: issues, representatives, and events.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTimeOffset` -> `Instant`.
//   * `Resolve` overwrites Status (unknown issue throws).
//   * `OpenIssues` = issues whose Status is not "Resolved" (case-insensitive).
//   * `RepsForDistrict` case-insensitive match on the (nullable) District.
//   * `UpcomingEvents` = future events (UTC now), ASC.

package com.bhengubv.circleai.civic

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (CivicPrimitives.cs)
// =====================================================================

/** A reported civic issue. Mirrors C# `CivicIssue`. */
data class CivicIssue(
    val issueId: String,
    val category: String,
    val description: String,
    val lat: Double,
    val lon: Double,
    val reportedUtc: Instant,
    val status: String,
)

/** An elected representative. Mirrors C# `Representative`. */
data class Representative(val repId: String, val name: String, val office: String, val contactEmail: String, val district: String?)

/** A civic event. Mirrors C# `CivicEvent`. */
data class CivicEvent(val eventId: String, val title: String, val atUtc: Instant, val location: String, val audience: String)

/** Deterministic civic board. Mirrors C# `ICivicBoard`. */
interface ICivicBoard {
    fun report(i: CivicIssue)
    fun resolve(issueId: String, status: String)
    fun openIssues(): List<CivicIssue>
    fun addRep(r: Representative)
    fun repsForDistrict(district: String): List<Representative>
    fun schedule(e: CivicEvent)
    fun upcomingEvents(): List<CivicEvent>
}

/** In-memory [ICivicBoard]. Mirrors C# `InMemoryCivicBoard`. */
class InMemoryCivicBoard : ICivicBoard {
    private val issues = ConcurrentHashMap<String, CivicIssue>()
    private val reps = ConcurrentHashMap<String, Representative>()
    private val events = ConcurrentHashMap<String, CivicEvent>()

    override fun report(i: CivicIssue) { issues[i.issueId] = i }

    override fun resolve(issueId: String, status: String) {
        val i = issues[issueId] ?: throw IllegalStateException("Unknown issue $issueId")
        issues[issueId] = i.copy(status = status)
    }

    override fun openIssues(): List<CivicIssue> =
        issues.values.filter { !it.status.equals("Resolved", ignoreCase = true) }

    override fun addRep(r: Representative) { reps[r.repId] = r }
    override fun repsForDistrict(district: String): List<Representative> =
        reps.values.filter { it.district.equals(district, ignoreCase = true) }

    override fun schedule(e: CivicEvent) { events[e.eventId] = e }
    override fun upcomingEvents(): List<CivicEvent> {
        val now = Instant.now()
        return events.values.filter { !it.atUtc.isBefore(now) }.sortedBy { it.atUtc }
    }
}

// =====================================================================
// DomainContext (CivicDomainContext.cs)
// =====================================================================

/** Static domain context for Civic. Mirrors C# `CivicDomainContext`. */
object CivicDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Civic] Expert in civic rights and government services. Help citizens navigate municipal " +
            "processes, permit applications, public participation, service delivery queries, and " +
            "constitutional rights. Explain bureaucratic processes in plain language. Compliance: PAJA, PAIA, " +
            "Constitution of SA, Municipal Systems Act."

    val complianceFlags: List<String> = listOf("PAJA", "PAIA", "Constitution_RSA", "Municipal_Systems_Act", "POPIA")

    val suggestedTools: List<String> = listOf("government_portals", "document_editor", "map", "web_search")
}

// =====================================================================
// CompanionAdapter (CivicCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Civic snippet + helpers. Mirrors C# `CivicCompanionAdapter`. */
class CivicCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${CivicDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun explainPermitProcessAsync(permitType: String, municipality: String): String =
        inner.agentAsync("Explain the application process for a $permitType permit in $municipality. Include required documents, fees, timelines, and escalation steps.")

    suspend fun draftObjectionAsync(issue: String, authority: String): String =
        inner.agentAsync("Draft a formal objection letter regarding: $issue. Addressed to: $authority. Cite relevant rights under PAJA and request a formal response within the prescribed 90 days.")

    suspend fun draftPetitionAsync(issue: String, targetOffice: String, signatureGoal: Int): String =
        inner.agentAsync("Draft a clear, factual petition on '$issue' to $targetOffice, targeting $signatureGoal signatures. Include problem, ask, evidence, signature ask.")

    suspend fun logServiceFailureAsync(serviceName: String, location: String, failureDescription: String): String =
        inner.agentAsync("Compose a service-failure report for $serviceName at $location: $failureDescription. Format for municipal ticketing systems.")

    suspend fun explainPolicyAsync(policyName: String, audience: String): String =
        inner.agentAsync("Explain '$policyName' to a $audience. Cover what it does, who's affected, and what to do if it affects you.")

    suspend fun prepareCouncilQuestionsAsync(topic: String, questionCount: Int): String =
        inner.agentAsync("Prepare $questionCount pointed questions for council on $topic. Each should be specific, evidence-based, and require a substantive answer.")
}
