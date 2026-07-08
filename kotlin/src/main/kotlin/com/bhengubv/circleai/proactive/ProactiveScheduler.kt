// ProactiveScheduler.kt
//
// Kotlin port of the CircleAI.Companion.Proactive scheduler surface — the C#
// reference is the EXACT spec (ProactiveScheduler.cs, NullImplementations.cs,
// ProactiveSchedulerBackgroundService.cs).
//
// ProactiveScheduler owns cron parsing, per-(context,taskId) last-run tracking,
// refresh, tick, event dispatch, and manual run-by-id. Null / in-memory / delegate
// backings mirror the C# safe defaults. The background service becomes a coroutine
// loop that refreshes once at startup then ticks each interval.

package com.bhengubv.circleai.proactive

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.time.Duration
import java.time.Instant

// =====================================================================
// ProactiveScheduler — default IProactiveScheduler.
// =====================================================================

/**
 * Default [IProactiveScheduler]. Singleton-safe. Refresh / tick is the contract;
 * the background service ticks every minute by default. Per-[ProactiveTask.sourceContext]
 * last-run tracking keeps multi-tenant hosts' schedules separate.
 */
class ProactiveScheduler(
    private val source: IProactiveTaskSource,
    private val runner: IProactiveTaskRunner,
) : IProactiveScheduler {

    private val gate = Any()
    private var tasksInternal: MutableList<ProactiveTask> = ArrayList()
    private var errorsInternal: MutableList<ProactiveTaskLoadError> = ArrayList()

    // Per-(context, taskId) last-run map. Context = sourceContext or "" if null.
    // Both context and id keys are compared case-insensitively (lower-cased).
    private val lastRuns = HashMap<String, HashMap<String, Instant>>()

    override val backendId: String get() = "default"

    override val tasks: List<ProactiveTask>
        get() = synchronized(gate) { tasksInternal.toList() }

    override val loadErrors: List<ProactiveTaskLoadError>
        get() = synchronized(gate) { errorsInternal.toList() }

    override fun getNextRun(task: ProactiveTask, after: Instant): Instant? {
        val cron = task.trigger.cron ?: return null
        return try {
            CronExpression.parse(cron).getNextOccurrence(after)
        } catch (_: Exception) {
            null
        }
    }

    override suspend fun refreshAsync() {
        val snapshot = source.getTasksAsync()
        val errors = source.getErrorsAsync()

        synchronized(gate) {
            tasksInternal = snapshot.toMutableList()
            errorsInternal = errors.toMutableList()

            // Drop last-run state for (context, taskId) pairs the source no longer
            // reports — prevents unbounded growth as tasks come and go.
            val live = tasksInternal
                .map { contextKey(it.sourceContext) to it.id.lowercase() }
                .toHashSet()

            for (ctxKey in lastRuns.keys.toList()) {
                val ids = lastRuns[ctxKey]!!
                for (id in ids.keys.toList()) {
                    if ((ctxKey to id) !in live) ids.remove(id)
                }
                if (ids.isEmpty()) lastRuns.remove(ctxKey)
            }
        }
    }

    override suspend fun tickAsync(now: Instant) {
        val candidates: List<ProactiveTask>
        synchronized(gate) {
            candidates = tasksInternal.filter { it.trigger.cron != null }
        }

        for (task in candidates) {
            val ctxKey = contextKey(task.sourceContext)
            val idKey = task.id.lowercase()
            val lastRun: Instant
            synchronized(gate) {
                val map = lastRuns.getOrPut(ctxKey) { HashMap() }
                lastRun = map[idKey] ?: Instant.MIN
            }

            try {
                val expr = CronExpression.parse(task.trigger.cron!!)
                val anchor = if (lastRun == Instant.MIN) now.minus(Duration.ofMinutes(1)) else lastRun
                val next = expr.getNextOccurrence(anchor)
                if (!next.isAfter(now)) {
                    runner.runAsync(task, null)
                    markRun(task, now)
                }
            } catch (_: Exception) {
                // Parse error — already surfaced via LoadErrors at the source layer.
                // Skip this task; don't crash the tick.
            }
        }
    }

    override suspend fun dispatchEventAsync(eventName: String, variables: Map<String, String>?) {
        require(eventName.isNotBlank()) { "eventName required" }

        val matched: List<ProactiveTask>
        synchronized(gate) {
            matched = tasksInternal.filter { it.trigger.onEvent.equals(eventName, ignoreCase = true) }
        }

        for (task in matched) {
            runner.runAsync(task, variables)
            markRun(task, Instant.now())
        }
    }

    override suspend fun runByIdAsync(id: String, variables: Map<String, String>?): ProactiveTaskRunResult {
        require(id.isNotBlank()) { "id required" }

        val task: ProactiveTask?
        synchronized(gate) {
            task = tasksInternal.firstOrNull { it.id.equals(id, ignoreCase = true) }
        }

        if (task == null) {
            return ProactiveTaskRunResult(id, success = false, failureMessage = "No task with id '$id'.")
        }

        val result = runner.runAsync(task, variables)
        markRun(task, Instant.now())
        return result
    }

    private fun markRun(task: ProactiveTask, whenAt: Instant) {
        val ctxKey = contextKey(task.sourceContext)
        synchronized(gate) {
            lastRuns.getOrPut(ctxKey) { HashMap() }[task.id.lowercase()] = whenAt
        }
    }

    private companion object {
        fun contextKey(sourceContext: String?): String = (sourceContext ?: "").lowercase()
    }
}

