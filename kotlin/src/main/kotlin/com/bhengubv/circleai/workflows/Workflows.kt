// Workflows.kt
//
// Kotlin port of CircleAI.Workflows — the C# reference is the EXACT spec
// (Contracts.cs, NullImplementations.cs, PacaConversations.cs).
//
// Durable-workflow contracts (definition store / runner / state), a real
// in-memory implementation of each, and the PACA conversation state machine
// (Queued -> Running -> Finished/Failed/Stopped). The Docker isolation +
// OpenHands SDK integration is host-supplied via IConversationExecutor; this
// module owns the state machine, history, and lifecycle events.
//
// C# -> Kotlin conventions:
//   ValueTask / Task          -> suspend fun
//   ReadOnlyMemory<byte>      -> ByteArray
//   IReadOnlyDictionary<...>  -> Map<String, Any?>
//   DateTimeOffset            -> java.time.Instant
//   CancellationTokenSource   -> per-conversation kotlinx.coroutines.Job
//   Action<ConversationStep>  -> (ConversationStep) -> Unit

package com.bhengubv.circleai.workflows

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.job
import java.time.Instant
import java.util.UUID
import java.util.concurrent.atomic.AtomicLong

// ===========================================================================
// Contracts  (Contracts.cs)
// ===========================================================================

enum class WorkflowPhase { Pending, Running, Suspended, Completed, Failed }

data class WorkflowDefinition(val definitionId: String, val name: String, val version: String, val description: String)

data class WorkflowExecution(
    val runId: String,
    val definitionId: String,
    val phase: WorkflowPhase,
    val startUtc: Instant,
    val failureReason: String?,
)

data class CheckpointPayload(val runId: String, val stepId: String, val stateBlob: ByteArray) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is CheckpointPayload) return false
        return runId == other.runId && stepId == other.stepId && stateBlob.contentEquals(other.stateBlob)
    }

    override fun hashCode(): Int {
        var result = runId.hashCode()
        result = 31 * result + stepId.hashCode()
        result = 31 * result + stateBlob.contentHashCode()
        return result
    }
}

interface IWorkflowDefinitionStore {
    val backendId: String
    suspend fun upsert(d: WorkflowDefinition)
    suspend fun get(id: String): WorkflowDefinition?
}

interface IWorkflowRunner {
    val backendId: String
    suspend fun start(definitionId: String, inputs: Map<String, Any?>? = null): WorkflowExecution
    suspend fun get(runId: String): WorkflowExecution?
    suspend fun cancel(runId: String)
}

interface IWorkflowState {
    val backendId: String
    suspend fun checkpoint(payload: CheckpointPayload)
    suspend fun load(runId: String, stepId: String): CheckpointPayload?
}

// ===========================================================================
// In-memory implementations
// ===========================================================================

class InMemoryWorkflowDefinitionStore : IWorkflowDefinitionStore {
    private val items = HashMap<String, WorkflowDefinition>()
    private val lock = Any()
    override val backendId: String get() = "in-memory"

    override suspend fun upsert(d: WorkflowDefinition) {
        require(d.definitionId.isNotBlank()) { "DefinitionId required" }
        synchronized(lock) { items[d.definitionId] = d }
    }

    override suspend fun get(id: String): WorkflowDefinition? {
        require(id.isNotBlank()) { "id required" }
        return synchronized(lock) { items[id] }
    }
}

/**
 * In-memory workflow runner. A run is created against a known definition and
 * transitions to [WorkflowPhase.Running]; [cancel] flips it to
 * [WorkflowPhase.Failed] with a cancellation reason. Backed by an injected
 * definition store so a runner can validate the definition exists.
 */
class InMemoryWorkflowRunner(private val definitions: IWorkflowDefinitionStore) : IWorkflowRunner {
    private val runs = HashMap<String, WorkflowExecution>()
    private val lock = Any()
    private val seq = AtomicLong(0)

