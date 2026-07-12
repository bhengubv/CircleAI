// OrchestrationSwarm.kt
//
// Kotlin port of the host-side orchestration layer from CircleAI.Orchestration
// (LokiOrchestrator.cs, IncidentTrigger.cs, SecurityOrchestrationBridge.cs).
//
// LokiOrchestrator drives a semaphore-bounded swarm of AgentTasks through an
// IAgentDispatcher, enforcing a quality gate on every result. IncidentTrigger
// maps memory/anomaly signals into AgentTasks. SecurityOrchestrationBridge
// wraps an ISecurityWatchdog so anomalies ALSO dispatch an ops-security agent,
// in parallel with the immediate immune-system response.
//
// C# -> Kotlin conventions:
//   IAsyncEnumerable<SwarmResult>   -> kotlinx.coroutines.flow.Flow<SwarmResult>
//   SemaphoreSlim(maxConcurrency)   -> kotlinx.coroutines.sync.Semaphore
//   CancellationTokenSource+timeout -> withTimeoutOrNull(taskTimeout)
//   record `with { ... }`           -> data class copy(...)
//   Task.ContinueWith (fire+forget) -> scope.launch { runCatching { ... } }

package com.bhengubv.circleai.orchestration

import com.bhengubv.circleai.memory.EpisodicMemoryEntry
import com.bhengubv.circleai.security.AnomalySignal
import com.bhengubv.circleai.security.ISecurityWatchdog
import com.bhengubv.circleai.security.SecurityCheckpoint
import com.bhengubv.circleai.security.SecurityResponse
import com.bhengubv.circleai.security.ThreatVector
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withTimeoutOrNull
import java.time.Instant
import java.util.Locale

// ===========================================================================
// LokiOrchestrator  (LokiOrchestrator.cs)
// ===========================================================================

/**
 * Host-side orchestrator. Accepts [AgentTask] items, dispatches them through an
 * [IAgentDispatcher], enforces quality gates, and exposes results as a
 * [Flow] for host applications to consume.
 *
 * Task execution is bounded by [AgentSwarmConfig.maxConcurrency]. After each
 * task completes, the quality gate is evaluated; gate failures are re-emitted as
 * [AgentStatus.Blocked] results with the gate's blocker messages appended to
 * [SwarmResult.issues].
 */
class LokiOrchestrator(
    private val dispatcher: IAgentDispatcher,
    config: AgentSwarmConfig? = null,
) {
    private val config: AgentSwarmConfig = config ?: AgentSwarmConfig.Default

    /**
     * Runs a swarm of tasks concurrently up to [AgentSwarmConfig.maxConcurrency].
     * For each completed task, the quality gate is evaluated; gate failures are
     * emitted as [AgentStatus.Blocked] results. Results are emitted one per task,
     * in the order the tasks were scheduled.
     */
    fun runSwarm(tasks: Iterable<AgentTask>): Flow<SwarmResult> = flow {
        coroutineScope {
            val semaphore = Semaphore(config.maxConcurrency)
            val running = ArrayList<Deferred<SwarmResult>>()

            for (task in tasks) {
                // Acquire before scheduling so no more than maxConcurrency tasks
                // are ever in flight at once (semaphore released inside runOne).
                semaphore.acquire()
                running.add(async { runOne(task, semaphore) })
            }

            for (deferred in running) {
                val result = deferred.await()
                val gate = dispatcher.runQualityGate(result)

                if (!gate.passed &&
                    (config.requireReviewPassBeforeDeploy || config.requireSecurityPassBeforeDeploy)
                ) {
                    emit(
                        result.copy(
                            status = AgentStatus.Blocked,
                            issues = result.issues + gate.blockers,
                        ),
                    )
                } else {
                    emit(result)
                }
            }
        }
    }

    private suspend fun runOne(task: AgentTask, semaphore: Semaphore): SwarmResult {
        try {
            val result = withTimeoutOrNull(config.taskTimeout.toMillis()) {
                dispatcher.dispatch(task)
            }
            return result ?: SwarmResult(
                task.id,
                task.role,
                AgentStatus.Failed,
                "Task timed out.",
                listOf("[HIGH] Task exceeded configured timeout."),
                Instant.now(),
            )
        } catch (ex: Exception) {
            // A dispatcher exception used to propagate out and break the swarm.
            // Wrap it as a failed SwarmResult so the remaining tasks still
            // surface to the caller.
            val label = "${ex.javaClass.simpleName}: ${ex.message}"
            return SwarmResult(
                task.id,
                task.role,
                AgentStatus.Failed,
                "Dispatcher threw: $label",
                listOf("[HIGH] $label"),
                Instant.now(),
            )
        } finally {
            semaphore.release()
        }
    }
}

// ===========================================================================
// IncidentTrigger  (IncidentTrigger.cs)
// ===========================================================================

/**
 * Maps a recorded [EpisodicMemoryEntry] (or an [AnomalySignal]) to the set of
 * agent tasks that should be triggered when it represents a crash or security
 * incident.
 */
object IncidentTrigger {
    /** Tag keys identifying an entry as a crash / unhandled-error incident. */
    private val crashTags = setOf(
        "crash", "exception", "unhandled_error", "oom", "null_reference",
    )

    /** Tag keys that additionally indicate a security investigation is warranted. */
    private val securityTags = setOf(
        "auth_failure", "permission_denied", "token_expired", "injection", "overflow",
    )

