// ServerEnterprise.kt
//
// Kotlin port of CircleAI.Inference.Server.Enterprise (2.7.0 / 3.3.0). C# is the
// EXACT spec. Multi-tenant routing + batch scheduling + model sharding +
// cross-tier offload (RT-12 v2). Covers:
//   • ServerTier (Contracts.cs)
//   • TenantContext / TenantQuota / BatchSlot / ShardDescriptor / OffloadDecision
//   • ITenantRouter / IBatchScheduler / IModelShardPlanner / ICrossTierOffload
//   • Real in-memory implementations (InMemoryInferenceServerEnterprise.cs):
//       RoundRobinTenantRouter, InMemoryBatchScheduler,
//       EvenSplitModelShardPlanner, PolicyCrossTierOffload
//   • Single-node null defaults (NullImplementations.cs)

package com.bhengubv.circleai.serverenterprise

import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong

// ── Enums / DTOs ─────────────────────────────────────────────────────────────

/** Deployment tier the enterprise server runs at (ports ServerTier). */
enum class ServerTier { SingleNode, Server, ServerFarm }

/** Per-tenant routing context (ports TenantContext). */
data class TenantContext(
    val tenantId: String,
    val parentTenantId: String? = null,
    val tags: Map<String, String>? = null,
)

/** Per-tenant resource quota (ports TenantQuota). */
data class TenantQuota(
    val tenantId: String,
    val maxConcurrentRequests: Int,
    val maxModelsLoaded: Int,
    val maxBytesInFlight: Long,
    val dailyTokenBudget: Int,
)

/** A reserved batch slot with a deadline (ports BatchSlot). */
data class BatchSlot(
    val slotId: String,
    val modelId: String,
    val tokens: Int,
    val deadlineUtc: Instant,
)

/** One shard's node assignment for a very-large model (ports ShardDescriptor). */
data class ShardDescriptor(
    val shardId: String,
    val rangeStart: Int,
    val rangeEnd: Int,
    val nodeId: String,
)

/** The result of a cross-tier offload decision (ports OffloadDecision). */
data class OffloadDecision(
    val shouldOffload: Boolean,
    val targetNodeId: String?,
    val reason: String?,
)

// ── Contracts ────────────────────────────────────────────────────────────────

/** (2.7.0) Multi-tenant routing — pick a backend node per tenant. */
interface ITenantRouter {
    val backendId: String
    suspend fun chooseNodeAsync(tenant: TenantContext, modelId: String): String?
    suspend fun setQuotaAsync(quota: TenantQuota)
    suspend fun getQuotaAsync(tenantId: String): TenantQuota?
}

/** (2.7.0) Batch scheduler — coalesce small requests into one big one. */
interface IBatchScheduler {
    val backendId: String
    suspend fun reserveAsync(modelId: String, estimatedTokens: Int, maxWait: Duration): BatchSlot
    suspend fun releaseAsync(slot: BatchSlot)
}

/** (2.7.0) Model-sharding plan for very-large-model deployments. */
interface IModelShardPlanner {
    val backendId: String
    suspend fun planAsync(modelId: String, paramBytes: Int): List<ShardDescriptor>
}

/** (2.7.0) RT-12 v2 cross-tier offload — phone borrows server brain. */
interface ICrossTierOffload {
    val backendId: String
    suspend fun shouldOffloadAsync(modelId: String, promptTokens: Int, callerTier: ServerTier): OffloadDecision
}

// ── Real in-memory implementations ───────────────────────────────────────────

/**
 * Round-robin tenant router: cycles over registered nodes per model. Ports
 * RoundRobinTenantRouter.
 */
class RoundRobinTenantRouter : ITenantRouter {
    private val quotas = ConcurrentHashMap<String, TenantQuota>()
    private val nodesByModel = ConcurrentHashMap<String, MutableList<String>>()
    private val rr = ConcurrentHashMap<String, Int>()
    private val lock = Any()

    override val backendId: String = "round-robin"

    /** Register [nodeId] as a serving node for [modelId] (idempotent). */
    fun registerNode(modelId: String, nodeId: String) {
        require(modelId.isNotBlank()) { "modelId required" }
        require(nodeId.isNotBlank()) { "nodeId required" }
        synchronized(lock) {
            val list = nodesByModel.getOrPut(modelId) { ArrayList() }
            if (!list.contains(nodeId)) list.add(nodeId)
        }
    }

    override suspend fun chooseNodeAsync(tenant: TenantContext, modelId: String): String? {
        require(modelId.isNotBlank()) { "modelId required" }
        synchronized(lock) {
            val nodes = nodesByModel[modelId]
            if (nodes.isNullOrEmpty()) return null
            val idx = rr.getOrDefault(modelId, 0)
            val pick = nodes[idx % nodes.size]
            rr[modelId] = idx + 1
            return pick
        }
    }

    override suspend fun setQuotaAsync(quota: TenantQuota) {
        quotas[quota.tenantId] = quota
    }

    override suspend fun getQuotaAsync(tenantId: String): TenantQuota? {
        require(tenantId.isNotBlank()) { "tenantId required" }
        return quotas[tenantId]
    }
}

