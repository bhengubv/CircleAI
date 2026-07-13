// Creative.kt
//
// Kotlin port of CircleAI.Creative (CreativePrimitives.cs +
// CreativeDomainContext.cs + CreativeCompanionAdapter.cs) — the C# reference is
// the EXACT spec. A deterministic in-memory creative board: works, inspiration,
// and critiques.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTimeOffset` -> `Instant`.
//   * `WorksByTag` case-insensitive tag match.
//   * `RecentInspiration` newest-first, capped at `limit` (default 20).
//   * `AvgScore` = mean critique score for the work, 0.0 when none
//     (mirrors C# `DefaultIfEmpty(0).Average()`).

package com.bhengubv.circleai.creative

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (CreativePrimitives.cs)
// =====================================================================

/** A creative work. Mirrors C# `CreativeWork`. */
data class CreativeWork(
    val workId: String,
    val title: String,
    val medium: String,
    val author: String,
    val createdUtc: Instant,
    val tags: List<String>,
)

/** An inspiration note. Mirrors C# `Inspiration`. */
data class Inspiration(val inspirationId: String, val promptText: String, val sourceUrl: String, val seenUtc: Instant)

/** A critique of a work. Mirrors C# `Critique`. */
data class Critique(val critiqueId: String, val workId: String, val reviewer: String, val body: String, val score: Int)

/** Deterministic creative board. Mirrors C# `ICreativeBoard`. */
interface ICreativeBoard {
    fun addWork(w: CreativeWork)
    fun getWork(id: String): CreativeWork?
    fun worksByTag(tag: String): List<CreativeWork>
    fun recordInspiration(i: Inspiration)
    fun recentInspiration(limit: Int = 20): List<Inspiration>
    fun addCritique(c: Critique)
    fun avgScore(workId: String): Double
}

/** In-memory [ICreativeBoard]. Mirrors C# `InMemoryCreativeBoard`. */
class InMemoryCreativeBoard : ICreativeBoard {
    private val works = ConcurrentHashMap<String, CreativeWork>()
    private val inspiration = mutableListOf<Inspiration>()
    private val critiques = mutableListOf<Critique>()
    private val lock = Any()

    override fun addWork(w: CreativeWork) { works[w.workId] = w }
    override fun getWork(id: String): CreativeWork? = works[id]
    override fun worksByTag(tag: String): List<CreativeWork> =
        works.values.filter { w -> w.tags.any { it.equals(tag, ignoreCase = true) } }

    override fun recordInspiration(i: Inspiration) { synchronized(lock) { inspiration.add(i) } }
    override fun recentInspiration(limit: Int): List<Inspiration> = synchronized(lock) {
        inspiration.sortedByDescending { it.seenUtc }.take(limit)
    }

    override fun addCritique(c: Critique) { synchronized(lock) { critiques.add(c) } }
    override fun avgScore(workId: String): Double = synchronized(lock) {
        val scores = critiques.filter { it.workId == workId }.map { it.score.toDouble() }
        if (scores.isEmpty()) 0.0 else scores.average()
    }

    /** Number of works catalogued. */
    val workCount: Int get() = works.size

    /** Remove a work by id, cascading its critiques. Returns true if the work was present. */
    fun removeWork(workId: String): Boolean {
        val removed = works.remove(workId) != null
        if (removed) synchronized(lock) { critiques.removeAll { it.workId == workId } }
        return removed
    }

    /** Works by a given author (case-insensitive), newest-first. */
    fun worksByAuthor(author: String): List<CreativeWork> =
        works.values.filter { it.author.equals(author, ignoreCase = true) }
            .sortedByDescending { it.createdUtc }

    /** Works in a given medium (case-insensitive), newest-first. */
    fun worksByMedium(medium: String): List<CreativeWork> =
        works.values.filter { it.medium.equals(medium, ignoreCase = true) }
            .sortedByDescending { it.createdUtc }

    /**
     * The work with the highest mean critique score, or null when no critiqued work
     * still exists. Mirrors the C# group-by-workId → avg → order desc → first extant.
     */
    fun topRatedWork(): CreativeWork? = synchronized(lock) {
        critiques.groupBy { it.workId }
            .map { (workId, group) -> workId to group.map { it.score }.average() }
            .sortedByDescending { it.second }
            .firstNotNullOfOrNull { works[it.first] }
    }

    /** Every distinct tag across all works (case-insensitive), ordered case-insensitively. */
    fun allTags(): List<String> =
        works.values.flatMap { it.tags }
            .distinctBy { it.lowercase(java.util.Locale.US) }
            .sortedBy { it.lowercase(java.util.Locale.US) }
}

// =====================================================================
// DomainContext (CreativeDomainContext.cs)
// =====================================================================

/** Static domain context for Creative. Mirrors C# `CreativeDomainContext`. */
object CreativeDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Creative] Imaginative creative arts companion. Help with storytelling, poetry, " +
            "worldbuilding, visual art direction, music lyrics, creative briefs, and overcoming creative " +
            "blocks. Encourage experimentation and original voice. Compliance: Copyright Act 98/1978, POPIA."

    val complianceFlags: List<String> = listOf("Copyright_Act_98_1978", "POPIA")

    val suggestedTools: List<String> = listOf("writing_tools", "image_tools", "music_tools", "document_editor")
}

// =====================================================================
// CompanionAdapter (CreativeCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Creative snippet + helpers. Mirrors C# `CreativeCompanionAdapter`. */
class CreativeCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${CreativeDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun generateWritingPromptAsync(genre: String, mood: String): String =
        inner.agentAsync("Generate 5 unique $genre writing prompts with a $mood tone. For each, include a character seed, central conflict, and opening line.")

    suspend fun overcomeBlockAsync(project: String, blockDescription: String): String =
        inner.agentAsync("Help me overcome creative block on $project. Block: $blockDescription. Use lateral thinking techniques and suggest 3 unconventional approaches to re-ignite momentum.")

    suspend fun generateBriefAsync(project: String, audience: String, deadline: String): String =
        inner.agentAsync("Generate a creative brief for '$project' aimed at $audience, due $deadline. Include problem, success, tone, constraints, deliverables.")

    suspend fun critiqueWorkAsync(workDescription: String, criteria: String): String =
        inner.agentAsync("Critique this work: $workDescription against $criteria. Use 'I notice / I wonder / I suggest', no destructive framing.")

    suspend fun suggestStyleReferencesAsync(aesthetic: String, medium: String): String =
        inner.agentAsync("Suggest 5 style references for $aesthetic in $medium. For each: who/when/why-fits.")

    suspend fun unblockCreativeAsync(currentState: String, blocker: String): String =
        inner.agentAsync("Help unblock this creative state: $currentState. Blocker: $blocker. Offer 3 different reframes + one micro-exercise.")
}
