// Proactive.kt
//
// Kotlin port of the CircleAI.Companion.Proactive project — the C# reference is
// the EXACT spec (Primitives.cs, Contracts.cs, CronExpression.cs). Primitives +
// contract surface for proactive scheduling: a source (where tasks come from), a
// runner (how one executes), and a scheduler (when they fire). Plus the standalone
// 5-field cron parser.

package com.bhengubv.circleai.proactive

import java.time.Instant
import java.time.ZoneOffset

// =====================================================================
// Primitives (Primitives.cs)
// =====================================================================

/**
 * How a task fires. Exactly one of [cron], [onEvent], or [manual] is non-null/true.
 *
 * @param cron 5-field cron expression — see [CronExpression].
 * @param onEvent Event name (e.g. "note-saved", "task-created").
 * @param manual True if the task only fires when explicitly invoked.
 */
data class ProactiveTrigger(
    val cron: String? = null,
    val onEvent: String? = null,
    val manual: Boolean = false,
)

/**
 * One scheduled task. Opaque from the substrate's perspective — the host's
 * [IProactiveTaskRunner] reads the [payload] and executes it. Mirrors C# `ProactiveTask`.
 *
 * @param id Unique task id within its source. Used for last-run tracking.
 * @param trigger Cron / event / manual trigger.
 * @param payload Consumer-owned object. Substrate never inspects it.
 * @param sourceContext Optional context tag (vault path, tenant id, …) so multi-tenant
 *   sources keep per-context last-run state separate.
 */
data class ProactiveTask(
    val id: String,
    val trigger: ProactiveTrigger,
    val payload: Any,
    val sourceContext: String? = null,
)

/** One run outcome — success or failure with a message. Mirrors C# `ProactiveTaskRunResult`. */
data class ProactiveTaskRunResult(
    val taskId: String,
    val success: Boolean,
    val failureMessage: String? = null,
)

/** One parse failure surfaced through the source. Mirrors C# `ProactiveTaskLoadError`. */
data class ProactiveTaskLoadError(
    val taskId: String,
    val message: String,
    val sourceContext: String? = null,
)

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/**
 * Where the active set of tasks comes from. Refreshed via [getTasksAsync] on
 * every scheduler refresh / tick.
 */
interface IProactiveTaskSource {
    /** Backend self-identification — "vault-fs", "in-memory", "null". */
    val backendId: String

    /** Snapshot the current set of tasks. */
    suspend fun getTasksAsync(): List<ProactiveTask>

    /** Any parse / load failures surfaced from the last refresh. */
    suspend fun getErrorsAsync(): List<ProactiveTaskLoadError>
}

/**
 * Executes one task. The substrate hands the task back; the consumer reads
 * [ProactiveTask.payload] and runs it. [variables] carry trigger-time context
 * (event payload, manual-invoke args, …).
 */
interface IProactiveTaskRunner {
    /** Backend self-identification — "workflow-engine", "delegate", "null". */
    val backendId: String

    suspend fun runAsync(
        task: ProactiveTask,
        variables: Map<String, String>? = null,
    ): ProactiveTaskRunResult
}

/**
 * The scheduling loop. Owns cron parsing + last-run tracking + event dispatch.
 * Ticked once a minute by [ProactiveSchedulerBackgroundService].
 */
interface IProactiveScheduler {
    /** Backend self-identification. */
    val backendId: String

    /** Current snapshot — populated by [refreshAsync]. */
    val tasks: List<ProactiveTask>

    /** Any load errors from the source. */
    val loadErrors: List<ProactiveTaskLoadError>

    /**
     * Next cron firing for a task. Returns null for non-cron triggers or
     * unparseable expressions.
     */
    fun getNextRun(task: ProactiveTask, after: Instant): Instant?

    /**
     * Re-snapshot tasks from the source. Drops state for tasks the source no
     * longer reports; leaves last-run state for surviving tasks intact.
     */
    suspend fun refreshAsync()

    /**
     * Tick. Run every task whose cron next-run is at-or-before [now] and that
     * hasn't already fired for the matching minute.
     */
    suspend fun tickAsync(now: Instant)

    /** Fire every event-triggered task matching the event name. */
    suspend fun dispatchEventAsync(eventName: String, variables: Map<String, String>? = null)

    /** One-shot manual run by task id. */
    suspend fun runByIdAsync(id: String, variables: Map<String, String>? = null): ProactiveTaskRunResult
}

