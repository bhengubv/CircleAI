// Safety.kt
//
// Kotlin port of CircleAI.Safety — the C# reference is the EXACT spec
// (SafetyPrimitives.cs, SafetyDomainContext.cs, SafetyCompanionAdapter.cs).
//
// (3.3.0) Real domain types + in-memory store for the Safety vertical:
// incidents, hazards, emergency contacts, severity-routing; the domain context
// (system-prompt snippet + compliance/tool hints); and a companion-session
// adapter that prefixes the domain context and adds safety-specific workflows.
//
// Type map (C# -> Kotlin):
//   enum IncidentSeverity            -> enum class IncidentSeverity
//   record Incident/Hazard/          -> data class Incident/Hazard/EmergencyContact
//     EmergencyContact
//   interface ISafetyBoard           -> interface ISafetyBoard
//   class InMemorySafetyBoard        -> class InMemorySafetyBoard (thread-safe)
//   static SafetyDomainContext       -> object SafetyDomainContext
//   class SafetyCompanionAdapter     -> class SafetyCompanionAdapter (wraps ICompanionSession)

package com.bhengubv.circleai.safety

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant

// ---------------------------------------------------------------------------
// IncidentSeverity
// ---------------------------------------------------------------------------

/**
 * Severity of a logged safety incident, ascending.
 *
 * Declaration order mirrors the C# reference so ordinals stay stable across
 * every language port — [AtOrAboveSeverity] compares by ordinal.
 */
enum class IncidentSeverity {
    /** Informational — no action needed. */
    Info,

    /** Warning — worth attention. */
    Warning,

    /** Critical — needs prompt response. */
    Critical,

    /** Emergency — immediate danger to life or property. */
    Emergency,
}

// ---------------------------------------------------------------------------
// Records
// ---------------------------------------------------------------------------

/** A logged safety incident, optionally geo-located. */
data class Incident(
    val incidentId: String,
    val severity: IncidentSeverity,
    val description: String,
    val latitude: Double?,
    val longitude: Double?,
    val atUtc: Instant,
)

/** A noted hazard (persistent risk, not a point-in-time incident). */
data class Hazard(
    val hazardId: String,
    val description: String,
    val category: String,
    val notedUtc: Instant,
)

/** An emergency contact in the safety ring. */
data class EmergencyContact(
    val contactId: String,
    val name: String,
    val phone: String,
    val relationship: String,
)

// ---------------------------------------------------------------------------
// ISafetyBoard
// ---------------------------------------------------------------------------

/**
 * In-memory board of incidents, hazards and emergency contacts.
 */
interface ISafetyBoard {
    /** Append an incident to the log. */
    fun log(i: Incident)

    /** All incidents, newest first. */
    val active: List<Incident>

    /** Incidents whose severity is at or above [minimum], newest first. */
    fun atOrAboveSeverity(minimum: IncidentSeverity): List<Incident>

    /** Record / overwrite a hazard by id. */
    fun noteHazard(h: Hazard)

    /** All hazards, most-recently-noted first. */
    val hazards: List<Hazard>

    /** Append an emergency contact. */
    fun addContact(c: EmergencyContact)

    /** The first-added contact, or null when the ring is empty. */
    val firstContact: EmergencyContact?

    /** All contacts in insertion order. */
    val contacts: List<EmergencyContact>
}

// ---------------------------------------------------------------------------
// InMemorySafetyBoard
// ---------------------------------------------------------------------------

/**
 * (3.3.0) Deterministic in-memory [ISafetyBoard]. Incidents and contacts are
 * append-only lists guarded by a single monitor (mirrors the C# `lock (_lock)`);
 * hazards are a keyed map so a later note with the same id overwrites the prior
 * one (mirrors the C# ConcurrentDictionary indexer assignment).
 */
class InMemorySafetyBoard : ISafetyBoard {

    private val incidents = ArrayList<Incident>()
    private val hazardsMap = HashMap<String, Hazard>()
    private val contactsList = ArrayList<EmergencyContact>()
    private val lock = Any()

    override fun log(i: Incident) {
        synchronized(lock) { incidents.add(i) }
    }

