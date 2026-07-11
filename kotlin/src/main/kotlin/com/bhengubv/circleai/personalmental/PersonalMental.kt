// PersonalMental.kt
//
// Kotlin port of CircleAI.Personal.Mental (PersonalMentalPrimitives.cs +
// PersonalMentalDomainContext.cs + PersonalMentalCompanionAdapter.cs) — the C#
// reference is the EXACT spec. Deterministic in-memory mental-health store:
// mood logs, journal entries, coping-strategy library, 7-day trend.
//
// Fidelity notes:
//   * C# `enum Mood` -> Kotlin `enum class` (VeryLow..Great, ordinals 0..4).
//   * C# `record` -> Kotlin `data class`.
//   * C# `DateTimeOffset` -> `java.time.Instant`.
//   * C# `IReadOnlyList<string>` (Tags) -> `List<String>`.
//   * C# `ConcurrentDictionary<string,_>` (Ordinal) -> `ConcurrentHashMap`;
//     mood logs live in a plain list behind a lock.
//   * `Last7Days` keeps AtUtc >= (now − 7 days), ordered ASC.
//   * `AddEntry` rejects a blank EntryId.
//   * `Entries` orders by AtUtc DESC.
//   * `StrategiesByTag` rejects a blank tag and matches tags case-insensitively.
//   * `AvgMood7Day` returns NaN for an empty 7-day window, else the mean of the
//     mood ordinals (VeryLow=0 .. Great=4).

package com.bhengubv.circleai.personalmental

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (PersonalMentalPrimitives.cs)
// =====================================================================

/** A discrete mood level. Mirrors C# `Mood` (VeryLow..Great, ordinals 0..4). */
enum class Mood { VeryLow, Low, Neutral, Good, Great }

/** A logged mood at a point in time. Mirrors C# `MoodLog`. */
data class MoodLog(val mood: Mood, val atUtc: Instant, val note: String?)

/** A journal entry. Mirrors C# `JournalEntry`. */
data class JournalEntry(val entryId: String, val title: String, val body: String, val atUtc: Instant)

/** A reusable coping strategy. Mirrors C# `CopingStrategy`. */
data class CopingStrategy(val strategyId: String, val title: String, val description: String, val tags: List<String>)

/** Deterministic mental-health board. Mirrors C# `IMentalHealthBoard`. */
interface IMentalHealthBoard {
    fun logMood(m: MoodLog)
    fun last7Days(): List<MoodLog>
    fun addEntry(e: JournalEntry)
    val entries: List<JournalEntry>
    fun registerStrategy(s: CopingStrategy)
    fun strategiesByTag(tag: String): List<CopingStrategy>
    fun avgMood7Day(): Double
}

/** In-memory [IMentalHealthBoard]. Mirrors C# `InMemoryMentalHealthBoard`. */
class InMemoryMentalHealthBoard : IMentalHealthBoard {
    private val moods = mutableListOf<MoodLog>()
    private val entries_ = ConcurrentHashMap<String, JournalEntry>()
    private val strats = ConcurrentHashMap<String, CopingStrategy>()
    private val lock = Any()

    override fun logMood(m: MoodLog) { synchronized(lock) { moods.add(m) } }

    override fun last7Days(): List<MoodLog> {
        val cutoff = Instant.now().minus(7, ChronoUnit.DAYS)
        return synchronized(lock) { moods.filter { it.atUtc >= cutoff }.sortedBy { it.atUtc } }
    }

    override fun addEntry(e: JournalEntry) {
        require(e.entryId.isNotBlank()) { "EntryId required" }
        entries_[e.entryId] = e
    }

    override val entries: List<JournalEntry>
        get() = entries_.values.sortedByDescending { it.atUtc }

    override fun registerStrategy(s: CopingStrategy) { strats[s.strategyId] = s }

    override fun strategiesByTag(tag: String): List<CopingStrategy> {
        require(tag.isNotBlank()) { "tag required" }
        return strats.values.filter { s -> s.tags.any { it.equals(tag, ignoreCase = true) } }
    }

    override fun avgMood7Day(): Double {
        val items = last7Days()
        if (items.isEmpty()) return Double.NaN
        return items.map { it.mood.ordinal.toDouble() }.average()
    }
}

// =====================================================================
// DomainContext (PersonalMentalDomainContext.cs)
// =====================================================================

/** Static domain context for Personal.Mental. Mirrors C# `PersonalMentalDomainContext`. */
object PersonalMentalDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Personal.Mental] Warm, empathetic mental wellness companion. Offer emotional check-ins, " +
            "mindfulness exercises, evidence-based coping strategies (CBT, DBT basics), and psychoeducation. " +
            "Never diagnose. Always validate feelings before offering tools. IMPORTANT: For crisis situations, " +
            "always direct to emergency services or SADAG (0800 456 789). Not a substitute for professional " +
            "therapy. Compliance: POPIA, Mental Health Care Act."

    val complianceFlags: List<String> =
        listOf("POPIA", "Mental_Health_Care_Act_17_2002", "Not_Therapy", "Crisis_Protocol")

    val suggestedTools: List<String> = listOf("journal", "breathing_tools", "mood_tracker", "web_search")
}

// =====================================================================
// CompanionAdapter (PersonalMentalCompanionAdapter.cs)
// =====================================================================

/**
 * Wraps an [ICompanionSession] with the Personal.Mental snippet + helpers.
 * Mirrors C# `PersonalMentalCompanionAdapter`.
 */
class PersonalMentalCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${PersonalMentalDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun checkInAsync(mood: String): String =
        inner.agentAsync("I am feeling: $mood. Respond with empathy, validate my feeling, then gently offer one evidence-based coping tool relevant to my current state.")

    suspend fun guideMindfulnessAsync(duration: String): String =
        inner.agentAsync("Guide me through a $duration mindfulness or breathing exercise. Use a calm, grounding tone.")

    suspend fun reframeThoughtAsync(distortedThought: String, context: String): String =
        inner.agentAsync("Help reframe this thought: $distortedThought. Context: $context. Name the distortion (CBT lens), offer a balanced alternative.")

    suspend fun designCheckInRitualAsync(lifeStage: String, availableMinutes: String): String =
        inner.agentAsync("Design a $availableMinutes-minute daily mental check-in for someone in $lifeStage. Make it sustainable for low-energy days.")

    suspend fun prepareTherapySessionAsync(sessionThemes: String, lastWeekEvents: String): String =
        inner.agentAsync("Prepare for a therapy session on themes: $sessionThemes. Recent events: $lastWeekEvents. List 3 top topics + one experiment to try.")

    suspend fun groundDuringPanicAsync(trigger: String, environment: String): String =
        inner.agentAsync("Guide a grounding script for panic triggered by: $trigger in environment: $environment. 5-4-3-2-1 sensory anchor + breath.")
}
