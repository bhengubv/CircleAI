// Scheduling.kt
//
// Kotlin port of the CircleAI.Hosting scheduled-task surface — the C# reference
// is the EXACT spec (CronScheduleParser.cs, CronJobModels.cs,
// IScheduledTaskStore.cs, InMemoryScheduledTaskStore.cs, ScheduledAIService.cs).
//
// CronScheduleParser is a real, deterministic 5-field parser (distinct from the
// proactive-package CronExpression — this one advances month/day/hour and uses
// DateTimeOffset day-of-week semantics where Sunday=0). Ported constant-for-
// constant. The background polling service becomes a coroutine loop.

package com.bhengubv.circleai.hosting

import com.bhengubv.circleai.memory.IAffectStore
import com.bhengubv.circleai.memory.IGoalStore
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.time.Duration
import java.time.Instant
import java.time.ZoneOffset
import java.time.ZonedDateTime
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// CronJobModels (CronJobModels.cs)
// =====================================================================

/** Delivery channel for a scheduled job's output. Mirrors C# `DeliveryTarget`. */
enum class DeliveryTarget {
    /** Deliver via in-process IAIObserver callback. */
    Local,
    /** Deliver via push notification. */
    Push,
    /** Deliver as a Telegram message. */
    Telegram,
    /** Deliver via email. */
    Email,
    /** Caller handles delivery via custom callback. */
    Custom,
}

/** State of a scheduled job's last execution. Mirrors C# `CronJobState`. */
enum class CronJobState {
    /** Job has never run. */
    Pending,
    /** Job is currently executing. */
    Running,
    /** Last run completed without error. */
    Succeeded,
    /** Last run threw an exception or the model returned an error. */
    Failed,
    /** Job has been manually paused. */
    Paused,
}

/** A named, recurring B! task with a cron schedule. Mirrors C# `CronJob` record. */
data class CronJob(
    val id: String,
    val name: String,
    val prompt: String,
    val cronExpression: String,
    val delivery: DeliveryTarget,
    val lastRunUtc: Instant? = null,
    val nextRunUtc: Instant? = null,
    val state: CronJobState = CronJobState.Pending,
    val isEnabled: Boolean = true,
)

// =====================================================================
// CronScheduleParser (CronScheduleParser.cs)
// =====================================================================

/**
 * Computes the next occurrence of a 5-field cron expression after a given
 * instant. Handles wildcards, lists, steps, and ranges. Field order:
 * `minute hour day-of-month month day-of-week` where day-of-week is 0-6
 * (0=Sunday). Ported constant-for-constant from the C# reference.
 */
object CronScheduleParser {

    /**
     * Returns the earliest UTC instant strictly after [after] that satisfies
     * [cronExpression]. Mirrors C# `GetNextOccurrence`.
     */
    fun getNextOccurrence(cronExpression: String, after: Instant): Instant {
        require(cronExpression.isNotBlank()) { "cronExpression is required" }

        val parts = cronExpression.trim().split(' ').filter { it.isNotEmpty() }
        require(parts.size == 5) {
            "Cron expression must have exactly 5 fields, got ${parts.size}: '$cronExpression'"
        }

        val minuteSet = parseField(parts[0], 0, 59)
        val hourSet = parseField(parts[1], 0, 23)
        val domSet = parseField(parts[2], 1, 31)
        val monthSet = parseField(parts[3], 1, 12)
        val dowSet = parseField(parts[4], 0, 6)

        // Start searching from the next whole minute after `after` (seconds zeroed).
        var candidate = after.atZone(ZoneOffset.UTC)
            .withSecond(0).withNano(0)
            .plusMinutes(1)

        val limit = candidate.plusYears(5)

        while (!candidate.isAfter(limit)) {
            // Month check
            if (candidate.monthValue !in monthSet) {
                candidate = advanceToNextMonth(candidate, monthSet)
                continue
            }
            // Day-of-month check
            if (candidate.dayOfMonth !in domSet) {
                candidate = candidate.plusDays(1).toLocalDate().atStartOfDay(ZoneOffset.UTC)
                continue
            }
            // Day-of-week check (Sunday=0 .. Saturday=6, matching C# (int)DayOfWeek)
            if (dayOfWeek0Sun(candidate) !in dowSet) {
                candidate = candidate.plusDays(1).toLocalDate().atStartOfDay(ZoneOffset.UTC)
                continue
            }
            // Hour check
            if (candidate.hour !in hourSet) {
                candidate = advanceToNextHour(candidate, hourSet)
                continue
            }
            // Minute check
            if (candidate.minute !in minuteSet) {
                candidate = candidate.plusMinutes(1)
                continue
            }
            // All fields match.
            return candidate.toInstant()
        }

        throw IllegalStateException(
            "No occurrence found within 5 years for cron expression '$cronExpression'.",
        )
    }

