// Fitness.kt
//
// Kotlin port of CircleAI.Fitness (FitnessPrimitives.cs + FitnessDomainContext.cs +
// FitnessCompanionAdapter.cs) — the C# reference is the EXACT spec. A
// deterministic in-memory fitness board: workouts, goals, and exercise sets.
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`; `DateTime`/`DateTimeOffset` -> `Instant`.
//   * `WorkoutsThisWeek` = from Sunday-based week start, ASC.
//   * `TotalCaloriesSince` sums calories at/after `since`.
//   * `GoalsFor` / `SetsFor` are simple filtered snapshots.

package com.bhengubv.circleai.fitness

import com.bhengubv.circleai.companion.CompanionContext
import com.bhengubv.circleai.companion.CompanionProactiveEvent
import com.bhengubv.circleai.companion.CompanionTurn
import com.bhengubv.circleai.companion.ICompanionSession
import com.bhengubv.circleai.companion.InterfaceKind
import kotlinx.coroutines.flow.Flow
import java.time.Instant
import java.time.ZoneOffset
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Primitives (FitnessPrimitives.cs)
// =====================================================================

/** A logged workout. Mirrors C# `Workout`. */
data class Workout(
    val workoutId: String,
    val userId: String,
    val kind: String,
    val durationMinutes: Int,
    val caloriesBurned: Double,
    val atUtc: Instant,
)

/** A fitness goal. Mirrors C# `FitnessGoal`. */
data class FitnessGoal(val goalId: String, val userId: String, val metric: String, val target: Double, val dueOn: Instant)

/** One exercise set. Mirrors C# `ExerciseSet`. */
data class ExerciseSet(val setId: String, val workoutId: String, val exercise: String, val reps: Int, val weightKg: Double)

/** Deterministic fitness board. Mirrors C# `IFitnessBoard`. */
interface IFitnessBoard {
    fun log(w: Workout)
    fun workoutsThisWeek(userId: String, now: Instant): List<Workout>
    fun totalCaloriesSince(userId: String, since: Instant): Double
    fun setGoal(g: FitnessGoal)
    fun goalsFor(userId: String): List<FitnessGoal>
    fun addSet(s: ExerciseSet)
    fun setsFor(workoutId: String): List<ExerciseSet>
}

/** In-memory [IFitnessBoard]. Mirrors C# `InMemoryFitnessBoard`. */
class InMemoryFitnessBoard : IFitnessBoard {
    private val workouts = mutableListOf<Workout>()
    private val goals = ConcurrentHashMap<String, FitnessGoal>()
    private val sets = mutableListOf<ExerciseSet>()
    private val lock = Any()

    override fun log(w: Workout) { synchronized(lock) { workouts.add(w) } }

    override fun workoutsThisWeek(userId: String, now: Instant): List<Workout> {
        val weekStart = weekStartOf(now)
        return synchronized(lock) {
            workouts.filter { it.userId == userId && !it.atUtc.isBefore(weekStart) }.sortedBy { it.atUtc }
        }
    }

    override fun totalCaloriesSince(userId: String, since: Instant): Double = synchronized(lock) {
        workouts.filter { it.userId == userId && !it.atUtc.isBefore(since) }.sumOf { it.caloriesBurned }
    }

    override fun setGoal(g: FitnessGoal) { goals[g.goalId] = g }
    override fun goalsFor(userId: String): List<FitnessGoal> = goals.values.filter { it.userId == userId }

    override fun addSet(s: ExerciseSet) { synchronized(lock) { sets.add(s) } }
    override fun setsFor(workoutId: String): List<ExerciseSet> = synchronized(lock) {
        sets.filter { it.workoutId == workoutId }
    }

    private companion object {
        fun weekStartOf(now: Instant): Instant {
            val date = now.atZone(ZoneOffset.UTC).toLocalDate()
            val csharpDow = date.dayOfWeek.value % 7
            return date.minusDays(csharpDow.toLong()).atStartOfDay(ZoneOffset.UTC).toInstant()
        }
    }
}

// =====================================================================
// DomainContext (FitnessDomainContext.cs)
// =====================================================================

/** Static domain context for Fitness. Mirrors C# `FitnessDomainContext`. */
object FitnessDomainContext {
    const val SYSTEM_PROMPT_SNIPPET: String =
        "[DOMAIN: Fitness] Personal fitness coach companion. Help with training programme design, workout " +
            "planning, recovery protocols, nutritional timing, and progress analysis. Apply evidence-based " +
            "exercise science principles. Not a medical service. Compliance: HPCSA fitness guidelines, POPIA."

    val complianceFlags: List<String> = listOf("HPCSA_Fitness", "POPIA", "Not_Medical_Advice")

    val suggestedTools: List<String> = listOf("fitness_tracker", "exercise_db", "nutrition_tools", "analytics")
}

// =====================================================================
// CompanionAdapter (FitnessCompanionAdapter.cs)
// =====================================================================

/** Wraps an [ICompanionSession] with the Fitness snippet + helpers. Mirrors C# `FitnessCompanionAdapter`. */
class FitnessCompanionAdapter(private val inner: ICompanionSession) : ICompanionSession {
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

    private fun enrich(m: String): String = "${FitnessDomainContext.SYSTEM_PROMPT_SNIPPET}\n\n$m"

    suspend fun designWorkoutAsync(goal: String, equipment: String, level: String, daysPerWeek: Int): String =
        inner.agentAsync("Design a $daysPerWeek-day/week workout programme. Goal: $goal. Equipment: $equipment. Level: $level. Include warm-up, main sets with reps/sets/rest, and cool-down.")

    suspend fun analyseProgressAsync(metrics: String): String =
        inner.agentAsync("Analyse my fitness progress and recommend programme adjustments:\n$metrics")

    suspend fun designWorkoutPlanAsync(goal: String, availableTime: String, equipment: String): String =
        inner.agentAsync("Design a workout plan for goal '$goal', $availableTime per session, equipment: $equipment. Periodise over 4 weeks.")

    suspend fun analysePersonalBestProgressionAsync(exercise: String, historyJson: String): String =
        inner.agentAsync("Analyse PB progression in $exercise: $historyJson. Identify plateaus, recommend deload + next mesocycle target.")

    suspend fun suggestRecoveryProtocolAsync(sorenessNotes: String, sleepAvgHours: String): String =
        inner.agentAsync("Suggest recovery protocol for soreness: $sorenessNotes, avg sleep ${sleepAvgHours}h. Cover mobility, nutrition, sleep, deload.")

    suspend fun critiqueFormCueAsync(exercise: String, formDescription: String): String =
        inner.agentAsync("Critique form for $exercise: $formDescription. Identify the 2 highest-leverage cues to fix first.")
}