    override val active: List<Incident>
        get() = synchronized(lock) { incidents.sortedByDescending { it.atUtc } }

    override fun atOrAboveSeverity(minimum: IncidentSeverity): List<Incident> =
        synchronized(lock) {
            incidents
                .filter { it.severity.ordinal >= minimum.ordinal }
                .sortedByDescending { it.atUtc }
        }

    override fun noteHazard(h: Hazard) {
        synchronized(lock) { hazardsMap[h.hazardId] = h }
    }

    override val hazards: List<Hazard>
        get() = synchronized(lock) { hazardsMap.values.sortedByDescending { it.notedUtc } }

    override fun addContact(c: EmergencyContact) {
        synchronized(lock) { contactsList.add(c) }
    }

    override val firstContact: EmergencyContact?
        get() = synchronized(lock) { contactsList.firstOrNull() }

    override val contacts: List<EmergencyContact>
        get() = synchronized(lock) { contactsList.toList() }
}

// ---------------------------------------------------------------------------
// SafetyDomainContext
// ---------------------------------------------------------------------------

/**
 * Static domain descriptor for the personal-safety vertical: the system-prompt
 * snippet plus compliance and suggested-tool hints. Strings are byte-identical
 * to the C# reference.
 */
object SafetyDomainContext {
    const val systemPromptSnippet: String =
        "[DOMAIN: Safety] Personal safety and emergency preparedness assistant. " +
            "Help with home security assessments, emergency response plans, first aid guidance " +
            "(always recommend professional training), situational awareness tips, and crisis " +
            "communication. IMPORTANT: For life-threatening emergencies, direct immediately to " +
            "10111 (SAPS) or 10177 (ambulance). Compliance: POPIA, OHS Act."

    val complianceFlags: List<String> = listOf("POPIA", "OHS_Act", "Emergency_Protocol_10111")

    val suggestedTools: List<String> = listOf("emergency_contacts", "document_editor", "map", "web_search")
}

// ---------------------------------------------------------------------------
// SafetyCompanionAdapter
// ---------------------------------------------------------------------------

/**
 * Wraps an [ICompanionSession], prefixing every user turn with the safety domain
 * context and exposing safety-specific agentic workflows. Delegates all base
 * session behaviour to the inner session — mirrors the C# SafetyCompanionAdapter.
 */
class SafetyCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {

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

    // ── Safety-specific workflows ──────────────────────────────────────────

    suspend fun createEmergencyPlanAsync(householdSize: String, location: String): String =
        inner.agentAsync(
            "Create a personalised emergency preparedness plan for a $householdSize-person household " +
                "in $location. Include evacuation routes, emergency contacts, go-bag checklist, and " +
                "72-hour supply list.",
        )

    suspend fun assessSecurityAsync(propertyType: String, concerns: String): String =
        inner.agentAsync(
            "Assess home security for a $propertyType. Concerns: $concerns. Identify vulnerabilities " +
                "and recommend physical, electronic, and procedural improvements.",
        )

    suspend fun conductRiskAssessmentAsync(activity: String, environment: String): String =
        inner.agentAsync(
            "Conduct a risk assessment for $activity in $environment. Hazard, likelihood, severity, controls.",
        )

    suspend fun draftEmergencyResponseAsync(incidentType: String, siteContext: String): String =
        inner.agentAsync(
            "Draft emergency response steps for $incidentType at $siteContext. Roles, escalation, comms, debrief.",
        )

    suspend fun briefSafetyToolboxAsync(task: String, topHazards: String): String =
        inner.agentAsync(
            "Brief a 5-min toolbox talk for task: $task. Top hazards: $topHazards. Controls, PPE, sign-off.",
        )

    suspend fun reviewIncidentReportAsync(incidentNarrative: String): String =
        inner.agentAsync(
            "Review this incident narrative: $incidentNarrative. Identify root cause, contributing factors, " +
                "corrective + preventive actions.",
        )

    /** Prefix a user message with the safety domain context (mirrors the C# `E(m)` helper). */
    private fun enrich(m: String): String = "${SafetyDomainContext.systemPromptSnippet}\n\n$m"
}