    // ── Parsing helpers ──────────────────────────────────────────────────────

    private fun parseField(field: String, min: Int, max: Int): Set<Int> {
        val result = HashSet<Int>()
        for (part in field.split(',')) {
            parsePart(part.trim(), min, max, result)
        }
        return result
    }

    private fun parsePart(part: String, min: Int, max: Int, result: MutableSet<Int>) {
        var step: Int? = null
        var core = part

        val slashIdx = part.indexOf('/')
        if (slashIdx >= 0) {
            val s = part.substring(slashIdx + 1).toIntOrNull()
            require(s != null && s >= 1) { "Invalid step in cron field part '$part'." }
            step = s
            core = part.substring(0, slashIdx)
        }

        val rangeMin: Int
        val rangeMax: Int
        if (core == "*") {
            rangeMin = min
            rangeMax = max
        } else {
            val dashIdx = core.indexOf('-')
            if (dashIdx >= 0) {
                val lo = core.substring(0, dashIdx).toIntOrNull()
                val hi = core.substring(dashIdx + 1).toIntOrNull()
                require(lo != null && hi != null) { "Invalid range in cron field part '$part'." }
                rangeMin = lo
                rangeMax = hi
            } else {
                val v = core.toIntOrNull()
                require(v != null) { "Invalid value in cron field part '$part'." }
                rangeMin = v
                rangeMax = v
            }
        }

        require(rangeMin >= min && rangeMax <= max && rangeMin <= rangeMax) {
            "Cron field value $rangeMin-$rangeMax out of range [$min,$max]."
        }

        val effectiveStep = step ?: 1
        var v = rangeMin
        while (v <= rangeMax) {
            result.add(v)
            v += effectiveStep
        }
    }

    // ── Advancement helpers ──────────────────────────────────────────────────

    private fun advanceToNextMonth(dt: ZonedDateTime, monthSet: Set<Int>): ZonedDateTime {
        var year = dt.year
        var month = dt.monthValue + 1
        if (month > 12) { month = 1; year++ }

        while (year < dt.year + 6) {
            if (month in monthSet) {
                return ZonedDateTime.of(year, month, 1, 0, 0, 0, 0, ZoneOffset.UTC)
            }
            month++
            if (month > 12) { month = 1; year++ }
        }
        throw IllegalStateException("No valid month found in cron expression.")
    }

    private fun advanceToNextHour(dt: ZonedDateTime, hourSet: Set<Int>): ZonedDateTime {
        // Try subsequent hours today.
        for (h in (dt.hour + 1)..23) {
            if (h in hourSet) {
                return ZonedDateTime.of(dt.year, dt.monthValue, dt.dayOfMonth, h, 0, 0, 0, ZoneOffset.UTC)
            }
        }
        // No valid hour today — move to next day, first valid hour.
        val nextDay = dt.plusDays(1)
        val minHour = hourSet.min()
        return ZonedDateTime.of(nextDay.year, nextDay.monthValue, nextDay.dayOfMonth, minHour, 0, 0, 0, ZoneOffset.UTC)
    }

    /** Day-of-week as Sunday=0 .. Saturday=6 (java Mon=1..Sun=7 -> Sun=0..Sat=6). */
    private fun dayOfWeek0Sun(dt: ZonedDateTime): Int = dt.dayOfWeek.value % 7
}

// =====================================================================
// IScheduledTaskStore + InMemoryScheduledTaskStore
// =====================================================================

/**
 * Persistence abstraction for [CronJob] records. All operations are suspend and
 * must be thread-safe. Mirrors C# `IScheduledTaskStore`.
 */
interface IScheduledTaskStore {
    /** Returns every registered job, regardless of enabled/disabled state. */
    suspend fun listAsync(): List<CronJob>

    /** Returns the job with the given [id], or null if not found. */
    suspend fun getAsync(id: String): CronJob?

    /** Inserts or replaces the job identified by [CronJob.id]. Returns the stored record. */
    suspend fun upsertAsync(job: CronJob): CronJob

    /** Removes the job with the given [id]. No-op if it does not exist. */
    suspend fun deleteAsync(id: String)