/**
 * In-memory batch scheduler with real reservation + release and deadline
 * guarantees. Ports InMemoryBatchScheduler.
 */
class InMemoryBatchScheduler : IBatchScheduler {
    private val slots = ConcurrentHashMap<String, BatchSlot>()
    private val seq = AtomicLong(0)

    override val backendId: String = "in-memory"

    override suspend fun reserveAsync(modelId: String, estimatedTokens: Int, maxWait: Duration): BatchSlot {
        require(modelId.isNotBlank()) { "modelId required" }
        require(estimatedTokens > 0) { "estimatedTokens must be > 0" }
        require(maxWait > Duration.ZERO) { "maxWait must be > 0" }
        val slot = BatchSlot(
            slotId = "slot-${seq.incrementAndGet()}",
            modelId = modelId,
            tokens = estimatedTokens,
            deadlineUtc = Instant.now().plus(maxWait),
        )
        slots[slot.slotId] = slot
        return slot
    }

    override suspend fun releaseAsync(slot: BatchSlot) {
        slots.remove(slot.slotId)
    }

    /** Live reservation count (diagnostics / tests). */
    val activeSlots: Int get() = slots.size
}

/**
 * Even-bucket shard planner: splits [paramBytes] across the model's registered
 * nodes, distributing the remainder to the first buckets. Ports
 * EvenSplitModelShardPlanner.
 */
class EvenSplitModelShardPlanner(
    private val nodesFor: (String) -> List<String>,
) : IModelShardPlanner {

    override val backendId: String = "even-split"

    override suspend fun planAsync(modelId: String, paramBytes: Int): List<ShardDescriptor> {
        require(modelId.isNotBlank()) { "modelId required" }
        require(paramBytes > 0) { "paramBytes must be > 0" }

        val nodes = nodesFor(modelId)
        if (nodes.isEmpty()) return emptyList()

        val bucket = paramBytes / nodes.size
        val rem = paramBytes % nodes.size
        val list = ArrayList<ShardDescriptor>(nodes.size)
        var cursor = 0
        for (i in nodes.indices) {
            val size = bucket + if (i < rem) 1 else 0
            list.add(ShardDescriptor("shard-$modelId-$i", cursor, cursor + size, nodes[i]))
            cursor += size
        }
        return list
    }
}

/**
 * Policy cross-tier offload: offload only when the caller is below the top tier
 * AND the prompt exceeds a local ceiling. Ports PolicyCrossTierOffload.
 */
class PolicyCrossTierOffload(
    private val localPromptCeiling: Int = 2048,
    private val farmTargetNode: String? = null,
) : ICrossTierOffload {

    init {
        require(localPromptCeiling > 0) { "localPromptCeiling must be > 0" }
    }

    override val backendId: String = "policy"

    override suspend fun shouldOffloadAsync(modelId: String, promptTokens: Int, callerTier: ServerTier): OffloadDecision {
        require(modelId.isNotBlank()) { "modelId required" }
        require(promptTokens >= 0) { "promptTokens must be >= 0" }
        if (callerTier == ServerTier.ServerFarm) {
            return OffloadDecision(false, null, "Caller is already top-tier")
        }
        if (promptTokens <= localPromptCeiling) {
            return OffloadDecision(false, null, "Prompt fits locally")
        }
        return OffloadDecision(true, farmTargetNode, "Prompt exceeds local ceiling ($localPromptCeiling tokens)")
    }
}

// ── Single-node null defaults ────────────────────────────────────────────────

/** Single-node default — never routes off-node (ports NullTenantRouter). */
object NullTenantRouter : ITenantRouter {
    override val backendId: String = "null"
    override suspend fun chooseNodeAsync(tenant: TenantContext, modelId: String): String? = null
    override suspend fun setQuotaAsync(quota: TenantQuota) {}
    override suspend fun getQuotaAsync(tenantId: String): TenantQuota? = null
}

/** Single-node default — issues a zero-guid slot immediately (ports NullBatchScheduler). */
object NullBatchScheduler : IBatchScheduler {
    override val backendId: String = "null"
    override suspend fun reserveAsync(modelId: String, estimatedTokens: Int, maxWait: Duration): BatchSlot =
        BatchSlot(
            slotId = "00000000-0000-0000-0000-000000000000",
            modelId = modelId,
            tokens = estimatedTokens,
            deadlineUtc = Instant.now().plus(maxWait),
        )
    override suspend fun releaseAsync(slot: BatchSlot) {}
}

/** Single-node default — never shards (ports NullModelShardPlanner). */
object NullModelShardPlanner : IModelShardPlanner {
    override val backendId: String = "null"
    override suspend fun planAsync(modelId: String, paramBytes: Int): List<ShardDescriptor> = emptyList()
}

/** Single-node default — never offloads (ports NullCrossTierOffload). */
object NullCrossTierOffload : ICrossTierOffload {
    override val backendId: String = "null"
    override suspend fun shouldOffloadAsync(modelId: String, promptTokens: Int, callerTier: ServerTier): OffloadDecision =
        OffloadDecision(false, null, "Local execution; no cross-tier offload configured.")
}
