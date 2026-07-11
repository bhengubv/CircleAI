// Beauty.kt
//
// Kotlin port of CircleAI.Beauty (BeautyPrimitives.cs + BeautyDomainContext.cs +
// BeautyCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory beauty board: treatments, appointments, and skin
// profiles.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `decimal` -> `BigDecimal`;
//     `DateTimeOffset` -> `Instant`.
//   * `AppointmentsBetween` = inclusive [start, end], ASC.
//   * `RecommendFor` = treatments whose Name contains any of the client's
//     concerns (case-insensitive); empty when the client has no profile.

package com.bhengubv.circleai.beauty

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
// Primitives (BeautyPrimitives.cs)
// =====================================================================

/** A treatment offered. Mirrors C# `Treatment`. */
data class Treatment(val treatmentId: String, val name: String, val durationMinutes: Int, val price: BigDecimal, val currency: String)

/** A booked appointment. Mirrors C# `Appointment`. */
data class Appointment(val apptId: String, val clientName: String, val treatmentId: String, val atUtc: Instant, val notes: String?)

/** A client skin profile. Mirrors C# `SkinProfile`. */
data class SkinProfile(val clientName: String, val skinType: String, val concerns: List<String>)

/** Deterministic beauty board. Mirrors C# `IBeautyBoard`. */
interface IBeautyBoard {
    fun addTreatment(t: Treatment)
    fun getTreatment(id: String): Treatment?
    fun book(a: Appointment)
    fun appointmentsBetween(start: Instant, end: Instant): List<Appointment>
    fun saveProfile(p: SkinProfile)
    fun getProfile(clientName: String): SkinProfile?
    fun recommendFor(clientName: String): List<Treatment>
}

/** In-memory [IBeautyBoard]. Mirrors C# `InMemoryBeautyBoard`. */
class InMemoryBeautyBoard : IBeautyBoard {
    private val treatments = ConcurrentHashMap<String, Treatment>()
    private val appts = mutableListOf<Appointment>()
    private val profiles = ConcurrentHashMap<String, SkinProfile>()
    private val lock = Any()

    override fun addTreatment(t: Treatment) { treatments[t.treatmentId] = t }
    override fun getTreatment(id: String): Treatment? = treatments[id]
    override fun book(a: Appointment) { synchronized(lock) { appts.add(a) } }
    override fun appointmentsBetween(start: Instant, end: Instant): List<Appointment> = synchronized(lock) {
        appts.filter { !it.atUtc.isBefore(start) && !it.atUtc.isAfter(end) }.sortedBy { it.atUtc }
    }
    override fun saveProfile(p: SkinProfile) { profiles[p.clientName] = p }
    override fun getProfile(clientName: String): SkinProfile? = profiles[clientName]

    override fun recommendFor(clientName: String): List<Treatment> {
        val p = profiles[clientName] ?: return emptyList()
        return treatments.values.filter { t -> p.concerns.any { t.name.contains(it, ignoreCase = true) } }
    }
}

// =====================================================================
// DomainContext (BeautyDomainContext.cs)
// =====================================================================

/** Static domain context for Beauty. Mirrors C# `BeautyDomainContext`. */
object BeautyDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Beauty] Expert beauty and personal care companion. Help with skincare routine building, " +
            "ingredient education, product recommendations (without brand bias), hair care, makeup guidance, " +
            "and wellness rituals. Celebrate all skin tones, types, and expressions. Compliance: POPIA, " +
            "Medicines and Related Substances Act (cosmetic claims)."

    val complianceFlags: List<String> = listOf("POPIA", "Medicines_Act_cosmetic_claims")

    val suggestedTools: List<String> = listOf("product_db", "ingredient_checker", "web_search")
}

// =====================================================================
// CompanionAdapter (BeautyCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Beauty snippet + helpers. Mirrors C# `BeautyCompanionAdapter`. */
class BeautyCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${BeautyDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun buildSkincareRoutineAsync(skinType: String, concerns: String): String =
        inner.agentAsync("Build a skincare routine for $skinType skin. Concerns: $concerns. Include morning and evening steps, key ingredients, and ingredients to avoid.")

    suspend fun analyseIngredientAsync(ingredient: String): String =
        inner.agentAsync("Analyse the skincare ingredient: $ingredient. Explain function, benefits, potential irritants, and who it suits best.")

    suspend fun recommendRoutineAsync(skinType: String, concerns: String, budget: String): String =
        inner.agentAsync("Recommend an AM/PM skincare routine for $skinType skin with $concerns, budget $budget. Include ingredient targets and product categories (not brands).")

    suspend fun assessIngredientCompatibilityAsync(ingredientList: String): String =
        inner.agentAsync("Assess this ingredient list for layering safety + irritation risk: $ingredientList. Flag known clashes (retinol+AHA, vit C+niacinamide, etc.).")

    suspend fun designTreatmentPlanAsync(clientGoals: String, sessionCount: Int): String =
        inner.agentAsync("Design a $sessionCount-session treatment plan to achieve: $clientGoals. Specify modality, interval, expected progress, and at-home care.")

    suspend fun draftBookingConfirmationAsync(clientName: String, treatment: String, dateTime: String): String =
        inner.agentAsync("Draft a warm booking confirmation message: $clientName, $treatment, $dateTime. Include prep instructions, cancellation policy, location.")
}
