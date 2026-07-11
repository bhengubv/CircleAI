// PersonalHealth.kt
//
// Kotlin port of CircleAI.Personal.Health (PersonalHealthPrimitives.cs +
// PersonalHealthDomainContext.cs + PersonalHealthCompanionAdapter.cs) — the C#
// reference is the EXACT spec. Deterministic in-memory personal-health store:
// vitals, allergies, medications. Instances are user-scoped.
//
// Fidelity notes:
//   * C# `enum VitalKind` -> Kotlin `enum class` (same members, same order).
//   * C# `record` -> Kotlin `data class`.
//   * C# `double` (VitalReading.Value) -> Kotlin `Double`.
//   * C# `DateTimeOffset` -> `java.time.Instant`.
//   * C# `ConcurrentDictionary<string,_>` (Ordinal) -> `ConcurrentHashMap`;
//     vitals live in a plain list behind a lock.
//   * `ReadSince` keeps kind matches with AtUtc >= since, ordered ASC.
//   * `Latest` returns the newest reading for a kind, or null.
//   * `EndMedication` throws on an unknown medication.
//   * `ActiveMedications` returns meds with no end date, ordered by Name ASC.

package com.bhengubv.circleai.personalhealth

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (PersonalHealthPrimitives.cs)
// =====================================================================

/** The kind of a vital reading. Mirrors C# `VitalKind` (members + order preserved). */
enum class VitalKind {
    BloodPressureSystolic,
    BloodPressureDiastolic,
    GlucoseMgDl,
    WeightKg,
    HeartRateBpm,
    TemperatureC,
    OxygenPct,
    StepsCount,
}

/** A single vital measurement. Mirrors C# `VitalReading`. */
data class VitalReading(val kind: VitalKind, val value: Double, val atUtc: Instant, val note: String?)

/** A recorded allergy. Mirrors C# `Allergy`. */
data class Allergy(val allergyId: String, val substance: String, val severity: String)

/** A medication (optionally ended). Mirrors C# `Medication`. */
data class Medication(
    val medId: String,
    val name: String,
    val dose: String,
    val frequency: String,
    val startedAtUtc: Instant,
    val endedAtUtc: Instant?,
)

/** Deterministic personal-health board. Mirrors C# `IPersonalHealthBoard`. */
interface IPersonalHealthBoard {
    fun record(v: VitalReading)
    fun readSince(kind: VitalKind, since: Instant): List<VitalReading>
    fun latest(kind: VitalKind): VitalReading?
    fun addAllergy(a: Allergy)
    val allergies: List<Allergy>
    fun addMedication(m: Medication)
    fun endMedication(medId: String, endedAtUtc: Instant)
    fun activeMedications(): List<Medication>
}

/** In-memory [IPersonalHealthBoard]. Mirrors C# `InMemoryPersonalHealthBoard`. */
class InMemoryPersonalHealthBoard : IPersonalHealthBoard {
    private val vitals = mutableListOf<VitalReading>()
    private val allergies_ = ConcurrentHashMap<String, Allergy>()
    private val meds = ConcurrentHashMap<String, Medication>()
    private val lock = Any()

    override fun record(v: VitalReading) { synchronized(lock) { vitals.add(v) } }

    override fun readSince(kind: VitalKind, since: Instant): List<VitalReading> = synchronized(lock) {
        vitals.filter { it.kind == kind && it.atUtc >= since }.sortedBy { it.atUtc }
    }

    override fun latest(kind: VitalKind): VitalReading? = synchronized(lock) {
        vitals.filter { it.kind == kind }.maxByOrNull { it.atUtc }
    }

    override fun addAllergy(a: Allergy) { allergies_[a.allergyId] = a }

    override val allergies: List<Allergy>
        get() = allergies_.values.toList()

    override fun addMedication(m: Medication) { meds[m.medId] = m }

    override fun endMedication(medId: String, endedAtUtc: Instant) {
        val m = meds[medId] ?: throw IllegalStateException("Unknown medication $medId")
        meds[medId] = m.copy(endedAtUtc = endedAtUtc)
    }

    override fun activeMedications(): List<Medication> =
        meds.values.filter { it.endedAtUtc == null }.sortedBy { it.name }
}

// =====================================================================
// DomainContext (PersonalHealthDomainContext.cs)
// =====================================================================

/** Static domain context for Personal.Health. Mirrors C# `PersonalHealthDomainContext`. */
object PersonalHealthDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Personal.Health] Personal health and wellness assistant. Help with symptom tracking, " +
            "appointment preparation, medication reminders, health goal setting, nutrition basics, and " +
            "health literacy. IMPORTANT: Always recommend consulting a qualified healthcare professional for " +
            "medical decisions. This is not medical advice. Compliance: POPIA, Health Professions Act."

    val complianceFlags: List<String> = listOf("POPIA", "Health_Professions_Act", "Not_Medical_Advice")

    val suggestedTools: List<String> =
        listOf("health_tracker", "symptom_checker_ref", "calendar", "document_editor")
}

// =====================================================================
// CompanionAdapter (PersonalHealthCompanionAdapter.cs)
// =====================================================================

/**
 * Wraps an [ICompanionSession] with the Personal.Health snippet + helpers.
 * Mirrors C# `PersonalHealthCompanionAdapter`.
 */
class PersonalHealthCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${PersonalHealthDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun prepareAppointmentAsync(symptoms: String, medHistory: String): String =
        inner.agentAsync("Help me prepare for a doctor appointment. Symptoms: $symptoms. Relevant history: $medHistory. Draft a concise symptom summary and list of questions to ask the doctor.")

    suspend fun explainHealthTermAsync(term: String): String =
        inner.agentAsync("Explain the medical term or concept in plain language: $term. Make it accessible to a non-medical person.")

    suspend fun interpretVitalsAsync(vitalsJson: String, age: String, baselineNotes: String): String =
        inner.agentAsync("Interpret vitals $vitalsJson for age $age. Baseline: $baselineNotes. Flag normal/borderline/concerning. Defer diagnosis to clinician.")

    suspend fun designSleepPlanAsync(currentPattern: String, targetWakeTime: String): String =
        inner.agentAsync("Design a sleep improvement plan from $currentPattern towards waking at $targetWakeTime. Cover light, caffeine, wind-down, environment.")

    suspend fun prepareForAppointmentAsync(concern: String, appointmentType: String): String =
        inner.agentAsync("Prepare for a $appointmentType about: $concern. Pre-visit checklist: symptoms log, questions, medication list, decisions to make.")

    suspend fun trackHabitImpactAsync(habit: String, vitalsBeforeAfter: String): String =
        inner.agentAsync("Analyse impact of $habit on vitals: $vitalsBeforeAfter. Confounders, signal strength, what to keep measuring.")
}
