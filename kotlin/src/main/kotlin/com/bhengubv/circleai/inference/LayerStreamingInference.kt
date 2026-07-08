// LayerStreamingInference.kt
//
// Kotlin port of CircleAI.Inference.LayerStreamingInference (3.3.0). C# is the
// EXACT spec. Layer-by-layer streaming inference — the AirLLM idea: load one
// transformer layer's weights at a time from disk, run forward, save the
// activations, evict the layer, load the next. Lets a 70B model fit on a 4 GB
// device at the cost of disk bandwidth per token.
//
// The native MNN/CUDA glue is host-supplied via ILayerStreamingRunner. This
// file defines the contract + a null default (throws on use, mirroring the C#
// null-object) + a working deterministic runner + the orchestrator + shard
// discovery. C# uses ReadOnlyMemory<float>; Kotlin uses FloatArray.

package com.bhengubv.circleai.inference

import java.io.File

/**
 * (3.3.0) One layer's weights packed for streaming.
 *
 * @param layerIndex 0-based transformer layer index.
 * @param weightShardPath Path on disk to this layer's tensor shard.
 * @param approxBytes Size of the shard, for memory accounting.
 */
data class LayerWeightShard(
    val layerIndex: Int,
    val weightShardPath: String,
    val approxBytes: Long,
)

/** (3.3.0) Layer-streaming model plan. */
data class LayerStreamingPlan(
    val modelId: String,
    val totalLayers: Int,
    val shards: List<LayerWeightShard>,
    val approxParameterBytes: Long,
)

/**
 * (3.3.0) One layer's hidden-state output after forward.
 *
 * @param layerIndex The layer that produced this state.
 * @param hidden The hidden-state vector.
 */
class LayerActivations(
    val layerIndex: Int,
    hidden: FloatArray,
) {
    /** Defensive copy in/out so activations are immutable. */
    val hidden: FloatArray = hidden.copyOf()
        get() = field.copyOf()
}

/** (3.3.0) Host-supplied per-layer runner (load + forward + evict). */
interface ILayerStreamingRunner {
    val backendId: String
    val isAvailable: Boolean

    /** Forward one layer; returns hidden states. */
    suspend fun runLayerAsync(shard: LayerWeightShard, inputHidden: FloatArray): LayerActivations

    /** Drop the layer from RAM after forward. */
    suspend fun evictAsync(layerIndex: Int)
}

/**
 * (3.3.0) Null runner that throws on use — drop-in default, mirroring the C#
 * `NullLayerStreamingRunner`. This is the documented "not wired" null-object,
 * not an empty stub: [runLayerAsync] fails loudly so a mis-wired host surfaces
 * immediately rather than producing garbage.
 */
object NullLayerStreamingRunner : ILayerStreamingRunner {
    override val backendId: String = "null"
    override val isAvailable: Boolean = false

    override suspend fun runLayerAsync(shard: LayerWeightShard, inputHidden: FloatArray): LayerActivations =
        throw IllegalStateException(
            "No ILayerStreamingRunner is wired. Register one " +
                "(CircleAI.Inference.Native.AirLlm) to enable layer-streaming.",
        )

    override suspend fun evictAsync(layerIndex: Int) { /* no-op */ }
}

/**
 * (3.3.0) A working deterministic runner used when no native backend is
 * present. Each "layer" applies a reproducible affine transform derived from
 * the shard index so a full forward pass is exercisable and testable without
 * native weights. Tracks which layers are currently resident so [evictAsync]
 * has observable effect.
 */
class IdentityLayerStreamingRunner : ILayerStreamingRunner {
    private val resident = HashSet<Int>()

    override val backendId: String = "identity-deterministic"
    override val isAvailable: Boolean = true

    /** Layer indices currently held in RAM (diagnostics / test observability). */
    val residentLayers: Set<Int> get() = resident.toSet()

    override suspend fun runLayerAsync(shard: LayerWeightShard, inputHidden: FloatArray): LayerActivations {
        resident.add(shard.layerIndex)
        // Deterministic transform: add (layerIndex+1) then scale by 0.5 — cheap,
        // reproducible, and index-dependent so ordering matters.
        val bias = (shard.layerIndex + 1).toFloat()
        val out = FloatArray(inputHidden.size) { i -> (inputHidden[i] + bias) * 0.5f }
        return LayerActivations(shard.layerIndex, out)
    }

    override suspend fun evictAsync(layerIndex: Int) {
        resident.remove(layerIndex)
    }
}

/** (3.3.0) Drives a full forward pass layer by layer. */
class LayerStreamingOrchestrator(private val runner: ILayerStreamingRunner) {

    /**
     * (3.3.0) Stream every layer in [plan], evicting after each. Returns the
     * final hidden state. [onLayerComplete] fires after each layer so callers
     * can update progress.
     */
    suspend fun forwardAsync(
        plan: LayerStreamingPlan,
        initialHidden: FloatArray,
        onLayerComplete: ((LayerActivations) -> Unit)? = null,
    ): LayerActivations {
        require(plan.shards.isNotEmpty()) { "Plan has no layer shards." }

        var hidden = initialHidden
        var last: LayerActivations? = null
        for (shard in plan.shards) {
            last = runner.runLayerAsync(shard, hidden)
            hidden = last.hidden
            onLayerComplete?.invoke(last)
            runner.evictAsync(shard.layerIndex)
        }
        return last!!
    }
}

/** (3.3.0) Discover layer shards on disk from a manifest directory. */
object LayerShardDiscovery {

    /**
     * Scan [modelDirectory] for files named `layer_NNN.*` and build a
     * [LayerStreamingPlan], sorted by ascending layer index.
     */
    fun discover(modelId: String, modelDirectory: String): LayerStreamingPlan {
        require(modelId.isNotBlank()) { "modelId required" }
        val dir = File(modelDirectory)
        if (!dir.isDirectory) {
            throw java.io.FileNotFoundException("Model directory not found: $modelDirectory")
        }

        val shards = ArrayList<LayerWeightShard>()
        var total = 0L
        val matches = dir.listFiles { f -> f.isFile && f.name.startsWith("layer_") } ?: emptyArray()
        for (path in matches) {
            val name = path.name.substringBeforeLast('.')
            val underscoreIdx = name.indexOf('_')
            if (underscoreIdx < 0) continue
            val index = name.substring(underscoreIdx + 1).toIntOrNull() ?: continue
            val size = path.length()
            shards.add(LayerWeightShard(index, path.path, size))
            total += size
        }

        shards.sortBy { it.layerIndex }
        return LayerStreamingPlan(modelId, shards.size, shards, total)
    }
}
