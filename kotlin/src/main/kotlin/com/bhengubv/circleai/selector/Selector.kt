// Selector.kt
//
// ChatCapability flags + ModelSelection + IModelSelector + DeviceAwareModelSelector.

package com.bhengubv.circleai.selector

import com.bhengubv.circleai.catalog.ModelEntry
import com.bhengubv.circleai.device.DeviceProbe
import com.bhengubv.circleai.device.DeviceTier
import com.bhengubv.circleai.registry.ModelRegistryService

/** ChatCapability is a bit-flag enum. */
object ChatCapability {
    const val NONE: Int = 0
    const val DEFAULT: Int = 1
    const val TOOLS: Int = 2
    const val VISION: Int = 4
    const val LONG_CONTEXT: Int = 8
    const val REASONING: Int = 16
}

fun Int.hasAllFlags(required: Int): Boolean = (this and required) == required

data class ModelSelection(
    val modelId: String,
    val requiresDownload: Boolean,
    val estimatedBytes: Long,
    val tier: DeviceTier,
)

interface IModelSelector {
    fun bestFit(probe: DeviceProbe, required: Int = ChatCapability.DEFAULT): ModelSelection
    fun allCandidates(probe: DeviceProbe): List<ModelSelection>
}

class DeviceAwareModelSelector(private val registry: ModelRegistryService) : IModelSelector {
    override fun bestFit(probe: DeviceProbe, required: Int): ModelSelection {
        val entries = registry.allModels
        require(entries.isNotEmpty()) { "Model registry is empty. Cannot select a model." }
        val ramGb = probe.ramAvailableBytes.toDouble() / (1024.0 * 1024 * 1024)
        val storageGb = probe.storageFreeBytes.toDouble() / (1024.0 * 1024 * 1024)

        val capabilityOk = entries.filter { satisfiesCapability(it, required) }
        require(capabilityOk.isNotEmpty()) {
            "No model satisfies required capabilities $required."
        }

        val deviceOk = capabilityOk.filter {
            it.minRamGb <= ramGb + 1e-4 &&
                (storageGb <= 0.0 || it.minStorageGb <= storageGb + 1e-4)
        }
        val candidates = if (deviceOk.isNotEmpty()) deviceOk else capabilityOk
        val winner = candidates.maxWith(
            compareByDescending<ModelEntry> { it.qualityRank }
                .thenBy { it.minRamGb }
        )

        return ModelSelection(
            modelId = winner.name,
            requiresDownload = true,
            estimatedBytes = winner.totalBytes,
            tier = probe.classify(),
        )
    }

    override fun allCandidates(probe: DeviceProbe): List<ModelSelection> {
        val tier = probe.classify()
        return registry.allModels
            .sortedByDescending { it.qualityRank }
            .map {
                ModelSelection(
                    modelId = it.name,
                    requiresDownload = true,
                    estimatedBytes = it.totalBytes,
                    tier = tier,
                )
            }
    }
}

private fun satisfiesCapability(entry: ModelEntry, required: Int): Boolean {
    if (required == ChatCapability.NONE) return true
    val declared = parseCapabilities(entry.capabilities)
    return declared.hasAllFlags(required)
}

fun parseCapabilities(labels: List<String>?): Int {
    if (labels.isNullOrEmpty()) return ChatCapability.DEFAULT
    var result = 0
    for (label in labels) {
        val key = label.trim().uppercase().replace(" ", "_")
        when (key) {
            "DEFAULT" -> result = result or ChatCapability.DEFAULT
            "TOOLS" -> result = result or ChatCapability.TOOLS
            "VISION" -> result = result or ChatCapability.VISION
            "LONGCONTEXT", "LONG_CONTEXT" -> result = result or ChatCapability.LONG_CONTEXT
            "REASONING" -> result = result or ChatCapability.REASONING
        }
    }
    return if (result == 0) ChatCapability.DEFAULT else result
}
