// Pets.kt
//
// Kotlin port of CircleAI.Pets (PetsPrimitives.cs + PetsDomainContext.cs +
// PetsCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory pets board: pets, vaccinations, weight history,
// and vet appointments.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTime`/`DateTimeOffset` -> `Instant`.
//   * `Pets` orders by Name ASC.
//   * `VaccinationsFor` returns newest-first (AdministeredUtc DESC).
//   * `WeightHistory` returns oldest-first (AtUtc ASC).
//   * `UpcomingAppointments` returns appts with AtUtc >= now (UTC), ordered ASC.

package com.bhengubv.circleai.pets

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (PetsPrimitives.cs)
// =====================================================================

/** A pet. Mirrors C# `Pet`. */
data class Pet(val petId: String, val name: String, val species: String, val breed: String?, val dateOfBirth: Instant)

/** A vaccination record. Mirrors C# `Vaccination`. */
data class Vaccination(
    val petId: String,
    val vaccine: String,
    val administeredUtc: Instant,
    val boosterDueUtc: Instant?,
)

/** A weight measurement. Mirrors C# `WeightSample`. */
data class WeightSample(val petId: String, val weightKg: Double, val atUtc: Instant)

/** A vet appointment. Mirrors C# `VetAppointment`. */
data class VetAppointment(val apptId: String, val petId: String, val reason: String, val atUtc: Instant, val vet: String)

/** Deterministic pets board. Mirrors C# `IPetsBoard`. */
interface IPetsBoard {
    fun add(p: Pet)
    fun getPet(id: String): Pet?
    val pets: List<Pet>
    fun recordVaccination(v: Vaccination)
    fun vaccinationsFor(petId: String): List<Vaccination>
    fun recordWeight(s: WeightSample)
    fun weightHistory(petId: String): List<WeightSample>
    fun schedule(a: VetAppointment)
    fun upcomingAppointments(): List<VetAppointment>
}

/** In-memory [IPetsBoard]. Mirrors C# `InMemoryPetsBoard`. */
class InMemoryPetsBoard : IPetsBoard {
    private val pets_ = ConcurrentHashMap<String, Pet>()
    private val vax = mutableListOf<Vaccination>()
    private val weights = mutableListOf<WeightSample>()
    private val appts = ConcurrentHashMap<String, VetAppointment>()
    private val lock = Any()

    override fun add(p: Pet) { pets_[p.petId] = p }
    override fun getPet(id: String): Pet? = pets_[id]
    override val pets: List<Pet>
        get() = pets_.values.sortedBy { it.name }

    override fun recordVaccination(v: Vaccination) { synchronized(lock) { vax.add(v) } }
    override fun vaccinationsFor(petId: String): List<Vaccination> = synchronized(lock) {
        vax.filter { it.petId == petId }.sortedByDescending { it.administeredUtc }
    }

    override fun recordWeight(s: WeightSample) { synchronized(lock) { weights.add(s) } }
    override fun weightHistory(petId: String): List<WeightSample> = synchronized(lock) {
        weights.filter { it.petId == petId }.sortedBy { it.atUtc }
    }

    override fun schedule(a: VetAppointment) { appts[a.apptId] = a }
    override fun upcomingAppointments(): List<VetAppointment> {
        val now = Instant.now()
        return appts.values.filter { !it.atUtc.isBefore(now) }.sortedBy { it.atUtc }
    }
}

// =====================================================================
// DomainContext (PetsDomainContext.cs)
// =====================================================================

/** Static domain context for Pets. Mirrors C# `PetsDomainContext`. */
object PetsDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Pets] Expert pet care companion. Help with nutrition advice, training techniques (positive " +
            "reinforcement), health symptom triage (recommend vet for medical decisions), breed-specific care, " +
            "and emergency first aid basics. Compliance: Animals Protection Act 71/1962, POPIA."

    val complianceFlags: List<String> = listOf("Animals_Protection_Act_71_1962", "POPIA", "Vet_Referral_Required")

    val suggestedTools: List<String> = listOf("vet_finder", "pet_health_db", "training_tools", "calendar")
}

// =====================================================================
// CompanionAdapter (PetsCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Pets snippet + helpers. Mirrors C# `PetsCompanionAdapter`. */
class PetsCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${PetsDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun triageSymptomAsync(species: String, breed: String, symptom: String): String =
        inner.agentAsync("Triage this pet health concern. Species: $species. Breed: $breed. Symptom: $symptom. Indicate urgency level and whether immediate vet care is needed.")

    suspend fun createTrainingPlanAsync(species: String, age: String, behaviour: String): String =
        inner.agentAsync("Create a positive reinforcement training plan for a $age $species to address: $behaviour. Include daily session structure, reward strategy, and realistic timeline.")

    suspend fun adviseDietAsync(species: String, lifeStage: String, healthNotes: String): String =
        inner.agentAsync("Advise diet for $lifeStage $species. Health notes: $healthNotes. Cover composition, portions, transitions, treats.")

    suspend fun planTravelWithPetAsync(species: String, destination: String, transport: String): String =
        inner.agentAsync("Plan $transport travel to $destination with $species. Documents, crate, breaks, stress reduction.")
}
