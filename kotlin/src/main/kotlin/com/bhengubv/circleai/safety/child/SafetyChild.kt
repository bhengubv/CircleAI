// SafetyChild.kt
//
// Kotlin port of CircleAI.Safety.Child — the C# reference is the EXACT spec
// (ChildSafetyPrimitives.cs, SafetyChildDomainContext.cs,
// SafetyChildCompanionAdapter.cs).
//
// (3.3.0) Real domain types + in-memory store for the Child Safety vertical:
// trusted-adult ring, geofences, check-in events; the domain context; and a
// companion-session adapter that prefixes the domain context and adds
// child-safety-specific workflows.
//
// Type map (C# -> Kotlin):
//   record TrustedAdult/Geofence/     -> data class TrustedAdult/Geofence/CheckIn
//     CheckIn
//   interface IChildSafetyBoard       -> interface IChildSafetyBoard
//   class InMemoryChildSafetyBoard    -> class InMemoryChildSafetyBoard (thread-safe)
//   static SafetyChildDomainContext   -> object SafetyChildDomainContext
//   class SafetyChildCompanionAdapter -> class SafetyChildCompanionAdapter
//
// NOTE: the C# `IsInsideAnyFence` Haversine (R = 6_371_000 m, deg->rad, atan2)
// is reproduced byte-for-byte so geofence membership matches across ports.

package com.bhengubv.circleai.safety.child

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.sqrt

// ---------------------------------------------------------------------------
// Records
// ---------------------------------------------------------------------------

/** A trusted adult in the child's safety ring, ordered by [ringPriority] (ascending). */
data class TrustedAdult(
    val adultId: String,
    val name: String,
    val phone: String,
    val relationship: String,
    val ringPriority: Int,
)

/** A circular geofence around a point, in metres. */
data class Geofence(
    val fenceId: String,
    val name: String,
    val centreLat: Double,
    val centreLon: Double,
    val radiusMeters: Double,
)

/** A child check-in event, optionally geo-located. */
data class CheckIn(
    val childId: String,
    val status: String,
    val lat: Double?,
    val lon: Double?,
    val atUtc: Instant,
)

// ---------------------------------------------------------------------------
// IChildSafetyBoard
// ---------------------------------------------------------------------------

/**
 * In-memory board for the child-safety vertical: trusted-adult ring, geofences,
 * and check-in history.
 */
interface IChildSafetyBoard {
    /** Add / overwrite a trusted adult by id. */
    fun addAdult(a: TrustedAdult)

    /** The trusted-adult ring ordered by ascending [TrustedAdult.ringPriority]. */
    val ringOrdered: List<TrustedAdult>

    /** Define / overwrite a geofence by id. */
    fun defineGeofence(g: Geofence)

    /** Look up a geofence by id, or null. */
    fun getGeofence(id: String): Geofence?

    /** True if [lat],[lon] is within any defined geofence's radius. */
    fun isInsideAnyFence(lat: Double, lon: Double): Boolean

    /** Append a check-in event. */
    fun recordCheckIn(c: CheckIn)

    /** The most recent [limit] check-ins for [childId], newest first. */
    fun recentCheckIns(childId: String, limit: Int = 20): List<CheckIn>
}

// ---------------------------------------------------------------------------
// InMemoryChildSafetyBoard
// ---------------------------------------------------------------------------

/**
 * (3.3.0) Deterministic in-memory [IChildSafetyBoard]. Adults and fences are
 * keyed maps (later add with the same id overwrites); check-ins are an
 * append-only list guarded by a single monitor (mirrors the C# `lock (_lock)`).
 */
class InMemoryChildSafetyBoard : IChildSafetyBoard {

    private val adults = HashMap<String, TrustedAdult>()
    private val fences = HashMap<String, Geofence>()
    private val checkIns = ArrayList<CheckIn>()
    private val lock = Any()

    override fun addAdult(a: TrustedAdult) {
        synchronized(lock) { adults[a.adultId] = a }
    }

    override val ringOrdered: List<TrustedAdult>
        get() = synchronized(lock) { adults.values.sortedBy { it.ringPriority } }

    override fun defineGeofence(g: Geofence) {
        synchronized(lock) { fences[g.fenceId] = g }
    }

    override fun getGeofence(id: String): Geofence? =
        synchronized(lock) { fences[id] }

    override fun isInsideAnyFence(lat: Double, lon: Double): Boolean =
        synchronized(lock) {
            for (g in fences.values) {
                if (haversineMeters(g.centreLat, g.centreLon, lat, lon) <= g.radiusMeters) return true
            }
            false
        }

