// Business.kt
//
// Kotlin port of CircleAI.Business (BusinessPrimitives.cs +
// BusinessDomainContext.cs + BusinessCompanionAdapter.cs) — the C#
// reference is the EXACT spec. A deterministic in-memory business board:
// a unit hierarchy, KPI samples, and quarter targets.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTimeOffset` -> `Instant`.
//   * `ChildrenOf` returns units whose ParentUnitId matches.
//   * `LatestKpi` returns the newest sample's value, `NaN` when none.
//   * `SetTarget` keys "{Unit}/{Metric}/{Year}Q{Quarter}".
//   * `TargetAchievement` = latestKpi / target; `NaN` when the target is
//     missing or zero.

package com.bhengubv.circleai.business

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
// Primitives (BusinessPrimitives.cs)
// =====================================================================

/** A business unit in the org hierarchy. Mirrors C# `BusinessUnit`. */
data class BusinessUnit(
    val unitId: String,
    val name: String,
    val parentUnitId: String,
    val kpiTags: List<String>,
)

/** A single KPI observation. Mirrors C# `KpiSample`. */
data class KpiSample(val unitId: String, val metric: String, val value: Double, val atUtc: Instant)

/** A per-quarter KPI target. Mirrors C# `QuarterTarget`. */
data class QuarterTarget(val unitId: String, val metric: String, val year: Int, val quarter: Int, val target: Double)

/** Deterministic business board. Mirrors C# `IBusinessBoard`. */
interface IBusinessBoard {
    fun add(u: BusinessUnit)
    fun getUnit(id: String): BusinessUnit?
    fun childrenOf(parentUnitId: String): List<BusinessUnit>
    fun record(s: KpiSample)
    fun latestKpi(unitId: String, metric: String): Double
    fun setTarget(t: QuarterTarget)
    fun targetAchievement(unitId: String, metric: String, year: Int, quarter: Int): Double
}

/** In-memory [IBusinessBoard]. Mirrors C# `InMemoryBusinessBoard`. */
class InMemoryBusinessBoard : IBusinessBoard {
    private val units = ConcurrentHashMap<String, BusinessUnit>()
    private val kpis = mutableListOf<KpiSample>()
    private val targets = ConcurrentHashMap<String, QuarterTarget>()
    private val lock = Any()

    override fun add(u: BusinessUnit) { units[u.unitId] = u }
    override fun getUnit(id: String): BusinessUnit? = units[id]
    override fun childrenOf(parentUnitId: String): List<BusinessUnit> =
        units.values.filter { it.parentUnitId == parentUnitId }

    override fun record(s: KpiSample) { synchronized(lock) { kpis.add(s) } }

    override fun latestKpi(unitId: String, metric: String): Double = synchronized(lock) {
        kpis.filter { it.unitId == unitId && it.metric == metric }
            .maxByOrNull { it.atUtc }?.value ?: Double.NaN
    }

    override fun setTarget(t: QuarterTarget) {
        targets["${t.unitId}/${t.metric}/${t.year}Q${t.quarter}"] = t
    }

    override fun targetAchievement(unitId: String, metric: String, year: Int, quarter: Int): Double {
        val key = "$unitId/$metric/${year}Q$quarter"
        val target = targets[key]
        if (target == null || target.target == 0.0) return Double.NaN
        return latestKpi(unitId, metric) / target.target
    }
}

// =====================================================================
// DomainContext (BusinessDomainContext.cs)
// =====================================================================

/** Static domain context for Business. Mirrors C# `BusinessDomainContext`. */
object BusinessDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Business] You are a business strategy and operations expert. Help with OKRs, strategic " +
            "planning, meeting facilitation, competitive analysis, and executive decision support. Structure " +
            "advice with clear options and trade-offs. Compliance: POPIA data handling, general commercial law."

    val complianceFlags: List<String> = listOf("POPIA", "Commercial_Law", "GDPR_aware")

    val suggestedTools: List<String> = listOf("calendar", "web_search", "document_editor", "task_manager")
}

// =====================================================================
// CompanionAdapter (BusinessCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Business snippet + helpers. Mirrors C# `BusinessCompanionAdapter`. */
class BusinessCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${BusinessDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun draftBusinessCaseAsync(proposal: String): String =
        inner.agentAsync("Draft a professional business case for: $proposal. Include: executive summary, problem statement, solution options, recommended approach, cost/benefit, timeline, and risks.")

    suspend fun summariseMeetingAsync(transcript: String): String =
        inner.agentAsync("Summarise this meeting transcript. Extract decisions, action items with owners, blockers, and next-meeting agenda.\n\nTranscript:\n$transcript")

    suspend fun generateOkrsAsync(companyContext: String, quarter: String): String =
        inner.agentAsync("Generate a set of OKRs for $quarter based on the following company context:\n$companyContext\nProvide 3-5 Objectives each with 2-4 measurable Key Results.")

    suspend fun draftOkrsForQuarterAsync(unitName: String, strategicTheme: String): String =
        inner.agentAsync("Draft 3 objectives × 3 key results for $unitName aligned to '$strategicTheme'. KRs must be measurable + time-bound.")

    suspend fun analyseUnitEconomicsAsync(productName: String, revenue: BigDecimal, cogs: BigDecimal, marketing: BigDecimal): String =
        inner.agentAsync("Analyse unit economics for $productName: revenue $revenue, COGS $cogs, marketing $marketing. Compute gross margin, LTV/CAC sanity, and 3 levers to improve.")

    suspend fun generateBoardUpdateAsync(quarter: String, wins: String, losses: String, asks: String): String =
        inner.agentAsync("Generate a 1-page board update for $quarter. Wins: $wins. Losses: $losses. Asks: $asks. Use Andy Grove-style brevity.")

    suspend fun suggestExperimentAsync(metric: String, currentValue: Double, targetValue: Double): String =
        inner.agentAsync("Suggest 3 experiments to move $metric from $currentValue to $targetValue. Score each by impact × confidence × cost.")
}