// =====================================================================
// Null / in-memory / delegate backings (NullImplementations.cs)
// =====================================================================

/** Empty source — no tasks, no errors. */
class NullProactiveTaskSource private constructor() : IProactiveTaskSource {
    override val backendId: String get() = "null"
    override suspend fun getTasksAsync(): List<ProactiveTask> = emptyList()
    override suspend fun getErrorsAsync(): List<ProactiveTaskLoadError> = emptyList()

    companion object {
        val Instance = NullProactiveTaskSource()
    }
}

/**
 * Reports every run as a failure with a "no runner registered" message. Fail-closed
 * default so a host that forgot to wire a real runner notices on first fire rather
 * than silently doing nothing.
 */
class NullProactiveTaskRunner private constructor() : IProactiveTaskRunner {
    override val backendId: String get() = "null"
    override suspend fun runAsync(task: ProactiveTask, variables: Map<String, String>?): ProactiveTaskRunResult =
        ProactiveTaskRunResult(
            taskId = task.id,
            success = false,
            failureMessage = "No IProactiveTaskRunner registered; using NullProactiveTaskRunner.",
        )

    companion object {
        val Instance = NullProactiveTaskRunner()
    }
}

/**
 * In-memory source for testing + simple consumers. Add / remove tasks; the
 * scheduler picks up changes on next [IProactiveScheduler.refreshAsync]. Keyed by
 * (sourceContext, id) so multi-tenant hosts can hold the same id in two contexts.
 */
class InMemoryProactiveTaskSource : IProactiveTaskSource {
    private data class Key(val ctx: String, val id: String)

    private val gate = Any()
    private val byKey = LinkedHashMap<Key, ProactiveTask>()
    private val errors = ArrayList<ProactiveTaskLoadError>()

    override val backendId: String get() = "in-memory"

    fun upsert(task: ProactiveTask) {
        synchronized(gate) { byKey[keyOf(task)] = task }
    }

    fun remove(id: String, sourceContext: String? = null): Boolean {
        require(id.isNotBlank()) { "id required" }
        synchronized(gate) { return byKey.remove(Key((sourceContext ?: "").lowercase(), id.lowercase())) != null }
    }

    fun clear() {
        synchronized(gate) {
            byKey.clear()
            errors.clear()
        }
    }

    fun recordError(error: ProactiveTaskLoadError) {
        synchronized(gate) { errors.add(error) }
    }

    override suspend fun getTasksAsync(): List<ProactiveTask> =
        synchronized(gate) { byKey.values.toList() }

    override suspend fun getErrorsAsync(): List<ProactiveTaskLoadError> =
        synchronized(gate) { errors.toList() }

    private companion object {
        fun keyOf(task: ProactiveTask): Key =
            Key((task.sourceContext ?: "").lowercase(), task.id.lowercase())
    }
}

/**
 * Runner that hands every task off to a host-supplied lambda. Useful for hosts
 * whose tasks don't need a structured runner — just "given a task, run something."
 */
class DelegateProactiveTaskRunner(
    private val handler: suspend (ProactiveTask, Map<String, String>?) -> ProactiveTaskRunResult,
) : IProactiveTaskRunner {
    override val backendId: String get() = "delegate"
    override suspend fun runAsync(task: ProactiveTask, variables: Map<String, String>?): ProactiveTaskRunResult =
        handler(task, variables)
}

// =====================================================================
// Background service (ProactiveSchedulerBackgroundService.cs)
// =====================================================================

/** Tunable knobs for the background tick loop. */
data class ProactiveSchedulerOptions(
    /** How often the scheduler ticks. Default 1 minute. */
    val tickInterval: Duration = Duration.ofMinutes(1),
    /** How often the source is re-snapshotted. Default 5 minutes. */
    val refreshInterval: Duration = Duration.ofMinutes(5),
)

/**
 * Hosted service that refreshes the scheduler once at startup, then loops on the
 * tick timer calling [IProactiveScheduler.tickAsync] (re-refreshing every
 * [ProactiveSchedulerOptions.refreshInterval]). [start]/[stop] manage the loop.
 */
class ProactiveSchedulerBackgroundService(
    private val scheduler: IProactiveScheduler,
    private val options: ProactiveSchedulerOptions = ProactiveSchedulerOptions(),
) {
    private var scope: CoroutineScope? = null
    private var loop: Job? = null

    fun start() {
        if (scope != null) return
        val s = CoroutineScope(Dispatchers.Default + Job())
        scope = s
        loop = s.launch { executeAsync() }
    }

    suspend fun stop() {
        loop?.cancelAndJoin()
        scope?.coroutineContext?.get(Job)?.cancel()
        scope = null
        loop = null
    }

    private suspend fun executeAsync() {
        val self = scope ?: return

        // Initial refresh — populate the scheduler before the first tick.
        try {
            scheduler.refreshAsync()
        } catch (ex: Exception) {
            System.err.println("[ProactiveScheduler] initial refresh failed: ${ex.message}")
        }

        var lastRefresh = Instant.now()

        while (self.isActive) {
            try {
                delay(options.tickInterval.toMillis())
            } catch (_: Exception) {
                return
            }

            val now = Instant.now()
            try {
                if (Duration.between(lastRefresh, now) >= options.refreshInterval) {
                    scheduler.refreshAsync()
                    lastRefresh = now
                }
                scheduler.tickAsync(now)
            } catch (ex: Exception) {
                System.err.println("[ProactiveScheduler] tick failed; will retry: ${ex.message}")
            }
        }
    }
}