    override fun recordCheckIn(c: CheckIn) {
        synchronized(lock) { checkIns.add(c) }
    }

    override fun recentCheckIns(childId: String, limit: Int): List<CheckIn> {
        require(limit > 0) { "limit must be greater than zero" }
        return synchronized(lock) {
            checkIns
                .filter { it.childId == childId }
                .sortedByDescending { it.atUtc }
                .take(limit)
        }
    }

    private companion object {
        private const val R = 6_371_000.0

        private fun degToRad(d: Double): Double = d * Math.PI / 180.0

        fun haversineMeters(aLat: Double, aLon: Double, bLat: Double, bLon: Double): Double {
            val dLat = degToRad(bLat - aLat)
            val dLon = degToRad(bLon - aLon)
            val s1 = sin(dLat / 2)
            val s2 = sin(dLon / 2)
            val a = s1 * s1 + cos(degToRad(aLat)) * cos(degToRad(bLat)) * s2 * s2
            val c = 2 * atan2(sqrt(a), sqrt(1 - a))
            return R * c
        }
    }
}

// ---------------------------------------------------------------------------
// SafetyChildDomainContext
// ---------------------------------------------------------------------------

/**
 * Static domain descriptor for the child-safety vertical. Strings are
 * byte-identical to the C# reference (namespace `CircleAI.SafetyChild`).
 */
object SafetyChildDomainContext {
    const val systemPromptSnippet: String =
        "[DOMAIN: Safety.Child] Child safety and safeguarding assistant for parents and educators. " +
            "Help with online safety education, age-appropriate device rules, recognising grooming signs, " +
            "reporting abuse, and digital literacy. Always prioritise child welfare. IMPORTANT: For " +
            "immediate child safety concerns, contact SAPS (10111) or Childline (116). Compliance: " +
            "Children's Act 38/2005, POPIA (children's data), FILMS_PUBLICATIONS_ACT, Cybercrimes Act."

    val complianceFlags: List<String> = listOf(
        "Childrens_Act_38_2005",
        "POPIA_Children",
        "Films_Publications_Act",
        "Cybercrimes_Act",
        "Emergency_116",
    )

    val suggestedTools: List<String> = listOf(
        "parental_controls",
        "web_search",
        "document_editor",
        "reporting_tools",
    )
}

// ---------------------------------------------------------------------------
// SafetyChildCompanionAdapter
// ---------------------------------------------------------------------------

/**
 * Wraps an [ICompanionSession], prefixing every user turn with the child-safety
 * domain context and exposing child-safety-specific agentic workflows. Delegates
 * all base session behaviour to the inner session — mirrors the C#
 * SafetyChildCompanionAdapter.
 */
class SafetyChildCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {

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

    // ── Child-safety-specific workflows ────────────────────────────────────

    suspend fun setDigitalRulesAsync(childAge: String): String =
        inner.agentAsync(
            "Create age-appropriate digital safety rules for a $childAge-year-old. Include screen time " +
                "limits, app/platform permissions, online communication rules, and how to report " +
                "concerning content.",
        )

    suspend fun educateOnlineRisksAsync(childAge: String): String =
        inner.agentAsync(
            "Explain online safety concepts appropriate for a $childAge-year-old. Cover: stranger danger " +
                "online, personal information sharing, cyberbullying, and who to tell if something feels " +
                "wrong. Use simple, non-scary language.",
        )

    suspend fun designSafetyConversationAsync(childAge: String, topic: String): String =
        inner.agentAsync(
            "Design an age-appropriate safety conversation for $childAge on: $topic. Concrete examples, " +
                "scripts they can use, role-play prompt.",
        )

    suspend fun assessOnlineRiskAsync(platform: String, childAge: String, behaviour: String): String =
        inner.agentAsync(
            "Assess online risk on $platform for $childAge-year-old showing $behaviour. Specific risks + " +
                "parent-action checklist.",
        )

    suspend fun verifyTrustedAdultsAsync(contactList: String): String =
        inner.agentAsync(
            "Help vet trusted-adult ring from: $contactList. Criteria to apply, questions to ask the child.",
        )

    suspend fun draftSchoolNotificationAsync(concern: String, evidence: String): String =
        inner.agentAsync(
            "Draft a school notification about: $concern. Evidence: $evidence. Calm, factual, requesting " +
                "specific action.",
        )

    /** Prefix a user message with the child-safety domain context (mirrors the C# `E(m)` helper). */
    private fun enrich(m: String): String = "${SafetyChildDomainContext.systemPromptSnippet}\n\n$m"
}