    /** Returns all enabled jobs whose [CronJob.nextRunUtc] is at-or-before now. */
    suspend fun getDueJobsAsync(): List<CronJob>
}

/**
 * Thread-safe, in-memory [IScheduledTaskStore]. All state is lost when the
 * process exits. Mirrors C# `InMemoryScheduledTaskStore` (ConcurrentDictionary
 * keyed by ordinal id).
 */
class InMemoryScheduledTaskStore : IScheduledTaskStore {
    private val store = ConcurrentHashMap<String, CronJob>()

    override suspend fun listAsync(): List<CronJob> = store.values.toList()

    override suspend fun getAsync(id: String): CronJob? {
        require(id.isNotBlank()) { "id is required" }
        return store[id]
    }

    override suspend fun upsertAsync(job: CronJob): CronJob {
        store[job.id] = job
        return job
    }

    override suspend fun deleteAsync(id: String) {
        require(id.isNotBlank()) { "id is required" }
        store.remove(id)
    }

    override suspend fun getDueJobsAsync(): List<CronJob> {
        val now = Instant.now()
        return store.values.filter {
            it.isEnabled && it.nextRunUtc != null && !it.nextRunUtc.isAfter(now)
        }.toList()
    }
}

// =====================================================================
// ScheduledAIService (ScheduledAIService.cs)
// =====================================================================

/** Event data emitted when a scheduled job finishes. Mirrors C# `JobCompletedEventArgs`. */
data class JobCompletedEventArgs(val job: CronJob, val response: String, val error: Throwable?)

/**
 * Runs a background loop that polls [IScheduledTaskStore] for due [CronJob]
 * records every 30 seconds, executes them via [IAIService.askAsync], and invokes
 * [onJobCompleted]. Mirrors C# `ScheduledAIService`.
 *
 * Delivery routing is left to the host via [onJobCompleted] so this layer has no
 * dependency on platform-specific notification libraries.
 */
class ScheduledAIService(
    private val butler: IAIService,
    private val store: IScheduledTaskStore,
) : AutoCloseable {

    private var scope: CoroutineScope? = null
    private var loop: Job? = null

    /**
     * Invoked on the background poll coroutine whenever a job completes (success
     * or error). Mirrors the C# `OnJobCompleted` event. Handlers must not throw;
     * thrown exceptions are caught so the loop keeps running.
     */
    var onJobCompleted: (suspend (JobCompletedEventArgs) -> Unit)? = null

    /** Starts the background polling loop. No-op when already running. */
    fun startAsync() {
        if (loop?.isActive == true) return
        val s = CoroutineScope(Dispatchers.Default + Job())
        scope = s
        loop = s.launch { runLoop() }
    }

    /** Signals the polling loop to stop and waits for it to exit. */
    suspend fun stopAsync() {
        loop?.cancelAndJoin()
        scope?.coroutineContext?.get(Job)?.cancel()
        scope = null
        loop = null
    }

    override fun close() {
        loop?.cancel()
        scope?.coroutineContext?.get(Job)?.cancel()
        scope = null
        loop = null
    }

    private suspend fun runLoop() {
        val self = scope ?: return
        while (self.isActive) {
            try {
                processDueJobs()
            } catch (ce: CancellationException) {
                throw ce
            } catch (_: Exception) {
                // Unhandled error in poll cycle — logged in C#; keep looping.
            }
            try {
                delay(POLL_INTERVAL.toMillis())
            } catch (_: CancellationException) {
                return
            }
        }
    }

    private suspend fun processDueJobs() {
        val dueJobs = store.getDueJobsAsync()
        if (dueJobs.isEmpty()) return
        for (job in dueJobs) {
            if (scope?.isActive != true) break
            executeJob(job)
        }
    }

    private suspend fun executeJob(job: CronJob) {
        val now = Instant.now()

        // Mark as Running.
        store.upsertAsync(job.copy(state = CronJobState.Running))

        var response = ""
        var error: Throwable? = null

        try {
            response = butler.askAsync(job.prompt)
        } catch (ce: CancellationException) {
            // Cancellation is not a job failure — restore previous state and rethrow.
            try { store.upsertAsync(job.copy(state = CronJobState.Pending)) } catch (_: Exception) {}
            throw ce
        } catch (ex: Exception) {
            error = ex
        }

        val nextRun = computeNextRun(job.cronExpression, now)
        val updatedState = if (error == null) CronJobState.Succeeded else CronJobState.Failed

        val updated = job.copy(
            lastRunUtc = now,
            nextRunUtc = nextRun,
            state = updatedState,
        )

        try {
            store.upsertAsync(updated)
        } catch (_: Exception) {
            // Failed to persist after execution — logged in C#; non-fatal.
        }

        // Fire event on best-effort basis — subscriber errors must not crash the loop.
        try {
            onJobCompleted?.invoke(JobCompletedEventArgs(updated, response, error))
        } catch (_: Exception) {
            // Subscriber threw — non-fatal.
        }
    }

    private fun computeNextRun(cronExpression: String, after: Instant): Instant? =
        try {
            CronScheduleParser.getNextOccurrence(cronExpression, after)
        } catch (_: Exception) {
            null
        }

    private companion object {
        val POLL_INTERVAL: Duration = Duration.ofSeconds(30)
    }
}

