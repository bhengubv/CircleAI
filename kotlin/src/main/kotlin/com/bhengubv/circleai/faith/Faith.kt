// Faith.kt
//
// Kotlin port of CircleAI.Faith (FaithPrimitives.cs + FaithDomainContext.cs +
// FaithCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory faith board: services, prayer requests, and scripture.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTimeOffset` -> `Instant`.
//   * `ServicesBetween` inclusive [start, end], ASC.
//   * `RecentPrayers` newest-first, capped at `limit` (default 20).
//   * `Lookup` = first exact (tradition, book, chapter, verse) match (or null).
//   * `ByTradition` case-insensitive.

package com.bhengubv.circleai.faith

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (FaithPrimitives.cs)
// =====================================================================

/** A faith community service. Mirrors C# `FaithService`. */
data class FaithService(val serviceId: String, val communityName: String, val title: String, val startUtc: Instant, val location: String)

/** A prayer request. Mirrors C# `PrayerRequest`. */
data class PrayerRequest(val requestId: String, val author: String, val body: String, val submittedUtc: Instant, val isAnonymous: Boolean)

/** A scripture reference. Mirrors C# `ScriptureReference`. */
data class ScriptureReference(val referenceId: String, val tradition: String, val book: String, val chapter: Int, val verse: Int, val text: String)

/** Deterministic faith board. Mirrors C# `IFaithBoard`. */
interface IFaithBoard {
    fun schedule(s: FaithService)
    fun servicesBetween(start: Instant, end: Instant): List<FaithService>
    fun submitPrayer(r: PrayerRequest)
    fun recentPrayers(limit: Int = 20): List<PrayerRequest>
    fun addScripture(r: ScriptureReference)
    fun lookup(tradition: String, book: String, chapter: Int, verse: Int): ScriptureReference?
    fun byTradition(tradition: String): List<ScriptureReference>
}

/** In-memory [IFaithBoard]. Mirrors C# `InMemoryFaithBoard`. */
class InMemoryFaithBoard : IFaithBoard {
    private val services = ConcurrentHashMap<String, FaithService>()
    private val prayers = mutableListOf<PrayerRequest>()
    private val scripture = ConcurrentHashMap<String, ScriptureReference>()
    private val lock = Any()

    override fun schedule(s: FaithService) { services[s.serviceId] = s }
    override fun servicesBetween(start: Instant, end: Instant): List<FaithService> =
        services.values.filter { !it.startUtc.isBefore(start) && !it.startUtc.isAfter(end) }.sortedBy { it.startUtc }

    override fun submitPrayer(r: PrayerRequest) { synchronized(lock) { prayers.add(r) } }
    override fun recentPrayers(limit: Int): List<PrayerRequest> = synchronized(lock) {
        prayers.sortedByDescending { it.submittedUtc }.take(limit)
    }

    override fun addScripture(r: ScriptureReference) { scripture[r.referenceId] = r }
    override fun lookup(tradition: String, book: String, chapter: Int, verse: Int): ScriptureReference? =
        scripture.values.firstOrNull { it.tradition == tradition && it.book == book && it.chapter == chapter && it.verse == verse }

    override fun byTradition(tradition: String): List<ScriptureReference> =
        scripture.values.filter { it.tradition.equals(tradition, ignoreCase = true) }
}

// =====================================================================
// DomainContext (FaithDomainContext.cs)
// =====================================================================

/** Static domain context for Faith. Mirrors C# `FaithDomainContext`. */
object FaithDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Faith] Respectful, non-denominational spiritual companion. Help with scripture study, " +
            "prayer composition, devotional content, faith community planning, and spiritual reflection " +
            "prompts. Respect all faith traditions equally. Never impose one tradition on another. Compliance: POPIA."

    val complianceFlags: List<String> = listOf("POPIA", "Non_Denominational_Respect")

    val suggestedTools: List<String> = listOf("scripture_tools", "document_editor", "calendar")
}

// =====================================================================
// CompanionAdapter (FaithCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Faith snippet + helpers. Mirrors C# `FaithCompanionAdapter`. */
class FaithCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${FaithDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun generateDevotionalAsync(theme: String, tradition: String): String =
        inner.agentAsync("Write a short devotional on the theme of $theme for the $tradition tradition. Include a scripture reference, reflection, and closing prayer or meditation.")

    suspend fun studyScriptureAsync(passage: String, question: String): String =
        inner.agentAsync("Help me study $passage. Question: $question. Provide historical context, key interpretations across traditions, and practical application.")

    suspend fun composeReflectionAsync(tradition: String, occasion: String, scriptureRef: String): String =
        inner.agentAsync("Compose a 200-word reflection in the $tradition for $occasion, anchored in $scriptureRef. Warm, inclusive, devotional.")

    suspend fun draftServiceOrderAsync(tradition: String, serviceType: String, durationMinutes: Int): String =
        inner.agentAsync("Draft a $durationMinutes-minute $serviceType order of service in the $tradition. Sections, transitions, music cues, scripture readings.")

    suspend fun writePastoralCareNoteAsync(parishionerSituation: String): String =
        inner.agentAsync("Write a pastoral care note for: $parishionerSituation. Acknowledge, hold space, offer concrete next step. Avoid platitudes.")

    suspend fun findScripturePassagesAsync(tradition: String, theme: String): String =
        inner.agentAsync("Find 3 scripture passages on '$theme' in the $tradition. For each: reference, key verse text, brief context.")
}
