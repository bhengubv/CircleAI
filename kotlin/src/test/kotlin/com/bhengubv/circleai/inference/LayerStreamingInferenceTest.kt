// LayerStreamingInferenceTest.kt
//
// Verifies CircleAI.Inference.LayerStreamingInference: the orchestrator's
// per-layer forward + evict, the null runner's throw contract, the deterministic
// identity runner, and shard discovery from a manifest directory.

package com.bhengubv.circleai.inference

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.io.File
import java.nio.file.Files
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class LayerStreamingInferenceTest {

    @Test
    fun `orchestrator runs every layer and evicts after each`() = runTest {
        val runner = IdentityLayerStreamingRunner()
        val shards = listOf(
            LayerWeightShard(0, "l0", 10),
            LayerWeightShard(1, "l1", 10),
            LayerWeightShard(2, "l2", 10),
        )
        val plan = LayerStreamingPlan("m", 3, shards, 30)
        val completed = ArrayList<Int>()

        val out = LayerStreamingOrchestrator(runner).forwardAsync(
            plan, floatArrayOf(0f, 0f),
        ) { completed.add(it.layerIndex) }

        assertEquals(listOf(0, 1, 2), completed)
        assertEquals(2, out.layerIndex)
        // Each layer applies (x + (i+1)) * 0.5 in sequence starting from 0:
        // l0: (0+1)*0.5 = 0.5 ; l1: (0.5+2)*0.5 = 1.25 ; l2: (1.25+3)*0.5 = 2.125
        assertContentEquals(floatArrayOf(2.125f, 2.125f), out.hidden)
        // All evicted after the pass.
        assertTrue(runner.residentLayers.isEmpty())
    }

    @Test
    fun `null runner throws to surface a mis-wired host`() = runTest {
        val plan = LayerStreamingPlan("m", 1, listOf(LayerWeightShard(0, "l0", 1)), 1)
        assertFailsWith<IllegalStateException> {
            LayerStreamingOrchestrator(NullLayerStreamingRunner).forwardAsync(plan, floatArrayOf(0f))
        }
    }

    @Test
    fun `orchestrator rejects an empty plan`() = runTest {
        val plan = LayerStreamingPlan("m", 0, emptyList(), 0)
        assertFailsWith<IllegalArgumentException> {
            LayerStreamingOrchestrator(IdentityLayerStreamingRunner()).forwardAsync(plan, floatArrayOf(0f))
        }
    }

    @Test
    fun `shard discovery parses layer indices and sorts them`() {
        val dir = Files.createTempDirectory("layers").toFile()
        // Create out-of-order layer files + one non-layer file that must be ignored.
        File(dir, "layer_002.safetensors").writeText("cc")
        File(dir, "layer_000.safetensors").writeText("a")
        File(dir, "layer_001.safetensors").writeText("bb")
        File(dir, "config.json").writeText("{}")

        val plan = LayerShardDiscovery.discover("m", dir.absolutePath)
        assertEquals(3, plan.totalLayers)
        assertEquals(listOf(0, 1, 2), plan.shards.map { it.layerIndex })
        assertEquals(1L + 2 + 2, plan.approxParameterBytes) // a=1, bb=2, cc=2
    }

    @Test
    fun `shard discovery throws on a missing directory`() {
        assertFailsWith<java.io.FileNotFoundException> {
            LayerShardDiscovery.discover("m", "/no/such/dir/here")
        }
    }
}