    override val backendId: String get() = "in-memory"

    override suspend fun start(definitionId: String, inputs: Map<String, Any?>?): WorkflowExecution {
        require(definitionId.isNotBlank()) { "definitionId required" }
        val known = definitions.get(definitionId)
        val runId = "run-${seq.incrementAndGet()}"
        val exec = if (known == null) {
            WorkflowExecution(runId, definitionId, WorkflowPhase.Failed, Instant.now(), "Unknown definition '$definitionId'.")
        } else {
            WorkflowExecution(runId, definitionId, WorkflowPhase.Running, Instant.now(), null)
        }
        synchronized(lock) { runs[runId] = exec }
        return exec
    }

    override suspend fun get(runId: String): WorkflowExecution? {
        require(runId.isNotBlank()) { "runId required" }
        return synchronized(lock) { runs[runId] }
    }

    override suspend fun cancel(runId: String) {
        require(runId.isNotBlank()) { "runId required" }
        synchronized(lock) {
            val existing = runs[runId] ?: return
            if (existing.phase == WorkflowPhase.Running || existing.phase == WorkflowPhase.Suspended) {
                runs[runId] = existing.copy(phase = WorkflowPhase.Failed, failureReason = "cancelled")
            }
        }
    }
}

class InMemoryWorkflowState : IWorkflowState {
    private val checkpoints = HashMap<String, CheckpointPayload>()
    private val lock = Any()
    override val backendId: String get() = "in-memory"

    private fun key(runId: String, stepId: String) = "$runId/$stepId"

    override suspend fun checkpoint(payload: CheckpointPayload) {
        require(payload.runId.isNotBlank()) { "RunId required" }
        require(payload.stepId.isNotBlank()) { "StepId required" }
        synchronized(lock) { checkpoints[key(payload.runId, payload.stepId)] = payload }
    }

    override suspend fun load(runId: String, stepId: String): CheckpointPayload? {
        require(runId.isNotBlank()) { "runId required" }
        require(stepId.isNotBlank()) { "stepId required" }
        return synchronized(lock) { checkpoints[key(runId, stepId)] }
    }
}

// ===========================================================================
// Null implementations  (NullImplementations.cs)
// ===========================================================================

private val NULL_GUID: String = UUID(0, 0).toString()

class NullWorkflowDefinitionStore private constructor() : IWorkflowDefinitionStore {
    override val backendId: String get() = "null"
    override suspend fun upsert(d: WorkflowDefinition) {}
    override suspend fun get(id: String): WorkflowDefinition? = null

    companion object {
        val Instance = NullWorkflowDefinitionStore()
    }
}

class NullWorkflowRunner private constructor() : IWorkflowRunner {
    override val backendId: String get() = "null"
    override suspend fun start(definitionId: String, inputs: Map<String, Any?>?): WorkflowExecution =
        WorkflowExecution(NULL_GUID, definitionId, WorkflowPhase.Failed, Instant.MIN, "NullWorkflowRunner")

    override suspend fun get(runId: String): WorkflowExecution? = null
    override suspend fun cancel(runId: String) {}

    companion object {
        val Instance = NullWorkflowRunner()
    }
}

class NullWorkflowState private constructor() : IWorkflowState {
    override val backendId: String get() = "null"
    override suspend fun checkpoint(payload: CheckpointPayload) {}
    override suspend fun load(runId: String, stepId: String): CheckpointPayload? = null

    companion object {
        val Instance = NullWorkflowState()
    }
}

// ===========================================================================
// Conversation state machine  (PacaConversations.cs)
// ===========================================================================

/** Conversation state. */
enum class ConversationState { Queued, Running, Finished, Failed, Stopped }