// =====================================================================
// Triggers (ITriggerCondition.cs, ScheduleTrigger.cs, IdleTrigger.cs)
// =====================================================================

/**
 * Context snapshot passed to trigger conditions. Mirrors C# `ProactiveContext`
 * record. [affectState] is a nullable [com.bhengubv.circleai.memory.AffectState].
 */
data class ProactiveContext(
    val userId: String,
    val nowUtc: Instant,
    val timeSinceLastInteraction: Duration,
    val affectState: com.bhengubv.circleai.memory.AffectState?,
    val activeGoals: List<com.bhengubv.circleai.memory.Goal>,
)

/** A condition that, when true, signals B! should check in proactively. Mirrors C# `ITriggerCondition`. */
interface ITriggerCondition {
    /** Stable name used for logging and deduplication. */
    val name: String

    /** Returns true when the condition is currently met. */
    suspend fun isMetAsync(context: ProactiveContext): Boolean
}

/**
 * Fires at a specific time of day. Active for a 5-minute window starting at
 * [triggerTime] and fires at most once per calendar day. Mirrors C#
 * `ScheduleTrigger`. Time comparison uses the context's local time derived from
 * [ProactiveContext.nowUtc] in the system default zone.
 */
class ScheduleTrigger(
    private val triggerTimeMinuteOfDay: Int,
    override val name: String = "schedule",
) : ITriggerCondition {

    private var lastFireDate: java.time.LocalDate? = null

    /** Convenience constructor from hour + minute. */
    constructor(hour: Int, minute: Int, name: String = "schedule") :
        this(hour * 60 + minute, name)

    /** Minute-of-day at which this trigger fires (hour*60 + minute). */
    val triggerTime: Int get() = triggerTimeMinuteOfDay

    override suspend fun isMetAsync(context: ProactiveContext): Boolean {
        // Convert NowUtc to local time for comparison (mirrors C# LocalDateTime).
        val localNow = context.nowUtc.atZone(java.time.ZoneId.systemDefault())
        val localDate = localNow.toLocalDate()
        val localMinute = localNow.hour * 60 + localNow.minute

        // Already fired today — don't fire again.
        if (lastFireDate != null && lastFireDate == localDate) return false

        val windowStart = triggerTimeMinuteOfDay
        val windowEnd = triggerTimeMinuteOfDay + 5

        val inWindow = if (windowEnd < 24 * 60) {
            // Normal case — window doesn't wrap midnight.
            localMinute >= windowStart && localMinute < windowEnd
        } else {
            // Window wraps midnight (e.g. 23:58 + 5 min).
            val wrappedEnd = windowEnd % (24 * 60)
            localMinute >= windowStart || localMinute < wrappedEnd
        }

        if (!inWindow) return false

        lastFireDate = localDate
        return true
    }
}

/**
 * Fires when [ProactiveContext.timeSinceLastInteraction] exceeds [idleThreshold].
 * Mirrors C# `IdleTrigger`. Default threshold 4 hours.
 */
class IdleTrigger(
    private val idleThreshold: Duration = Duration.ofHours(4),
) : ITriggerCondition {

    override val name: String get() = "idle"

    override suspend fun isMetAsync(context: ProactiveContext): Boolean =
        context.timeSinceLastInteraction > idleThreshold
}

// =====================================================================
// IProactiveReasoningService + ProactiveReasoningService
// =====================================================================

/**
 * Event arguments emitted when B! generates a proactive message. Mirrors C#
 * `ProactiveMessageEventArgs`.
 */
data class ProactiveMessageEventArgs(
    val userId: String,
    val message: String,
    val triggerName: String,
    val generatedUtc: Instant,
)

