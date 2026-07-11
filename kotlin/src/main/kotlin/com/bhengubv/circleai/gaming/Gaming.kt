// Gaming.kt
//
// Kotlin port of CircleAI.Gaming (GamingPrimitives.cs + GamingDomainContext.cs +
// GamingCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory gaming board: titles, play sessions, and achievements.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `TimeSpan` -> `java.time.Duration`,
//     `DateTimeOffset` -> `java.time.Instant`.
//   * `TitlesByGenre` case-insensitive genre match.
//   * `TotalPlayTime` sums all sessions' durations for (user, title).
//   * `AchievementsFor` newest-first.
//   * `MostPlayed` groups sessions by title, orders by total play time DESC,
//     takes topK, maps to known titles (skipping any missing); topK<=0 throws.

package com.bhengubv.circleai.gaming

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (GamingPrimitives.cs)
// =====================================================================

/** A game title. Mirrors C# `GameTitle`. */
data class GameTitle(val titleId: String, val name: String, val genre: String, val platform: String)

/** A play session. Mirrors C# `PlaySession`. */
data class PlaySession(val sessionId: String, val userId: String, val titleId: String, val duration: Duration, val atUtc: Instant)

/** An achievement unlock. Mirrors C# `AchievementUnlock`. */
data class AchievementUnlock(val unlockId: String, val userId: String, val titleId: String, val achievement: String, val atUtc: Instant)

/** Deterministic gaming board. Mirrors C# `IGamingBoard`. */
interface IGamingBoard {
    fun addTitle(t: GameTitle)
    fun getTitle(id: String): GameTitle?
    fun titlesByGenre(genre: String): List<GameTitle>
    fun recordSession(s: PlaySession)
    fun totalPlayTime(userId: String, titleId: String): Duration
    fun unlock(u: AchievementUnlock)
    fun achievementsFor(userId: String): List<AchievementUnlock>
    fun mostPlayed(userId: String, topK: Int = 5): List<GameTitle>
}

/** In-memory [IGamingBoard]. Mirrors C# `InMemoryGamingBoard`. */
class InMemoryGamingBoard : IGamingBoard {
    private val titles = ConcurrentHashMap<String, GameTitle>()
    private val sessions = mutableListOf<PlaySession>()
    private val unlocks = mutableListOf<AchievementUnlock>()
    private val lock = Any()

    override fun addTitle(t: GameTitle) { titles[t.titleId] = t }
    override fun getTitle(id: String): GameTitle? = titles[id]
    override fun titlesByGenre(genre: String): List<GameTitle> =
        titles.values.filter { it.genre.equals(genre, ignoreCase = true) }

    override fun recordSession(s: PlaySession) { synchronized(lock) { sessions.add(s) } }

    override fun totalPlayTime(userId: String, titleId: String): Duration = synchronized(lock) {
        val ms = sessions.filter { it.userId == userId && it.titleId == titleId }
            .sumOf { it.duration.toMillis() }
        Duration.ofMillis(ms)
    }

    override fun unlock(u: AchievementUnlock) { synchronized(lock) { unlocks.add(u) } }
    override fun achievementsFor(userId: String): List<AchievementUnlock> = synchronized(lock) {
        unlocks.filter { it.userId == userId }.sortedByDescending { it.atUtc }
    }

    override fun mostPlayed(userId: String, topK: Int): List<GameTitle> {
        if (topK <= 0) throw IllegalArgumentException("topK")
        return synchronized(lock) {
            sessions.filter { it.userId == userId }
                .groupBy { it.titleId }
                .entries
                .sortedByDescending { e -> e.value.sumOf { it.duration.toMillis() } }
                .take(topK)
                .mapNotNull { titles[it.key] }
        }
    }
}

// =====================================================================
// DomainContext (GamingDomainContext.cs)
// =====================================================================

/** Static domain context for Gaming. Mirrors C# `GamingDomainContext`. */
object GamingDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Gaming] Expert gaming companion. Help with game strategy guides, build optimisation, " +
            "community event planning, game review writing, speedrun technique research, and gaming health " +
            "(screen time, ergonomics). Compliance: POPIA, WASPA (in-game purchases), child protection where " +
            "applicable."

    val complianceFlags: List<String> = listOf("POPIA", "WASPA", "Child_Protection")

    val suggestedTools: List<String> = listOf("game_db", "community_tools", "analytics", "web_search")
}

// =====================================================================
// CompanionAdapter (GamingCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Gaming snippet + helpers. Mirrors C# `GamingCompanionAdapter`. */
class GamingCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${GamingDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun buildStrategyAsync(game: String, goal: String, currentSetup: String): String =
        inner.agentAsync("Build a competitive strategy for $game. Goal: $goal. Current setup: $currentSetup. Include build recommendations, macro strategy, and key counters.")

    suspend fun writeGameReviewAsync(game: String, playtime: String, verdict: String): String =
        inner.agentAsync("Write a structured game review for $game. Playtime: $playtime. My verdict: $verdict. Include: graphics, gameplay, story, performance, value, and a score out of 10.")

    suspend fun recommendGameAsync(mood: String, platform: String, timeAvailableMin: Int): String =
        inner.agentAsync("Recommend 3 games for mood '$mood' on $platform, with $timeAvailableMin min. Mix indie/AAA, justify per pick.")

    suspend fun designSpeedrunRouteAsync(gameTitle: String, category: String): String =
        inner.agentAsync("Sketch a speedrun route outline for $gameTitle ($category). Cover key skips, glitches at high level, risk-vs-reward gates.")

    suspend fun draftPatchNotesAsync(changes: String, audience: String): String =
        inner.agentAsync("Draft patch notes for changes: $changes. Audience: $audience. Group balance/QoL/bugfix, lead with player impact.")

    suspend fun analysePlayerRetentionAsync(day1Pct: String, day7Pct: String, day30Pct: String): String =
        inner.agentAsync("Analyse retention: D1=$day1Pct, D7=$day7Pct, D30=$day30Pct. Diagnose the weakest curve segment + an experiment to lift it.")
}
