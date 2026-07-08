// ServerEnterpriseTest.kt
//
// Verifies the ported CircleAI.Inference.Server.Enterprise primitives:
// round-robin tenant routing + quotas, in-memory batch reserve/release,
// even-split shard planning, policy cross-tier offload, and the single-node
// null defaults.

package com.bhengubv.circleai.serverenterprise

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ServerEnterpriseTest {

    private val tenant = TenantContext("acme")

    // ── tenant router ────────────────────────────────────────────────────────

    @Test
    fun `round-robin cycles registered nodes per model`() = runTest {
        val r = RoundRobinTenantRouter()
        r.registerNode("qwen", "node-a")
        r.registerNode("qwen", "node-b")
        r.registerNode("qwen", "node-a") // dup ignored
        assertEquals("node-a", r.chooseNodeAsync(tenant, "qwen"))
        assertEquals("node-b", r.chooseNodeAsync(tenant, "qwen"))
        assertEquals("node-a", r.chooseNodeAsync(tenant, "qwen")) // wraps
        assertNull(r.chooseNodeAsync(tenant, "unregistered"))
    }

    @Test
    fun `router stores and returns quotas`() = runTest {
        val r = RoundRobinTenantRouter()
        val q = TenantQuota("acme", 4, 2, 1_000_000, 100_000)
        r.setQuotaAsync(q)
        assertEquals(q, r.getQuotaAsync("acme"))
        assertNull(r.getQuotaAsync("nobody"))
    }

    // ── batch scheduler ──────────────────────────────────────────────────────

    @Test
    fun `batch scheduler reserves with a deadline and releases`() = runTest {
        val s = InMemoryBatchScheduler()
        val slot = s.reserveAsync("qwen", 128, Duration.ofSeconds(2))
        assertTrue(slot.slotId.startsWith("slot-"))
        assertEquals(128, slot.tokens)
        assertTrue(slot.deadlineUtc.isAfter(java.time.Instant.now().minusSeconds(1)))
        assertEquals(1, s.activeSlots)
        s.releaseAsync(slot)
        assertEquals(0, s.activeSlots)
    }

    @Test
    fun `batch scheduler validates inputs`() = runTest {
        val s = InMemoryBatchScheduler()
        assertFailsWith<IllegalArgumentException> { s.reserveAsync("", 1, Duration.ofSeconds(1)) }
        assertFailsWith<IllegalArgumentException> { s.reserveAsync("m", 0, Duration.ofSeconds(1)) }
        assertFailsWith<IllegalArgumentException> { s.reserveAsync("m", 1, Duration.ZERO) }
    }

    // ── shard planner ────────────────────────────────────────────────────────

    @Test
    fun `even split distributes remainder to the first buckets`() = runTest {
        val planner = EvenSplitModelShardPlanner { listOf("n0", "n1", "n2") }
        val shards = planner.planAsync("big", 10) // 10 / 3 = 3 r1 → 4,3,3
        assertEquals(3, shards.size)
        assertEquals(0, shards[0].rangeStart); assertEquals(4, shards[0].rangeEnd)
        assertEquals(4, shards[1].rangeStart); assertEquals(7, shards[1].rangeEnd)
        assertEquals(7, shards[2].rangeStart); assertEquals(10, shards[2].rangeEnd)
        assertEquals(listOf("n0", "n1", "n2"), shards.map { it.nodeId })
    }

    @Test
    fun `even split returns empty when there are no nodes`() = runTest {
        val planner = EvenSplitModelShardPlanner { emptyList() }
        assertTrue(planner.planAsync("m", 100).isEmpty())
    }

    // ── cross-tier offload ───────────────────────────────────────────────────

    @Test
    fun `policy offload only when below top tier and over the ceiling`() = runTest {
        val off = PolicyCrossTierOffload(localPromptCeiling = 2048, farmTargetNode = "farm-1")
        // fits locally → no offload
        assertFalse(off.shouldOffloadAsync("m", 100, ServerTier.SingleNode).shouldOffload)
        // over ceiling on a low tier → offload to the farm node
        val d = off.shouldOffloadAsync("m", 5000, ServerTier.Server)
        assertTrue(d.shouldOffload)
        assertEquals("farm-1", d.targetNodeId)
        // top tier never offloads
        assertFalse(off.shouldOffloadAsync("m", 5000, ServerTier.ServerFarm).shouldOffload)
    }

    // ── null defaults ────────────────────────────────────────────────────────

    @Test
    fun `null defaults never route, shard, or offload`() = runTest {
        assertNull(NullTenantRouter.chooseNodeAsync(tenant, "m"))
        assertNull(NullTenantRouter.getQuotaAsync("acme"))
        assertTrue(NullModelShardPlanner.planAsync("m", 100).isEmpty())
        assertFalse(NullCrossTierOffload.shouldOffloadAsync("m", 99999, ServerTier.SingleNode).shouldOffload)
        val slot = NullBatchScheduler.reserveAsync("m", 10, Duration.ofSeconds(1))
        assertEquals("00000000-0000-0000-0000-000000000000", slot.slotId)
    }

    @Test
    fun `backend ids identify each implementation`() {
        assertEquals("round-robin", RoundRobinTenantRouter().backendId)
        assertEquals("in-memory", InMemoryBatchScheduler().backendId)
        assertEquals("even-split", EvenSplitModelShardPlanner { emptyList() }.backendId)
        assertEquals("policy", PolicyCrossTierOffload().backendId)
        assertEquals("null", NullTenantRouter.backendId)
    }
}
