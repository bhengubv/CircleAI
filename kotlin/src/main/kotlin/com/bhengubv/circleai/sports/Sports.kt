// Sports.kt
//
// Kotlin port of CircleAI.Sports (SportsPrimitives.cs + SportsDomainContext.cs +
// SportsCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory sports board: activities, personal bests, and
// training sessions.
//
// Fidelity notes:
//   * C# `enum DistanceKind`         -> Kotlin `enum class`.
//   * C# `record`                    -> Kotlin `data class`; `TimeSpan` ->
//     `java.time.Duration`, `DateTimeOffset` -> `java.time.Instant`.
//   * `History` newest-first, capped at `limit` (default 50; limit<=0 throws).
//   * `TotalKmThisWeek` sums distance from the start of the week (Sunday, per
//     `now.DayOfWeek` where Sunday=0) — reproduced with a Sunday-based week start.
//   * `Best` picks the fastest activity at/above the distance, projecting a
//     `PersonalBest`.
//   * `Complete` flips `Completed` (unknown id throws).
//   * `Upcoming` = not-completed, future (UTC now), for the user, ASC.

package com.bhengubv.circleai.sports

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
// Primitives (SportsPrimitives.cs)
// =====================================================================

/** Kind of distance activity. Mirrors C# `DistanceKind`. */
enum class DistanceKind { Run, Bike, Swim, Walk, Row }

/** A logged activity. Mirrors C# `Activity`. */
data class Activity(
    val activityId: String,
    val userId: String,
    val kind: DistanceKind,
    val distanceKm: Double,
    val duration: Duration,
    val atUtc: Instant,
)

/** A personal best. Mirrors C# `PersonalBest`. */
data class PersonalBest(
    val userId: String,
    val kind: DistanceKind,
    val distanceKm: Double,
    val time: Duration,
    val achievedUtc: Instant,
)

/** A scheduled training session. Mirrors C# `TrainingSession`. */
data class TrainingSession(
    val sessionId: String,
    val userId: String,
    val plan: String,
    val scheduledUtc: Instant,
    val completed: Boolean,
)

/** Deterministic sports board. Mirrors C# `ISportsBoard`. */
interface ISportsBoard {
    fun log(a: Activity)
    fun history(userId: String, limit: Int = 50): List<Activity>
    fun totalKmThisWeek(userId: String, kind: DistanceKind, now: Instant): Double
    fun best(userId: String, kind: DistanceKind, distanceKm: Double): PersonalBest?
    fun schedule(s: TrainingSession)
    fun complete(sessionId: String)
    fun upcoming(userId: String): List<TrainingSession>
}

/** In-memory [ISportsBoard]. Mirrors C# `InMemorySportsBoard`. */
class InMemorySportsBoard : ISportsBoard {
    private val activities = mutableListOf<Activity>()
    private val sessions = ConcurrentHashMap<String, TrainingSession>()
    private val lock = Any()

    override fun log(a: Activity) { synchronized(lock) { activities.add(a) } }

    override fun history(userId: String, limit: Int): List<Activity> {
        if (limit <= 0) throw IllegalArgumentException("limit")
        return synchronized(lock) {
            activities.filter { it.userId == userId }.sortedByDescending { it.atUtc }.take(limit)
        }
    }

    override fun totalKmThisWeek(userId: String, kind: DistanceKind, now: Instant): Double {
        val weekStart = weekStartOf(now)
        return synchronized(lock) {
            activities.filter { it.userId == userId && it.kind == kind && !it.atUtc.isBefore(weekStart) }
                .sumOf { it.distanceKm }
        }
    }

    override fun best(userId: String, kind: DistanceKind, distanceKm: Double): PersonalBest? = synchronized(lock) {
        val hit = activities.filter { it.userId == userId && it.kind == kind && it.distanceKm >= distanceKm }
            .minByOrNull { it.duration }
        if (hit == null) null else PersonalBest(userId, kind, distanceKm, hit.duration, hit.atUtc)
    }

    override fun schedule(s: TrainingSession) { sessions[s.sessionId] = s }

    override fun complete(sessionId: String) {
        val s = sessions[sessionId] ?: throw IllegalStateException("Unknown session $sessionId")
        sessions[sessionId] = s.copy(completed = true)
    }

    override fun upcoming(userId: String): List<TrainingSession> {
        val now = Instant.now()
        return sessions.values
            .filter { it.userId == userId && !it.completed && !it.scheduledUtc.isBefore(now) }
            .sortedBy { it.scheduledUtc }
    }

    private companion object {
        /** Start-of-week (midnight UTC) using C#'s Sunday=0 DayOfWeek convention. */
        fun weekStartOf(now: Instant): Instant {
            val date = now.atZone(ZoneOffset.UTC).toLocalDate()
            // java DayOfWeek: MON=1..SUN=7; C# DayOfWeek: SUN=0..SAT=6.
            val csharpDow = date.dayOfWeek.value % 7 // SUN->0, MON->1, ... SAT->6
            return date.minusDays(csharpDow.toLong()).atStartOfDay(ZoneOffset.UTC).toInstant()
        }
    }
}

// =====================================================================
// DomainContext (SportsDomainContext.cs)
// =====================================================================

/** Static domain context for Sports. Mirrors C# `SportsDomainContext`. */
object SportsDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Sports] Expert sports management and performance assistant. Help with training programme " +
            "design, athlete nutrition guidance, club administration, fixture scheduling, performance data " +
            "analysis, and sports event management. Apply periodisation and load management principles. " +
            "Compliance: WADA anti-doping rules, SASCOC, Sport and Recreation SA, POPIA."

    val complianceFlags: List<String> = listOf("WADA", "SASCOC", "Sport_Recreation_SA", "POPIA")

    val suggestedTools: List<String> = listOf("performance_tracker", "analytics", "schedule_manager", "document_editor")
}

// =====================================================================
// CompanionAdapter (SportsCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Sports snippet + helpers. Mirrors C# `SportsCompanionAdapter`. */
class SportsCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${SportsDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun designTrainingProgramAsync(sport: String, athleteProfile: String, goal: String, weeks: Int): String =
        inner.agentAsync("Design a $weeks-week periodised training programme for $sport. Athlete: $athleteProfile. Goal: $goal. Include weekly volume, intensity zones, key sessions, and recovery weeks.")

    suspend fun analysePerformanceAsync(athleteData: String): String =
        inner.agentAsync("Analyse this athlete performance data and identify strengths, weaknesses, and priority interventions:\n$athleteData")

    suspend fun designTrainingBlockAsync(sport: String, targetEvent: String, weeks: Int): String =
        inner.agentAsync("Design a $weeks-week training block for $sport peaking at $targetEvent. Periodisation, key sessions, tapers.")

    suspend fun analysePerformanceAsync(sport: String, recentResults: String, keyMetrics: String): String =
        inner.agentAsync("Analyse recent $sport performance: $recentResults. Key metrics: $keyMetrics. Strengths to lean into, gaps to close.")

    suspend fun planRecoveryAsync(sessionIntensity: String, daysUntilNext: String): String =
        inner.agentAsync("Plan recovery between sessions: $sessionIntensity, $daysUntilNext days. Nutrition, sleep, mobility, modality picks.")

    suspend fun draftPostMatchReportAsync(match: String, keyMoments: String): String =
        inner.agentAsync("Draft a post-match report on $match. Key moments: $keyMoments. Tactical, individual standouts, areas to drill.")
}