// =====================================================================
// CronExpression (CronExpression.cs)
// =====================================================================

/**
 * Five-field cron expression parser: `minute hour day-of-month month day-of-week`.
 * Supports `*`, integers, ranges (`1-5`), lists (`1,15,30`), and step values
 * (star-slash-15). Day-of-week uses 0=Sunday through 6=Saturday. Ported
 * constant-for-constant from the C# reference (lifted from CircleUp's CronExpression).
 */
class CronExpression private constructor(
    private val minutes: Set<Int>,
    private val hours: Set<Int>,
    private val daysOfMonth: Set<Int>,
    private val months: Set<Int>,
    private val daysOfWeek: Set<Int>,
) {

    /**
     * Next UTC time at or after [after] when the expression matches. Hard upper
     * bound of one year forward — if nothing matches in 365 days the expression
     * is effectively dead and this throws rather than spinning.
     */
    fun getNextOccurrence(after: Instant): Instant {
        // t = after + 1 minute, truncated to the minute (seconds/nanos zeroed).
        var t = after.plusSeconds(60)
        t = truncateToMinute(t)
        val limit = t.atZone(ZoneOffset.UTC).plusYears(1).toInstant()
        while (!t.isAfter(limit)) {
            if (matches(t)) return t
            t = t.plusSeconds(60)
        }
        throw IllegalStateException("Cron expression does not match any time in the next year.")
    }

    fun matches(moment: Instant): Boolean {
        val z = moment.atZone(ZoneOffset.UTC)
        if (z.minute !in minutes) return false
        if (z.hour !in hours) return false
        if (z.dayOfMonth !in daysOfMonth) return false
        if (z.monthValue !in months) return false
        // Day-of-month AND day-of-week must both match (C# settles on AND for
        // predictability). DayOfWeek: Sunday=0 .. Saturday=6.
        val dow = z.dayOfWeek.value % 7 // java Mon=1..Sun=7 -> Sun=0..Sat=6
        if (dow !in daysOfWeek) return false
        return true
    }

    companion object {
        fun parse(expression: String): CronExpression {
            val fields = expression.split(' ')
                .map { it.trim() }
                .filter { it.isNotEmpty() }
            if (fields.size != 5) {
                throw IllegalArgumentException(
                    "Cron expression must have 5 fields, got ${fields.size}: '$expression'",
                )
            }
            return CronExpression(
                parseField(fields[0], 0, 59),
                parseField(fields[1], 0, 23),
                parseField(fields[2], 1, 31),
                parseField(fields[3], 1, 12),
                parseField(fields[4], 0, 6),
            )
        }

        private fun truncateToMinute(t: Instant): Instant {
            val z = t.atZone(ZoneOffset.UTC).withSecond(0).withNano(0)
            return z.toInstant()
        }

        private fun parseField(field: String, min: Int, max: Int): Set<Int> {
            val values = HashSet<Int>()
            for (part in field.split(',')) {
                expandPart(part.trim(), min, max, values)
            }
            if (values.isEmpty()) {
                throw IllegalArgumentException("Cron field '$field' resolved to no values.")
            }
            return values
        }

        private fun expandPart(partIn: String, min: Int, max: Int, sink: MutableSet<Int>) {
            var part = partIn
            var step = 1
            val slash = part.indexOf('/')
            if (slash >= 0) {
                val stepStr = part.substring(slash + 1)
                val parsed = stepStr.toIntOrNull()
                if (parsed == null || parsed <= 0) {
                    throw IllegalArgumentException("Cron step '$part' is not a positive integer.")
                }
                step = parsed
                part = part.substring(0, slash)
            }

            val rangeStart: Int
            val rangeEnd: Int
            if (part == "*") {
                rangeStart = min
                rangeEnd = max
            } else if (part.contains('-')) {
                val dash = part.indexOf('-')
                rangeStart = part.substring(0, dash).toInt()
                rangeEnd = part.substring(dash + 1).toInt()
            } else {
                rangeStart = part.toInt()
                rangeEnd = rangeStart
            }

            if (rangeStart < min || rangeEnd > max || rangeStart > rangeEnd) {
                throw IllegalArgumentException("Cron part '$part' out of range [$min,$max].")
            }

            var v = rangeStart
            while (v <= rangeEnd) {
                sink.add(v)
                v += step
            }
        }
    }
}