    /**
     * Inspects an episodic memory entry and returns the agent tasks that should
     * be triggered. Returns an empty list when the entry is not an incident.
     *
     * - One [AgentRole.Operations] task is always included when a crash tag is
     *   detected.
     * - One [AgentRole.Security] task is additionally included when a security
     *   tag is also present.
     */
    fun fromMemoryEntry(entry: EpisodicMemoryEntry): List<AgentTask> {
        val tags = entry.tags
        val isCrash = tags.any { it.lowercase(Locale.ROOT) in crashTags }
        if (!isCrash) return emptyList()

        val tasks = ArrayList<AgentTask>()

        // Always dispatch an ops-incident task for every crash entry.
        tasks.add(
            AgentTask.create(
                AgentRole.Operations,
                "ops-incident: diagnose crash recorded at ${entry.createdUtc}",
                AgentPriority.High,
                mapOf(
                    "episode_id" to entry.id,
                    "user_id" to entry.userId,
                    "content" to entry.content,
                ),
            ),
        )

        // When security indicators are also present, escalate to a security agent.
        val isSecurity = tags.any { it.lowercase(Locale.ROOT) in securityTags }
        if (isSecurity) {
            tasks.add(
                AgentTask.create(
                    AgentRole.Security,
                    "ops-security: investigate security incident from episode ${entry.id}",
                    AgentPriority.Critical,
                    mapOf(
                        "episode_id" to entry.id,
                        "tags" to tags.joinToString(","),
                    ),
                ),
            )
        }

        return tasks
    }

    /**
     * Maps a confirmed [AnomalySignal] from the local immune system into an
     * [AgentTask] for an ops-security agent. Returns `null` for signals below
     * the dispatch threshold.
     *
     * @param dispatchThreshold Minimum [AnomalySignal.confidence] required to
     *   dispatch. Default 0.30 — matches DefaultSecurityWatchdog's rotation
     *   threshold.
     */
    fun fromAnomalySignal(signal: AnomalySignal, dispatchThreshold: Double = 0.30): AgentTask? {
        if (signal.confidence < dispatchThreshold) return null

        // Confidence drives priority — high-severity vectors are bumped one rank.
        var priority = when {
            signal.confidence >= 0.85f -> AgentPriority.Critical
            signal.confidence >= 0.60f -> AgentPriority.High
            else -> AgentPriority.Normal
        }

        val isHighSeverityVector = signal.vector == ThreatVector.ControlFlowDrift ||
            signal.vector == ThreatVector.PrivilegeEscalation ||
            signal.vector == ThreatVector.NetworkPivot ||
            signal.vector == ThreatVector.StateCorruption

        // Priority ordering: Critical(0) < High(1) < Normal(2) < Low(3).
        // "Bumping one rank" means decreasing the ordinal value.
        if (isHighSeverityVector && priority.ordinal > AgentPriority.Critical.ordinal) {
            val bumped = maxOf(AgentPriority.Critical.ordinal, priority.ordinal - 1)
            priority = AgentPriority.entries[bumped]
        }

        val inputs = LinkedHashMap<String, String>(signal.evidence)
        inputs["signal_id"] = signal.id.toString()
        inputs["vector"] = signal.vector.toString()
        inputs["confidence"] = String.format(Locale.ROOT, "%.3f", signal.confidence)
        inputs["affected_module"] = signal.affectedModule
        inputs["description"] = signal.description
        inputs["detected_at"] = signal.detectedAt.toString()

        val pct = String.format(Locale.ROOT, "%.0f%%", signal.confidence * 100.0)
        return AgentTask.create(
            AgentRole.Security,
            "ops-security: anomaly ${signal.vector} in ${signal.affectedModule} (confidence $pct)",
            priority,
            inputs,
        )
    }
}

// ===========================================================================
// SecurityOrchestrationBridge  (SecurityOrchestrationBridge.cs)
// ===========================================================================

/**
 * Wraps an [ISecurityWatchdog] so that every anomaly signal ALSO dispatches an
 * ops-security [AgentTask] to a [LokiOrchestrator]. Runtime response and agent
 * dispatch proceed in parallel; neither blocks the other.
 *
 * @param inner Underlying watchdog (typically DefaultSecurityWatchdog).
 * @param orchestrator Orchestrator that runs the dispatched agent swarms.
 * @param dispatchThreshold Minimum [AnomalySignal.confidence] required to
 *   dispatch an agent. Default 0.30 — matches the inner watchdog's rotation
 *   threshold.
 */
class SecurityOrchestrationBridge(
    private val inner: ISecurityWatchdog,
    private val orchestrator: LokiOrchestrator,
    private val dispatchThreshold: Double = 0.30,
) : ISecurityWatchdog {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    override suspend fun onAnomalyDetected(
        signal: AnomalySignal,
        checkpoint: SecurityCheckpoint?,
    ): SecurityResponse {
        // Fire the agent dispatch in the background so the runtime response
        // (key rotation, rollback) is NEVER blocked by the agent swarm, which
        // may take minutes. Agent failures must not crash the runtime — hence
        // the runCatching wrapper (mirrors the C# ContinueWith swallow).
        scope.launch {
            runCatching { dispatchAgent(signal) }
        }

        // Await the watchdog so the caller gets the runtime response immediately.
        return inner.onAnomalyDetected(signal, checkpoint)
    }

    override fun streamSignals() = inner.streamSignals()

    private suspend fun dispatchAgent(signal: AnomalySignal) {
        val task = IncidentTrigger.fromAnomalySignal(signal, dispatchThreshold) ?: return
        // Drain the swarm flow — typically a single task -> single result.
        // Results are observable through orchestrator subscriptions host-side.
        orchestrator.runSwarm(listOf(task)).collect { }
    }
}
