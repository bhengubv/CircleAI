// MeshOffload.kt
//
// Kotlin port of the RT-12 mesh-offload strategy from
// CircleAI.Inference/MnnInteropRtFeatures.cs — the C# reference is the EXACT
// spec. Routes inference to a peer when local execution is infeasible (low RAM,
// slow CPU, model not loaded locally) or when a faster peer is available. The
// strategy is pure; hosts wire the peer registry.
//
// The other three RT features in the C# file (RT-03 mmap, RT-05 speculative
// decoding, RT-10 LoRA) are P/Invoke wrappers over the `mnnbridge` native
// library — a non-portable native seam — and are intentionally out of scope for
// this cluster; only the pure managed mesh-offload strategy is ported here.
//
// C# -> Kotlin conventions:
//   sealed record                 -> data class
//   Func<IReadOnlyList<MeshPeer>> -> () -> List<MeshPeer>
//   IReadOnlyList<T>              -> List<T>
//   FirstOrDefault / OrderBy      -> minByOrNull (stable pick of the min key)
//   string.Equals(OrdinalIgnore) -> equals(ignoreCase = true)

package com.bhengubv.circleai.inference

/**
 * One peer eligible to run inference on behalf of the local node. Mirrors C#
 * `MeshPeer`.
 *
 * @param peerId Stable id of the peer.
 * @param latencyMs Round-trip latency to the peer, in milliseconds.
 * @param ramBytes RAM the peer has available for a model.
 * @param loadAvg Peer load average (0 = idle, 1 = saturated).
 * @param supportedModels Model ids the peer can already serve.
 */
data class MeshPeer(
    val peerId: String,
    val latencyMs: Double,
    val ramBytes: Long,
    val loadAvg: Double,
    val supportedModels: List<String>,
)

/**
 * The outcome of an offload decision. Mirrors C# `OffloadVerdict`.
 *
 * @param shouldOffload True when the caller should route the request to [targetPeerId].
 * @param targetPeerId The chosen peer, or null when staying local / no eligible peer.
 * @param reason Human-readable rationale (verbatim from the C# reference).
 */
data class OffloadVerdict(
    val shouldOffload: Boolean,
    val targetPeerId: String?,
    val reason: String,
)

/**
 * (3.3.0) Mesh-offload strategy: picks a peer when local execution is infeasible
 * (low RAM, slow CPU, model not loaded locally) or when a faster peer is
 * available. Hosts wire the peer registry; the strategy is pure. Mirrors C#
 * `MeshOffloadStrategy`.
 */
class MeshOffloadStrategy(
    private val peers: () -> List<MeshPeer>,
    private val localRamBytes: Long,
    private val localLoadAvg: Double,
) {
    /**
     * Decide whether to offload [modelId] (which needs [requiredRamBytes] and is
     * expected to take [expectedSecondsLocal] locally). Mirrors C#
     * `MeshOffloadStrategy.Decide`.
     */
    fun decide(modelId: String, requiredRamBytes: Long, expectedSecondsLocal: Double): OffloadVerdict {
        require(modelId.isNotBlank()) { "modelId required" }
        require(requiredRamBytes > 0) { "requiredRamBytes" }

        // 1) Always offload if local can't fit the model.
        if (localRamBytes < requiredRamBytes) {
            val pick = pickBestPeer(modelId, requiredRamBytes)
            return if (pick == null) {
                OffloadVerdict(false, null, "Local can't fit; no eligible peer")
            } else {
                OffloadVerdict(true, pick.peerId, "Local RAM insufficient")
            }
        }

        // 2) Offload if local is overloaded AND a peer can do it noticeably faster.
        if (localLoadAvg > 0.85) {
            val pick = pickBestPeer(modelId, requiredRamBytes)
            if (pick != null && pick.loadAvg < 0.5 && pick.latencyMs < expectedSecondsLocal * 1000 * 0.7) {
                return OffloadVerdict(true, pick.peerId, "Local overloaded; peer faster")
            }
        }

        return OffloadVerdict(false, null, "Local capacity sufficient")
    }

    private fun pickBestPeer(modelId: String, requiredRamBytes: Long): MeshPeer? =
        peers()
            .filter { p ->
                p.ramBytes >= requiredRamBytes &&
                    p.supportedModels.any { it.equals(modelId, ignoreCase = true) }
            }
            // C# OrderBy(...).FirstOrDefault() — ascending by the same composite key.
            .minByOrNull { it.latencyMs + it.loadAvg * 500 }
}
