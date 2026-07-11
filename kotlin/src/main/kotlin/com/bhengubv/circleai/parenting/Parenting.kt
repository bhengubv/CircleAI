// Parenting.kt
//
// Kotlin port of CircleAI.Parenting (ParentingPrimitives.cs +
// ParentingDomainContext.cs + ParentingCompanionAdapter.cs) — the C#
// reference is the EXACT spec. A deterministic in-memory parenting board:
// children, milestones, and school-day routines.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTime`/`DateTimeOffset` -> `Instant`;
//     `DayOfWeek` -> `java.time.DayOfWeek`; `TimeSpan` -> `java.time.Duration`.
//   * `Children` orders by Name ASC.
//   * `RecordMilestone` requires ChildId, appends to a per-child list.
//   * `MilestonesFor` returns newest-first (by AchievedAtUtc DESC).
//   * `SetRoutine`/`GetRoutine` key "{childId}/{dow}".
//   * `AgeAsOf` throws on unknown child; returns `at - DateOfBirth`.

package com.bhengubv.circleai.parenting

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Duration
import java.time.DayOfWeek
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (ParentingPrimitives.cs)
// =====================================================================

/** A child. Mirrors C# `Child`. */
data class Child(val childId: String, val name: String, val dateOfBirth: Instant, val gender: String?)

/** A developmental milestone. Mirrors C# `Milestone`. */
data class Milestone(
    val milestoneId: String,
    val childId: String,
    val category: String,
    val description: String,
    val achievedAtUtc: Instant,
)

/** One entry in a daily routine. Mirrors C# `RoutineEntry`. */
data class RoutineEntry(val time: String, val activity: String)

/** A day-of-week routine for a child. Mirrors C# `Routine`. */
data class Routine(val childId: String, val dayOfWeek: DayOfWeek, val entries: List<RoutineEntry>)

/** Deterministic parenting board. Mirrors C# `IParentingBoard`. */
interface IParentingBoard {
    fun addChild(c: Child)
    fun getChild(id: String): Child?
    val children: List<Child>
    fun recordMilestone(m: Milestone)
    fun milestonesFor(childId: String): List<Milestone>
    fun setRoutine(r: Routine)
    fun getRoutine(childId: String, dow: DayOfWeek): Routine?
    fun ageAsOf(childId: String, at: Instant): Duration
}

/** In-memory [IParentingBoard]. Mirrors C# `InMemoryParentingBoard`. */
class InMemoryParentingBoard : IParentingBoard {
    private val children_ = ConcurrentHashMap<String, Child>()
    private val milestones = ConcurrentHashMap<String, MutableList<Milestone>>()
    private val routines = ConcurrentHashMap<String, Routine>()
    private val lock = Any()

    override fun addChild(c: Child) { children_[c.childId] = c }
    override fun getChild(id: String): Child? = children_[id]
    override val children: List<Child>
        get() = children_.values.sortedBy { it.name }

    override fun recordMilestone(m: Milestone) {
        if (m.childId.isBlank()) throw IllegalArgumentException("ChildId required")
        synchronized(lock) {
            milestones.getOrPut(m.childId) { mutableListOf() }.add(m)
        }
    }

    override fun milestonesFor(childId: String): List<Milestone> = synchronized(lock) {
        val list = milestones[childId] ?: return emptyList()
        list.sortedByDescending { it.achievedAtUtc }
    }

    override fun setRoutine(r: Routine) {
        routines[key(r.childId, r.dayOfWeek)] = r
    }

    override fun getRoutine(childId: String, dow: DayOfWeek): Routine? = routines[key(childId, dow)]

    override fun ageAsOf(childId: String, at: Instant): Duration {
        val c = children_[childId] ?: throw IllegalStateException("Unknown child $childId")
        return Duration.between(c.dateOfBirth, at)
    }

    private fun key(childId: String, d: DayOfWeek): String = "$childId/$d"
}

// =====================================================================
// DomainContext (ParentingDomainContext.cs)
// =====================================================================

/** Static domain context for Parenting. Mirrors C# `ParentingDomainContext`. */
object ParentingDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Parenting] Supportive parenting companion. Offer evidence-based parenting strategies " +
            "(positive discipline, attachment, development milestones), school communication guidance, and " +
            "family wellbeing tips. Acknowledge the difficulty of parenting without judgment. Compliance: " +
            "Children's Act 38/2005, POPIA."

    val complianceFlags: List<String> = listOf("Childrens_Act_38_2005", "POPIA")

    val suggestedTools: List<String> = listOf("development_tracker", "document_editor", "web_search", "calendar")
}

// =====================================================================
// CompanionAdapter (ParentingCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Parenting snippet + helpers. Mirrors C# `ParentingCompanionAdapter`. */
class ParentingCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${ParentingDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun adviseOnBehaviourAsync(childAge: String, behaviour: String, context: String): String =
        inner.agentAsync("Advise on managing this behaviour in a $childAge-year-old: $behaviour. Context: $context. Use positive discipline principles and suggest age-appropriate strategies.")

    suspend fun draftSchoolEmailAsync(purpose: String, teacherName: String): String =
        inner.agentAsync("Draft a professional, respectful email to teacher $teacherName regarding: $purpose. Balance parental advocacy with collaborative tone.")

    suspend fun respondToBehaviourAsync(childAge: String, behaviour: String, context: String): String =
        inner.agentAsync("Respond to $childAge-year-old $behaviour in context: $context. Provide a calm script + the developmental rationale.")

    suspend fun designRoutineAsync(childAge: String, targetWindow: String): String =
        inner.agentAsync("Design a $targetWindow routine for a $childAge-year-old. Cover transitions, sensory needs, choice points.")

    suspend fun milestoneCheckInAsync(childAge: String, observations: String): String =
        inner.agentAsync("Sanity-check milestones for $childAge: $observations. Flag what's normal-range vs worth-discussing-with-pediatrician.")

    suspend fun prepareSchoolConferenceAsync(childName: String, grade: String, concerns: String): String =
        inner.agentAsync("Prepare $childName's parent-teacher conference ($grade). Concerns: $concerns. Draft questions + advocacy points.")
}