/** One conversation between a human + an agent (or multiple agents). */
data class AgentConversation(
    val id: String,
    val projectId: String,
    val agentMemberId: String,
    val humanMemberId: String?,
    val openingPrompt: String,
    val state: ConversationState,
    val queuedAtUtc: Instant,
    val startedAtUtc: Instant?,
    val finishedAtUtc: Instant?,
    val resultJson: String?,
    val failureReason: String?,
)

/** One executed step in a conversation. */
data class ConversationStep(
    val conversationId: String,
    val order: Int,
    val speaker: String, // "user" / "agent" / "tool"
    val contentJson: String,
    val at: Instant,
)

/** Permission flag set required to run risky actions. */
data class ConversationPermissions(val allowCloneRepos: Boolean, val allowCreatePr: Boolean)

/** Host-supplied executor — invokes OpenHands SDK / Docker container per conversation. */
interface IConversationExecutor {
    /** Start a conversation; emit ConversationStep events into the registry as work progresses. */
    suspend fun run(
        conversation: AgentConversation,
        permissions: ConversationPermissions,
        onStep: (ConversationStep) -> Unit,
    )
}

/** Conversation registry + state machine. */
class PacaConversationRuntime(
    private val executor: IConversationExecutor,
    private val clock: () -> Instant = { Instant.now() },
) {
    private val conversations = HashMap<String, AgentConversation>()
    private val steps = HashMap<String, MutableList<ConversationStep>>()
    private val running = HashMap<String, Job>()
    private val lock = Any()

    fun queue(
        id: String,
        projectId: String,
        agentMemberId: String,
        openingPrompt: String,
        humanMemberId: String? = null,
    ): AgentConversation {
        val c = AgentConversation(
            id = id,
            projectId = projectId,
            agentMemberId = agentMemberId,
            humanMemberId = humanMemberId,
            openingPrompt = openingPrompt,
            state = ConversationState.Queued,
            queuedAtUtc = clock(),
            startedAtUtc = null,
            finishedAtUtc = null,
            resultJson = null,
            failureReason = null,
        )
        synchronized(lock) {
            if (conversations.containsKey(id)) throw IllegalStateException("Conversation '$id' already exists.")
            conversations[id] = c
            steps[id] = ArrayList()
        }
        return c
    }

    fun get(id: String): AgentConversation? = synchronized(lock) { conversations[id] }

    fun steps(id: String): List<ConversationStep> = synchronized(lock) { steps[id]?.toList() ?: emptyList() }

    /** Begin executing the conversation, driven by the calling coroutine. */
    suspend fun start(id: String, permissions: ConversationPermissions) {
        val current = synchronized(lock) { conversations[id] }
        if (current == null || current.state != ConversationState.Queued) {
            throw IllegalStateException("Conversation '$id' is not in Queued state.")
        }
        val started = current.copy(state = ConversationState.Running, startedAtUtc = clock())
        val job = currentCoroutineContext().job
        synchronized(lock) {
            conversations[id] = started
            running[id] = job
        }

        try {
            executor.run(started, permissions) { step ->
                synchronized(lock) { steps[id]?.add(step) }
            }
            synchronized(lock) {
                conversations[id] = started.copy(
                    state = ConversationState.Finished,
                    finishedAtUtc = clock(),
                    resultJson = "{}",
                )
            }
        } catch (ce: CancellationException) {
            synchronized(lock) {
                conversations[id] = started.copy(state = ConversationState.Stopped, finishedAtUtc = clock())
            }
            throw ce
        } catch (ex: Exception) {
            synchronized(lock) {
                conversations[id] = started.copy(
                    state = ConversationState.Failed,
                    finishedAtUtc = clock(),
                    failureReason = ex.message,
                )
            }
        } finally {
            synchronized(lock) { running.remove(id) }
        }
    }

    /** Stop a running conversation from the UI. */
    fun stop(id: String) {
        // Snapshot the job under lock, then cancel outside the lock so the
        // job's completion handler cannot re-enter the lock while we hold it.
        val job = synchronized(lock) { running[id] }
        job?.cancel()
    }
}
