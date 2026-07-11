// Elderly.kt
//
// Kotlin port of CircleAI.Elderly (ElderlyPrimitives.cs +
// ElderlyDomainContext.cs + ElderlyCompanionAdapter.cs) — the C# reference
// is the EXACT spec. A deterministic in-memory elderly-care board: care
// plans, medication reminders, and check-ins.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `TimeSpan` -> `java.time.Duration`;
//     `DateTimeOffset` -> `Instant`.
//   * Plans keyed by ResidentName; reminders by ReminderId.
//   * `DeactivateReminder` throws on unknown; flips Active false.
//   * `ActiveRemindersFor` filters reminders by resident + Active.
//   * `LatestCheckIn` returns the newest check-in for the resident, null when none.
//   * `MissedCheckIn` is true when there is no check-in or the latest predates `since`.

package com.bhengubv.circleai.elderly

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (ElderlyPrimitives.cs)
// =====================================================================

/** A resident care plan. Mirrors C# `CarePlan`. */
data class CarePlan(
    val planId: String,
    val residentName: String,
    val medicalConditions: List<String>,
    val allergies: List<String>,
    val carerNotes: String,
)

/** A daily medication reminder. Mirrors C# `MedReminder`. */
data class MedReminder(
    val reminderId: String,
    val residentName: String,
    val medication: String,
    val dailyAt: Duration,
    val active: Boolean,
)

/** A resident check-in. Mirrors C# `CheckIn`. */
data class CheckIn(
    val checkInId: String,
    val residentName: String,
    val atUtc: Instant,
    val status: String,
    val note: String?,
)

/** Deterministic elderly-care board. Mirrors C# `IElderlyCareBoard`. */
interface IElderlyCareBoard {
    fun setPlan(p: CarePlan)
    fun getPlan(resident: String): CarePlan?
    fun addReminder(r: MedReminder)
    fun deactivateReminder(reminderId: String)
    fun activeRemindersFor(resident: String): List<MedReminder>
    fun recordCheckIn(c: CheckIn)
    fun latestCheckIn(resident: String): CheckIn?
    fun missedCheckIn(resident: String, since: Instant): Boolean
}

/** In-memory [IElderlyCareBoard]. Mirrors C# `InMemoryElderlyCareBoard`. */
class InMemoryElderlyCareBoard : IElderlyCareBoard {
    private val plans = ConcurrentHashMap<String, CarePlan>()
    private val reminders = ConcurrentHashMap<String, MedReminder>()
    private val checkIns = mutableListOf<CheckIn>()
    private val lock = Any()

    override fun setPlan(p: CarePlan) { plans[p.residentName] = p }
    override fun getPlan(resident: String): CarePlan? = plans[resident]

    override fun addReminder(r: MedReminder) { reminders[r.reminderId] = r }
    override fun deactivateReminder(reminderId: String) {
        val r = reminders[reminderId] ?: throw IllegalStateException("Unknown reminder $reminderId")
        reminders[reminderId] = r.copy(active = false)
    }

    override fun activeRemindersFor(resident: String): List<MedReminder> =
        reminders.values.filter { it.residentName == resident && it.active }

    override fun recordCheckIn(c: CheckIn) { synchronized(lock) { checkIns.add(c) } }
    override fun latestCheckIn(resident: String): CheckIn? = synchronized(lock) {
        checkIns.filter { it.residentName == resident }.maxByOrNull { it.atUtc }
    }

    override fun missedCheckIn(resident: String, since: Instant): Boolean {
        val latest = latestCheckIn(resident)
        return latest == null || latest.atUtc.isBefore(since)
    }
}

// =====================================================================
// DomainContext (ElderlyDomainContext.cs)
// =====================================================================

/** Static domain context for Elderly care. Mirrors C# `ElderlyDomainContext`. */
object ElderlyDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Elderly] Compassionate care assistant for elderly persons and their caregivers. Help with " +
            "medication reminders, appointment management, benefit and pension queries, carer communication, " +
            "and social activity suggestions. Use clear, patient language. Compliance: Older Persons Act 13/2006, " +
            "POPIA, Social Assistance Act."

    val complianceFlags: List<String> = listOf("Older_Persons_Act_13_2006", "Social_Assistance_Act", "POPIA")

    val suggestedTools: List<String> = listOf("medication_reminder", "calendar", "web_search", "document_editor")
}

// =====================================================================
// CompanionAdapter (ElderlyCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Elderly snippet + helpers. Mirrors C# `ElderlyCompanionAdapter`. */
class ElderlyCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${ElderlyDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun createMedScheduleAsync(medications: String): String =
        inner.agentAsync("Create a clear, simple medication schedule for these prescriptions:\n$medications\nInclude time of day, food requirements, and what to do if a dose is missed.")

    suspend fun locateSupportAsync(need: String, location: String): String =
        inner.agentAsync("Find elderly support services for: $need in $location. Include government services, NGOs, and contact details.")

    suspend fun reviewMedicationListAsync(medicationList: String, conditions: String): String =
        inner.agentAsync("Review this medication list for $conditions: $medicationList. Flag potential interactions, redundancies, and timing issues. Defer prescribing to clinician.")

    suspend fun suggestFallPreventionAsync(livingArrangement: String, mobilityNotes: String): String =
        inner.agentAsync("Suggest fall-prevention measures for $livingArrangement. Mobility: $mobilityNotes. Cover home modifications, footwear, exercise, vision.")

    suspend fun draftCheckInPromptsAsync(residentName: String, interestProfile: String): String =
        inner.agentAsync("Draft 5 warm, dignified check-in conversation prompts for $residentName. Interests: $interestProfile. Avoid talk-down language.")

    suspend fun summariseCarerHandoverAsync(shiftNotes: String): String =
        inner.agentAsync("Summarise these shift notes for the next carer: $shiftNotes. SBAR format (Situation, Background, Assessment, Recommendation).")
}
