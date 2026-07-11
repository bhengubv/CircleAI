// MicroAgents.kt
//
// Kotlin port of CircleAI.MicroAgents — the C# reference is the EXACT spec
// (Contracts.cs, InMemoryMicroAgents.cs, MicroAgentHelpers.cs,
// NullImplementations.cs).
//
// Micro-agent contracts + an in-memory host that keeps a registry of agents
// and routes Invoke calls to them. FuncMicroAgent wraps a lambda; capability
// search + an invocation log round out the helpers.
//
// C# -> Kotlin conventions: ValueTask -> suspend, DateTimeOffset ->
// java.time.Instant, ConcurrentDictionary -> synchronized MutableMap,
// Func<...> -> suspend function type.

package com.bhengubv.circleai.microagents

import java.time.Instant

// ===========================================================================
// Contracts  (Contracts.cs)
// ===========================================================================

data class MicroAgentDescriptor(val agentId: String, val description: String, val capabilities: List<String>)

data class MicroAgentResponse(val agentId: String, val output: String, val metadata: Map<String, String>? = null)

interface IMicroAgent {
    val agentId: String
    val backendId: String
    val descriptor: MicroAgentDescriptor
    suspend fun invoke(input: String): MicroAgentResponse
}

interface IMicroAgentHost {
    val backendId: String
    fun register(agent: IMicroAgent)
    fun list(): List<MicroAgentDescriptor>
    suspend fun invoke(agentId: String, input: String): MicroAgentResponse?
}

// ===========================================================================
// FuncMicroAgent  (InMemoryMicroAgents.cs)
// ===========================================================================

/** Wrap a lambda in an IMicroAgent so callers can register lambdas. */
class FuncMicroAgent(
    override val agentId: String,
    description: String,
    capabilities: List<String>?,
    private val impl: suspend (String) -> MicroAgentResponse,
) : IMicroAgent {
    init {
        require(agentId.isNotBlank()) { "agentId required" }
    }

    override val backendId: String get() = "func"
    override val descriptor: MicroAgentDescriptor =
        MicroAgentDescriptor(agentId, description, capabilities ?: emptyList())

    override suspend fun invoke(input: String): MicroAgentResponse = impl(input)
}

// ===========================================================================
// Helpers  (MicroAgentHelpers.cs)
// ===========================================================================

data class MicroAgentInvocation(val agentId: String, val input: String, val responseText: String, val atUtc: Instant)

/** Capability filter — find agents whose descriptor advertises a capability tag. */
object MicroAgentSearch {
    fun byCapability(all: Iterable<MicroAgentDescriptor>, capability: String): List<MicroAgentDescriptor> {
        require(capability.isNotBlank()) { "capability required" }
        return all.filter { d -> d.capabilities.any { it.equals(capability, ignoreCase = true) } }
            .sortedBy { it.agentId }
    }

    fun search(all: Iterable<MicroAgentDescriptor>, query: String, topK: Int = 10): List<MicroAgentDescriptor> {
        require(topK > 0) { "topK must be positive" }
        return all.filter { d ->
            d.agentId.contains(query, ignoreCase = true) ||
                d.description.contains(query, ignoreCase = true) ||
                d.capabilities.any { it.contains(query, ignoreCase = true) }
        }.take(topK)
    }
}

/** Keep an in-memory invocation log. */
class MicroAgentInvocationLog {
    private val items = ArrayList<MicroAgentInvocation>()
    private val lock = Any()

    fun append(i: MicroAgentInvocation) {
        synchronized(lock) { items.add(i) }
    }

    fun forAgent(agentId: String, limit: Int = 50): List<MicroAgentInvocation> {
        require(limit > 0) { "limit must be positive" }
        return synchronized(lock) {
            items.filter { it.agentId == agentId }.sortedByDescending { it.atUtc }.take(limit)
        }
    }

    val totalInvocations: Int
        get() = synchronized(lock) { items.size }
}

// ===========================================================================
// In-memory host + null agent  (NullImplementations.cs)
// ===========================================================================

class NullMicroAgent : IMicroAgent {
    override val agentId: String get() = "null"
    override val backendId: String get() = "null"
    override val descriptor: MicroAgentDescriptor = MicroAgentDescriptor("null", "No-op micro agent", emptyList())
    override suspend fun invoke(input: String): MicroAgentResponse = MicroAgentResponse(agentId, "")
}

class InMemoryMicroAgentHost : IMicroAgentHost {
    override val backendId: String get() = "in-memory"
    private val agents = HashMap<String, IMicroAgent>()
    private val lock = Any()

    override fun register(agent: IMicroAgent) {
        synchronized(lock) { agents[agent.agentId] = agent }
    }

    override fun list(): List<MicroAgentDescriptor> =
        synchronized(lock) { agents.values.map { it.descriptor } }

    override suspend fun invoke(agentId: String, input: String): MicroAgentResponse? {
        val a = synchronized(lock) { agents[agentId] } ?: return null
        return a.invoke(input)
    }
}
