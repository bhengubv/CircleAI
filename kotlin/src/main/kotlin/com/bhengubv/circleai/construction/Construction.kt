// Construction.kt
//
// Kotlin port of CircleAI.Construction (ConstructionPrimitives.cs +
// ConstructionDomainContext.cs + ConstructionCompanionAdapter.cs) — the C#
// reference is the EXACT spec. A deterministic in-memory construction board:
// projects, tasks, and cost entries.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `decimal` -> `BigDecimal`;
//     `DateTime`/`DateTime?`/`DateTimeOffset` -> `Instant`/`Instant?`/`Instant`.
//   * `Complete` flips the task's Completed flag (unknown task throws).
//   * `OpenConstructionTasksFor` = incomplete tasks for the project, ASC by DueOn.
//   * `SpendFor` sums cost entries; `RemainingBudget` = budget − spend
//     (unknown project throws).

package com.bhengubv.circleai.construction

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
// Primitives (ConstructionPrimitives.cs)
// =====================================================================

/** A construction project. Mirrors C# `Project`. */
data class Project(val projectId: String, val name: String, val startOn: Instant, val endOn: Instant?, val budget: BigDecimal, val currency: String)

/** A construction task. Mirrors C# `ConstructionTask`. */
data class ConstructionTask(val constructionTaskId: String, val projectId: String, val description: String, val dueOn: Instant, val completed: Boolean)

/** A cost entry. Mirrors C# `CostEntry`. */
data class CostEntry(val entryId: String, val projectId: String, val category: String, val amount: BigDecimal, val atUtc: Instant)

/** Deterministic construction board. Mirrors C# `IConstructionBoard`. */
interface IConstructionBoard {
    fun create(p: Project)
    fun getProject(id: String): Project?
    fun add(t: ConstructionTask)
    fun complete(taskId: String)
    fun openConstructionTasksFor(projectId: String): List<ConstructionTask>
    fun recordCost(c: CostEntry)
    fun spendFor(projectId: String): BigDecimal
    fun remainingBudget(projectId: String): BigDecimal
}

/** In-memory [IConstructionBoard]. Mirrors C# `InMemoryConstructionBoard`. */
class InMemoryConstructionBoard : IConstructionBoard {
    private val projects = ConcurrentHashMap<String, Project>()
    private val tasks = ConcurrentHashMap<String, ConstructionTask>()
    private val costs = mutableListOf<CostEntry>()
    private val lock = Any()

    override fun create(p: Project) { projects[p.projectId] = p }
    override fun getProject(id: String): Project? = projects[id]
    override fun add(t: ConstructionTask) { tasks[t.constructionTaskId] = t }

    override fun complete(taskId: String) {
        val t = tasks[taskId] ?: throw IllegalStateException("Unknown task $taskId")
        tasks[taskId] = t.copy(completed = true)
    }

    override fun openConstructionTasksFor(projectId: String): List<ConstructionTask> =
        tasks.values.filter { it.projectId == projectId && !it.completed }.sortedBy { it.dueOn }

    override fun recordCost(c: CostEntry) { synchronized(lock) { costs.add(c) } }
    override fun spendFor(projectId: String): BigDecimal = synchronized(lock) {
        costs.filter { it.projectId == projectId }.fold(BigDecimal.ZERO) { acc, c -> acc + c.amount }
    }

    override fun remainingBudget(projectId: String): BigDecimal {
        val p = projects[projectId] ?: throw IllegalStateException("Unknown project $projectId")
        return p.budget - spendFor(projectId)
    }
}

// =====================================================================
// DomainContext (ConstructionDomainContext.cs)
// =====================================================================

/** Static domain context for Construction. Mirrors C# `ConstructionDomainContext`. */
object ConstructionDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Construction] Expert construction project management assistant. Help with BOQ preparation, " +
            "programme of works, site safety plans, NHBRC compliance, subcontractor management, and defect " +
            "liability. Apply NEC/JBCC contract principles. Compliance: OHS Act, NHBRC Act, CIDB Act, ECSA, " +
            "National Building Regulations."

    val complianceFlags: List<String> = listOf("OHS_Act", "NHBRC_Act", "CIDB_Act", "National_Building_Regs", "POPIA")

    val suggestedTools: List<String> = listOf("project_scheduler", "document_editor", "map", "analytics")
}

// =====================================================================
// CompanionAdapter (ConstructionCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Construction snippet + helpers. Mirrors C# `ConstructionCompanionAdapter`. */
class ConstructionCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${ConstructionDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun draftSafetyPlanAsync(projectType: String, risks: String): String =
        inner.agentAsync("Draft an OHS Act-compliant safety plan for a $projectType project. Key risks: $risks. Include risk assessment, control measures, emergency procedures, and competency requirements.")

    suspend fun prepareBoqAsync(scope: String): String =
        inner.agentAsync("Prepare a Bill of Quantities structure for: $scope. Include trade sections, measurement units, and provisional sums guidance per ASAQS standards.")

    suspend fun estimateCostAsync(scope: String, areaM2: Double, finishLevel: String): String =
        inner.agentAsync("Estimate cost for ${areaM2}m² of $scope, finish level $finishLevel. Break by trade, contingency 10%, exclusions.")

    suspend fun generateSafetyToolboxAsync(activity: String, siteHazards: String): String =
        inner.agentAsync("Generate a toolbox talk for '$activity' with hazards: $siteHazards. Format: hazards, controls, PPE, sign-off.")

    suspend fun sequenceCriticalPathAsync(projectScope: String, durationDays: Int): String =
        inner.agentAsync("Sequence the critical path for: $projectScope in $durationDays days. List tasks, dependencies, slack, and 2 risks per phase.")

    suspend fun draftSnagListAsync(area: String, observations: String): String =
        inner.agentAsync("Draft a snag list for $area. Observations: $observations. Order by trade, severity, and access requirement.")
}
