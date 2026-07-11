// Healthcare.kt
//
// Kotlin port of CircleAI.Healthcare (HealthcarePrimitives.cs +
// HealthcareDomainContext.cs + HealthcareCompanionAdapter.cs) — the C#
// reference is the EXACT spec. Deterministic in-memory healthcare board.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `DateTime` / `DateTimeOffset` -> `java.time.LocalDate` / `Instant`.
//   * C# `ConcurrentDictionary<string,_>` (Ordinal) -> `ConcurrentHashMap`.
//   * C# `ArgumentNullException.ThrowIfNull` -> Kotlin non-null params (compiler-enforced).
//   * `AppointmentsFor` orders by AtUtc ASC; `PrescriptionsFor` orders by
//     PrescribedUtc DESC — reproduced exactly.
//   * `UpdateStatus` on an unknown appointment throws (IllegalStateException,
//     mirroring InvalidOperationException).
//   * The CompanionAdapter delegates to the Kotlin `ICompanionSession` and
//     prepends the domain system-prompt snippet, exactly as the C# adapter does.

package com.bhengubv.circleai.healthcare

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
// Primitives (HealthcarePrimitives.cs)
// =====================================================================

/** A patient. Mirrors C# `Patient`. */
data class Patient(val patientId: String, val name: String, val dateOfBirth: LocalDate)

/** A scheduled clinical appointment. Mirrors C# `HealthAppointment`. */
data class HealthAppointment(
    val apptId: String,
    val patientId: String,
    val provider: String,
    val atUtc: Instant,
    val status: String,
)

/** A prescription. Mirrors C# `Prescription`. */
data class Prescription(
    val rxId: String,
    val patientId: String,
    val medicationName: String,
    val dose: String,
    val frequency: String,
    val prescribedUtc: Instant,
)

/** Deterministic healthcare operations board. Mirrors C# `IHealthcareBoard`. */
interface IHealthcareBoard {
    fun register(p: Patient)
    fun getPatient(id: String): Patient?
    fun schedule(a: HealthAppointment)
    fun updateStatus(apptId: String, status: String)
    fun appointmentsFor(patientId: String): List<HealthAppointment>
    fun prescribe(r: Prescription)
    fun prescriptionsFor(patientId: String): List<Prescription>
}

/** In-memory [IHealthcareBoard]. Mirrors C# `InMemoryHealthcareBoard`. */
class InMemoryHealthcareBoard : IHealthcareBoard {
    private val patients = ConcurrentHashMap<String, Patient>()
    private val appts = ConcurrentHashMap<String, HealthAppointment>()
    private val rx = ConcurrentHashMap<String, Prescription>()

    override fun register(p: Patient) { patients[p.patientId] = p }
    override fun getPatient(id: String): Patient? = patients[id]
    override fun schedule(a: HealthAppointment) { appts[a.apptId] = a }

    override fun updateStatus(apptId: String, status: String) {
        val a = appts[apptId] ?: throw IllegalStateException("Unknown appointment $apptId")
        appts[apptId] = a.copy(status = status)
    }

    override fun appointmentsFor(patientId: String): List<HealthAppointment> =
        appts.values.filter { it.patientId == patientId }.sortedBy { it.atUtc }

    override fun prescribe(r: Prescription) { rx[r.rxId] = r }

    override fun prescriptionsFor(patientId: String): List<Prescription> =
        rx.values.filter { it.patientId == patientId }.sortedByDescending { it.prescribedUtc }
}

// =====================================================================
// DomainContext (HealthcareDomainContext.cs)
// =====================================================================

/** Static domain context for the Healthcare vertical. Mirrors C# `HealthcareDomainContext`. */
object HealthcareDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Healthcare] You are a healthcare operations and clinical knowledge assistant. " +
            "Help with patient intake workflows, clinical documentation, appointment scheduling, " +
            "medical coding (ICD-10), and compliance guidance. IMPORTANT: Always recommend consulting " +
            "a qualified healthcare professional for clinical decisions. This is a support tool, not a " +
            "diagnostic system. Compliance: HIPAA, POPIA, Health Professions Act, NHA."

    val complianceFlags: List<String> =
        listOf("HIPAA", "POPIA", "Health_Professions_Act_56_1974", "NHA_61_2003", "ICD10")

    val suggestedTools: List<String> =
        listOf("ehr_system", "appointment_scheduler", "document_editor", "icd10_lookup")
}

// =====================================================================
// CompanionAdapter (HealthcareCompanionAdapter.cs)
// =====================================================================

/**
 * Wraps an [ICompanionSession], prepending the Healthcare domain snippet to
 * ordinary turns and exposing domain-specific agentic helpers. Mirrors C#
 * `HealthcareCompanionAdapter`.
 */
class HealthcareCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${HealthcareDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun documentClinicalNoteAsync(patientVisitSummary: String): String =
        inner.agentAsync("Format this patient visit summary into a structured SOAP clinical note:\n$patientVisitSummary")

    suspend fun suggestIcd10CodesAsync(diagnosis: String): String =
        inner.agentAsync("Suggest relevant ICD-10-CM codes for the following diagnosis/condition: $diagnosis. Include primary and secondary codes with descriptions.")

    suspend fun draftPatientCommunicationAsync(purpose: String, patientContext: String): String =
        inner.agentAsync("Draft a clear, empathetic patient communication for: $purpose. Patient context: $patientContext. Keep language accessible (Grade 8 reading level).")

    suspend fun triageSymptomsAsync(patientAge: String, symptoms: String, duration: String): String =
        inner.agentAsync("Triage symptoms for $patientAge-year-old: $symptoms, duration $duration. Output urgency (emergency/urgent/routine), red flags, next step. Defer diagnosis to clinician.")

    suspend fun explainMedicationAsync(medication: String, indication: String): String =
        inner.agentAsync("Explain $medication prescribed for $indication to a patient. Cover purpose, dose schedule, common side effects, when to call.")

    suspend fun draftReferralLetterAsync(fromProvider: String, toSpecialty: String, clinicalSummary: String): String =
        inner.agentAsync("Draft a referral letter from $fromProvider to $toSpecialty. Clinical summary: $clinicalSummary. Include reason, history, exam, ask.")

    suspend fun counselOnAdherenceAsync(medication: String, patientConcerns: String): String =
        inner.agentAsync("Counsel on adherence to $medication. Patient concerns: $patientConcerns. Address each with evidence + practical strategies.")
}
