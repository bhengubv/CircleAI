// Orchestration.kt
//
// Kotlin port of CircleAI.Orchestration — the C# reference is the EXACT spec
// (AgentRole.cs, AgentTask.cs, AgentSwarmConfig.cs, SwarmResult.cs,
// QualityGateResult.cs, IAgentDispatcher.cs, LocalAgentDispatcher.cs).
//
// Agent-swarm dispatch: routes agent tasks to per-role handler delegates and
// evaluates a deterministic quality gate on the result. No external network
// calls are made — loki-mode hooks into this dispatcher at the host level.
//
// C# -> Kotlin conventions:
//   Guid                 -> java.util.UUID
//   TimeSpan             -> java.time.Duration
//   Task                 -> suspend fun
//   IReadOnlyDictionary  -> Map<String, String>
//   DateTimeOffset       -> java.time.Instant
//   Func<AgentTask,CancellationToken,Task<SwarmResult>> -> suspend (AgentTask) -> SwarmResult
//   IDisposable          -> AutoCloseable

package com.bhengubv.circleai.orchestration

import com.bhengubv.circleai.device.DeviceProbe
import com.bhengubv.circleai.device.DeviceTierDefaults
import java.time.Duration
import java.time.Instant
import java.util.UUID

// ===========================================================================
// AgentRole / AgentPriority / AgentStatus  (AgentRole.cs)
// ===========================================================================

/** Categorises the domain responsibility of an agent in a swarm. */
enum class AgentRole { Engineering, Operations, Review, Security }

/** Execution priority of an agent task. Lower ordinal = higher urgency. */
enum class AgentPriority { Critical, High, Normal, Low }

/** Lifecycle status of an agent task or swarm result. */
enum class AgentStatus { Pending, Running, Passed, Failed, Blocked }

// ===========================================================================
// AgentTask  (AgentTask.cs)
// ===========================================================================

/** A single unit of work dispatched to an agent swarm. */
data class AgentTask(
    val id: UUID,
    val role: AgentRole,
    val description: String,
    val priority: AgentPriority,
    val inputs: Map<String, String>,
    val createdAt: Instant,
) {
    companion object {
        /** Stamps a new task with a fresh id and current UTC timestamp. */
        fun create(
            role: AgentRole,
            description: String,
            priority: AgentPriority,
            inputs: Map<String, String>? = null,
        ): AgentTask = AgentTask(
            id = UUID.randomUUID(),
            role = role,
            description = description,
            priority = priority,
            inputs = inputs ?: emptyMap(),
            createdAt = Instant.now(),
        )
    }
}

// ===========================================================================
// AgentSwarmConfig  (AgentSwarmConfig.cs)
// ===========================================================================

/** Tuning parameters governing swarm scheduling + quality gates. */
data class AgentSwarmConfig(
    val maxConcurrency: Int,
    val taskTimeout: Duration,
    val requireReviewPassBeforeDeploy: Boolean,
    val requireSecurityPassBeforeDeploy: Boolean,
) {
    companion object {
        /** Production-safe defaults: 4 concurrent tasks, 5-minute timeout, both gates enforced. */
        val Default: AgentSwarmConfig
            get() = AgentSwarmConfig(4, Duration.ofMinutes(5), true, true)

        /**
         * Device-aware defaults: maxConcurrency is sized via
         * [DeviceTierDefaults.maxConcurrency] against the supplied [DeviceProbe];
         * everything else matches [Default].
         */
        fun forDevice(probe: DeviceProbe): AgentSwarmConfig = AgentSwarmConfig(
            maxConcurrency = DeviceTierDefaults.maxConcurrency(probe.classify(), probe.cpuCores),
            taskTimeout = Duration.ofMinutes(5),
            requireReviewPassBeforeDeploy = true,
            requireSecurityPassBeforeDeploy = true,
        )
    }
}

// ===========================================================================
// SwarmResult / QualityGateResult  (SwarmResult.cs, QualityGateResult.cs)
// ===========================================================================

/** The outcome produced by an agent handler for a single [AgentTask]. */
data class SwarmResult(
    val taskId: UUID,
    val role: AgentRole,
    val status: AgentStatus,
    val output: String,
    val issues: List<String>,
    val completedAt: Instant,
)

/** The verdict produced by [IAgentDispatcher.runQualityGate]. */
data class QualityGateResult(
    val passed: Boolean,
    val blockers: List<String>,
    val warnings: List<String>,
)

// ===========================================================================
// IAgentDispatcher  (IAgentDispatcher.cs)
// ===========================================================================

/** Routes agent tasks to their handlers and evaluates quality gates on results. */
interface IAgentDispatcher {
    /** Dispatches [task] to the appropriate handler and returns the result. */
    suspend fun dispatch(task: AgentTask): SwarmResult

    /** Evaluates a completed [SwarmResult] and determines whether it passes the deployment gate. */
    suspend fun runQualityGate(result: SwarmResult): QualityGateResult
}

// ===========================================================================
// LocalAgentDispatcher  (LocalAgentDispatcher.cs)
// ===========================================================================

/**
 * In-process agent dispatcher. Routes tasks to handler delegates registered
 * per [AgentRole]. Tasks dispatched to roles without a registered handler
 * return [AgentStatus.Blocked] immediately.
 */
class LocalAgentDispatcher : IAgentDispatcher, AutoCloseable {
    private val handlers = HashMap<AgentRole, suspend (AgentTask) -> SwarmResult>()
    private val lock = Any()

    @Volatile
    private var disposed = false

    /** Registers a handler for [role]. Replaces any previously registered handler. */
    fun registerHandler(role: AgentRole, handler: suspend (AgentTask) -> SwarmResult) {
        synchronized(lock) { handlers[role] = handler }
    }

    override suspend fun dispatch(task: AgentTask): SwarmResult {
        check(!disposed) { "LocalAgentDispatcher has been disposed." }

        val handler = synchronized(lock) { handlers[task.role] }
        if (handler != null) return handler(task)

        // No handler registered — surface a blocked result with an actionable message.
        return SwarmResult(
            task.id,
            task.role,
            AgentStatus.Blocked,
            "No handler registered for role ${task.role}.",
            listOf("Register a handler for AgentRole.${task.role} before dispatching."),
            Instant.now(),
        )
    }

    /**
     * Deterministic gate: any issue prefixed with `[CRITICAL]` or `[HIGH]`
     * (case-insensitive) is a blocker; all other issues are warnings.
     */
    override suspend fun runQualityGate(result: SwarmResult): QualityGateResult {
        val blockers = result.issues.filter {
            it.startsWith("[CRITICAL]", ignoreCase = true) || it.startsWith("[HIGH]", ignoreCase = true)
        }
        val warnings = result.issues.filter { it !in blockers }
        return QualityGateResult(passed = blockers.isEmpty(), blockers = blockers, warnings = warnings)
    }

    override fun close() {
        disposed = true
    }
}
