// Kids.kt
//
// Kotlin port of CircleAI.Kids (KidsPrimitives.cs + KidsDomainContext.cs +
// KidsCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory kids board: content, daily-time limits, and usage.
//
// Fidelity notes:
//   * C# `enum AgeAppropriateness`   -> Kotlin `enum class`.
//   * C# `record`                    -> Kotlin `data class`; `TimeSpan` ->
//     `java.time.Duration`, `DateTimeOffset` -> `java.time.Instant`.
//   * `ContentFor` = content in the band, ASC by Title.
//   * `UsedToday` sums durations logged on the same UTC calendar day.
//   * `OverLimit`: false with no limits set; caps by kind — "screen"/"reading"
//     (case-insensitive) use the set limit, any other kind is effectively
//     uncapped (C# `TimeSpan.MaxValue`).

package com.bhengubv.circleai.kids

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Duration
import java.time.Instant
import java.time.ZoneOffset
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (KidsPrimitives.cs)
// =====================================================================

/** Age band for content appropriateness. Mirrors C# `AgeAppropriateness`. */
enum class AgeAppropriateness { Toddler, Preschool, EarlyPrimary, LatePrimary, PreTeen, Teen }

/** A piece of kids content. Mirrors C# `KidsContent`. */
data class KidsContent(val contentId: String, val title: String, val ageBand: AgeAppropriateness, val kind: String, val tags: List<String>)

/** Daily-time limits for a kid. Mirrors C# `DailyTime`. */
data class DailyTime(val kidName: String, val screenLimit: Duration, val readingLimit: Duration)

/** A usage-time log entry. Mirrors C# `TimeLog`. */
data class TimeLog(val kidName: String, val kind: String, val duration: Duration, val atUtc: Instant)

/** Deterministic kids board. Mirrors C# `IKidsBoard`. */
interface IKidsBoard {
    fun addContent(c: KidsContent)
    fun contentFor(band: AgeAppropriateness): List<KidsContent>
    fun setLimits(d: DailyTime)
    fun limitsFor(kidName: String): DailyTime?
    fun recordTime(t: TimeLog)
    fun usedToday(kidName: String, kind: String, now: Instant): Duration
    fun overLimit(kidName: String, kind: String, now: Instant): Boolean
}

/** In-memory [IKidsBoard]. Mirrors C# `InMemoryKidsBoard`. */
class InMemoryKidsBoard : IKidsBoard {
    private val content = ConcurrentHashMap<String, KidsContent>()
    private val limits = ConcurrentHashMap<String, DailyTime>()
    private val logs = mutableListOf<TimeLog>()
    private val lock = Any()

    override fun addContent(c: KidsContent) { content[c.contentId] = c }
    override fun contentFor(band: AgeAppropriateness): List<KidsContent> =
        content.values.filter { it.ageBand == band }.sortedBy { it.title }

    override fun setLimits(d: DailyTime) { limits[d.kidName] = d }
    override fun limitsFor(kidName: String): DailyTime? = limits[kidName]

    override fun recordTime(t: TimeLog) { synchronized(lock) { logs.add(t) } }

    override fun usedToday(kidName: String, kind: String, now: Instant): Duration = synchronized(lock) {
        val today = now.atZone(ZoneOffset.UTC).toLocalDate()
        val ms = logs.filter {
            it.kidName == kidName && it.kind == kind &&
                it.atUtc.atZone(ZoneOffset.UTC).toLocalDate() == today
        }.sumOf { it.duration.toMillis() }
        Duration.ofMillis(ms)
    }

    override fun overLimit(kidName: String, kind: String, now: Instant): Boolean {
        val limit = limits[kidName] ?: return false
        val used = usedToday(kidName, kind, now)
        val cap = when {
            kind.equals("screen", ignoreCase = true) -> limit.screenLimit
            kind.equals("reading", ignoreCase = true) -> limit.readingLimit
            else -> UNCAPPED
        }
        return used > cap
    }

    private companion object {
        // C# TimeSpan.MaxValue analogue — effectively unbounded for comparison.
        val UNCAPPED: Duration = Duration.ofDays(3_650_000L)
    }
}

// =====================================================================
// DomainContext (KidsDomainContext.cs)
// =====================================================================

/** Static domain context for Kids. Mirrors C# `KidsDomainContext`. */
object KidsDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Kids] Safe, age-appropriate learning companion for children. Use simple, encouraging " +
            "language. Help with homework, creative storytelling, educational games, and curiosity questions. " +
            "Never generate inappropriate content. Validate effort, not just results. Compliance: POPIA " +
            "(children's data), COPPA-principles, Children's Act, CAPS curriculum."

    val complianceFlags: List<String> = listOf("POPIA_Childrens_Data", "COPPA_principles", "Childrens_Act", "CAPS_curriculum")

    val suggestedTools: List<String> = listOf("educational_content", "story_tools", "quiz_tools")
}

// =====================================================================
// CompanionAdapter (KidsCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Kids snippet + helpers. Mirrors C# `KidsCompanionAdapter`. */
class KidsCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${KidsDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun helpHomeworkAsync(subject: String, grade: String, question: String): String =
        inner.agentAsync("Help a Grade $grade learner with $subject homework. Question: $question. Guide with Socratic questions rather than giving the answer directly. Keep explanation simple and encouraging.")

    suspend fun tellStoryAsync(theme: String, characters: String, ageGroup: String): String =
        inner.agentAsync("Tell a short, imaginative story for age group $ageGroup. Theme: $theme. Characters: $characters. Keep it age-appropriate, with a positive lesson at the end.")

    suspend fun designActivityAsync(ageBand: String, minutes: Int, interests: String): String =
        inner.agentAsync("Design a $minutes-minute activity for $ageBand with interests: $interests. Materials, steps, learning value, mess level.")

    suspend fun explainHardConceptAsync(concept: String, ageBand: String): String =
        inner.agentAsync("Explain '$concept' to $ageBand. Use one analogy from their world, one example they've seen, one question to check understanding.")

    suspend fun screenContentAsync(contentTitle: String, ageBand: String): String =
        inner.agentAsync("Screen '$contentTitle' for $ageBand: themes, violence/language/scary moments, talk-after questions, age verdict.")

    suspend fun handleBigFeelingAsync(ageBand: String, situation: String): String =
        inner.agentAsync("Coach a parent through helping a $ageBand with big feelings about: $situation. Validate-name-co-regulate script.")
}