/**
 * Evaluates trigger conditions and, when any fires, generates a proactive
 * check-in message unprompted by the user. Mirrors C# `IProactiveReasoningService`.
 */
interface IProactiveReasoningService {
    /**
     * Evaluates all trigger conditions and, when any fires, generates a proactive
     * message and invokes [proactiveMessageReady].
     */
    suspend fun checkAsync(userId: String)

    /** Invoked when B! has something to say unprompted. Mirrors the C# event. */
    var proactiveMessageReady: (suspend (ProactiveMessageEventArgs) -> Unit)?
}

/**
 * Default [IProactiveReasoningService]. Evaluates a prioritised list of
 * [ITriggerCondition] instances and calls [IAIService.askAsync] to generate a
 * warm, goal-aware check-in when any condition fires. Only the first firing
 * trigger runs per call. Mirrors C# `ProactiveReasoningService`.
 */
class ProactiveReasoningService(
    private val butler: IAIService,
    private val goalStore: IGoalStore?,
    private val affectStore: IAffectStore?,
    private val triggers: List<ITriggerCondition>,
) : IProactiveReasoningService {

    override var proactiveMessageReady: (suspend (ProactiveMessageEventArgs) -> Unit)? = null

    override suspend fun checkAsync(userId: String) {
        require(userId.isNotBlank()) { "userId is required" }
        if (triggers.isEmpty()) return

        // 1. Load affect state.
        var affect: com.bhengubv.circleai.memory.AffectState? = null
        if (affectStore != null) {
            try {
                affect = affectStore.loadAsync(userId)
            } catch (_: Exception) { /* continue */ }
        }

        // 2. Load active goals.
        var activeGoals: List<com.bhengubv.circleai.memory.Goal> = emptyList()
        if (goalStore != null) {
            try {
                activeGoals = goalStore.getActiveAsync(userId)
            } catch (_: Exception) { /* continue */ }
        }

        // 3. Build context snapshot.
        val now = Instant.now()
        val timeSinceLast = if (affect != null) Duration.between(affect.lastUpdatedUtc, now) else Duration.ZERO

        val context = ProactiveContext(
            userId = userId,
            nowUtc = now,
            timeSinceLastInteraction = timeSinceLast,
            affectState = affect,
            activeGoals = activeGoals,
        )

        // 4. Check triggers in order — fire only the first one.
        for (trigger in triggers) {
            val met: Boolean = try {
                trigger.isMetAsync(context)
            } catch (_: Exception) {
                continue
            }
            if (!met) continue

            // 5. Build a proactive prompt.
            val prompt = buildProactivePrompt(timeSinceLast, activeGoals)

            // 6. Generate the message.
            val message: String = try {
                butler.askAsync(prompt)
            } catch (_: Exception) {
                return
            }

            // 7. Raise the event.
            val args = ProactiveMessageEventArgs(
                userId = userId,
                message = message,
                triggerName = trigger.name,
                generatedUtc = Instant.now(),
            )
            try {
                proactiveMessageReady?.invoke(args)
            } catch (_: Exception) { /* non-fatal */ }

            // Only fire one trigger per call.
            return
        }
    }

    private companion object {
        fun buildProactivePrompt(
            timeSinceLastInteraction: Duration,
            activeGoals: List<com.bhengubv.circleai.memory.Goal>,
        ): String {
            val sb = StringBuilder()
            sb.append("You are B!. ")

            val totalMinutes = timeSinceLastInteraction.seconds / 60.0
            if (totalMinutes > 5) {
                val hours = (timeSinceLastInteraction.seconds / 3600).toInt()
                val minutes = ((timeSinceLastInteraction.seconds / 60) % 60).toInt()
                if (hours > 0) {
                    sb.append("The user has been away for approximately $hours hour${if (hours == 1) "" else "s"}. ")
                } else {
                    sb.append("The user has been away for approximately $minutes minute${if (minutes == 1) "" else "s"}. ")
                }
            }

            if (activeGoals.isNotEmpty()) {
                sb.append("They have ${activeGoals.size} active goal${if (activeGoals.size == 1) "" else "s"}: ")
                for (i in activeGoals.indices) {
                    sb.append('"')
                    sb.append(activeGoals[i].title)
                    sb.append('"')
                    if (i < activeGoals.size - 1) sb.append(", ")
                }
                sb.append(". ")
            }

            sb.append("Generate a brief, friendly check-in message (1-2 sentences). ")
            sb.append("Be warm, specific to their goals if you know them, and not intrusive.")
            return sb.toString()
        }
    }
}
