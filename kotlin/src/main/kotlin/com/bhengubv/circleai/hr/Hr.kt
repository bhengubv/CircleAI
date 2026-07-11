// Hr.kt
//
// Kotlin port of CircleAI.HR (HRPrimitives.cs + HRDomainContext.cs +
// HRCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory HR board: employees, leave requests, and
// performance reviews, plus the domain context + companion adapter.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; C# `DateTime`/`DateTimeOffset` -> `Instant`.
//   * C# `decimal` -> `java.math.BigDecimal`.
//   * `Employees` orders by Name ASC; `PendingLeaves` filters Status == "Pending"
//     (OrdinalIgnoreCase); `DecideLeave` throws on unknown request.
//   * `AvgRatingFor` averages ratings for the employee, 0 when none (parity with
//     `DefaultIfEmpty(0).Average()`).

package com.bhengubv.circleai.hr

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
// Primitives (HRPrimitives.cs)
// =====================================================================

/** An employee. Mirrors C# `Employee`. */
data class Employee(
    val employeeId: String,
    val name: String,
    val role: String,
    val hiredOn: Instant,
    val salary: BigDecimal,
    val currency: String,
)

/** A leave request. Mirrors C# `LeaveRequest`. */
data class LeaveRequest(
    val requestId: String,
    val employeeId: String,
    val kind: String,
    val from: Instant,
    val to: Instant,
    val status: String,
)

/** A performance review. Mirrors C# `PerformanceReview`. */
data class PerformanceReview(
    val reviewId: String,
    val employeeId: String,
    val reviewedOn: Instant,
    val ratingOutOf5: Int,
    val notes: String,
)

/** Deterministic HR board. Mirrors C# `IHRBoard`. */
interface IHRBoard {
    fun hire(e: Employee)
    fun getEmployee(id: String): Employee?
    val employees: List<Employee>
    fun request(r: LeaveRequest)
    fun decideLeave(requestId: String, decision: String)
    fun pendingLeaves(): List<LeaveRequest>
    fun review(r: PerformanceReview)
    fun avgRatingFor(employeeId: String): Double
}

/** In-memory [IHRBoard]. Mirrors C# `InMemoryHRBoard`. */
class InMemoryHRBoard : IHRBoard {
    private val employees_ = ConcurrentHashMap<String, Employee>()
    private val leaves = ConcurrentHashMap<String, LeaveRequest>()
    private val reviews = mutableListOf<PerformanceReview>()
    private val lock = Any()

    override fun hire(e: Employee) { employees_[e.employeeId] = e }
    override fun getEmployee(id: String): Employee? = employees_[id]
    override val employees: List<Employee>
        get() = employees_.values.sortedBy { it.name }

    override fun request(r: LeaveRequest) { leaves[r.requestId] = r }

    override fun decideLeave(requestId: String, decision: String) {
        val r = leaves[requestId] ?: throw IllegalStateException("Unknown leave request $requestId")
        leaves[requestId] = r.copy(status = decision)
    }

    override fun pendingLeaves(): List<LeaveRequest> =
        leaves.values.filter { it.status.equals("Pending", ignoreCase = true) }

    override fun review(r: PerformanceReview) { synchronized(lock) { reviews.add(r) } }

    override fun avgRatingFor(employeeId: String): Double = synchronized(lock) {
        val ratings = reviews.filter { it.employeeId == employeeId }.map { it.ratingOutOf5.toDouble() }
        if (ratings.isEmpty()) 0.0 else ratings.average()
    }
}

// =====================================================================
// DomainContext (HRDomainContext.cs)
// =====================================================================

/** Static domain context for HR. Mirrors C# `HRDomainContext`. */
object HRDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: HR] You are a human resources expert. Help with job description drafting, interview " +
            "frameworks, performance review templates, disciplinary procedures, leave management, and people " +
            "analytics. Apply South African labour law principles. Compliance: Labour Relations Act 66/1995, " +
            "BCEA, EEA, Skills Development Act, POPIA."

    val complianceFlags: List<String> = listOf("LRA_66_1995", "BCEA", "EEA", "Skills_Development_Act", "POPIA")

    val suggestedTools: List<String> = listOf("hris", "document_editor", "analytics", "job_boards")
}

// =====================================================================
// CompanionAdapter (HRCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the HR snippet + helpers. Mirrors C# `HRCompanionAdapter`. */
class HRCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${HRDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun draftJobDescriptionAsync(role: String, requirements: String): String =
        inner.agentAsync("Draft a compelling, legally compliant job description for: $role. Requirements: $requirements. Include purpose, responsibilities, qualifications, and EEA statement.")

    suspend fun generatePerformanceReviewAsync(employeeName: String, role: String, achievements: String): String =
        inner.agentAsync("Generate a structured performance review for $employeeName ($role). Achievements: $achievements. Include ratings, development areas, and SMART goals.")

    suspend fun adviseOnDisciplinaryAsync(misconduct: String, employeeHistory: String): String =
        inner.agentAsync("Advise on disciplinary action for: $misconduct. Employee history: $employeeHistory. Apply LRA progressive discipline principles and recommend appropriate sanction.")

    suspend fun draftJobDescriptionAsync(roleTitle: String, seniority: String, mustHaves: String): String =
        inner.agentAsync("Draft a job description for $seniority $roleTitle. Must-haves: $mustHaves. Inclusive language, outcomes-led not task-list.")

    suspend fun structureInterviewLoopAsync(role: String, hoursAvailable: Int): String =
        inner.agentAsync("Structure an interview loop for $role in $hoursAvailable hours. Map each stage to a competency, name the evaluator role.")

    suspend fun writePerformanceFeedbackAsync(employeeName: String, strengths: String, growthAreas: String): String =
        inner.agentAsync("Write performance feedback for $employeeName. Strengths: $strengths. Growth: $growthAreas. SBI format, specific, future-focused.")

    suspend fun handleSensitiveHrIssueAsync(situation: String, jurisdiction: String): String =
        inner.agentAsync("Suggest first-response plan for HR situation: $situation in $jurisdiction. Cover legal hold, witness, documentation, escalation path.")
}
