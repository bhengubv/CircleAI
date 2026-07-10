// AetherNet.kt
//
// Kotlin port of the self-contained RT-12 v1 mesh-capability discovery types
// from src/CircleAI.AetherNet/MeshCapabilityRegistry.cs.
//
// Mesh capability discovery: peers broadcast what they have loaded ("I have
// Qwen3-1.7B-MNN with 2048 tokens of free KV budget on a Tier=Phone device").
// v1 ships the contracts + an in-memory registry; the AetherNet broadcast
// transport lands later with RT-12 v2 actual offload.
//
// The remaining CircleAI.AetherNet types (AetherNetContextAdapter,
// AetherNetTelemetryAdapter, EventTranslator, CircleAiAetherNetAiProvider,
// the directive bridges, AetherNetCompanionStateChannel) bind to the EXTERNAL
// AetherNet.* protocol package (AetherNet.Extensibility / .Messaging /
// .Protocol / .Constants), which is a separate repository and is not part of
// this Kotlin tree — so they are intentionally out of scope for this port.
//
// C#→Kotlin conventions:
//   record                → data class
//   ValueTask / ValueTask<T> → suspend fun
//   ConcurrentDictionary  → ConcurrentHashMap
//   Func<DateTimeOffset>  → () -> Instant
//   DeviceTier            → reused from com.bhengubv.circleai.device.DeviceTier

package com.bhengubv.circleai.aethernet

import com.bhengubv.circleai.device.DeviceTier
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/**
 * (RT-12 v1) One peer's advertisement of what it can serve right now. Pure data
 * — no execution state.
 *
 * @property peerId Stable opaque identifier for the advertising peer.
 * @property modelId The model the peer has loaded, e.g. `"Qwen3-1.7B-MNN"`.
 * @property freeKvTokens How many tokens of KV-cache budget the peer has spare.
 * @property tier The peer's device tier (Wearable .. Workstation).
 * @property contextWindowTokens The model's configured context window.
 * @property advertisedAtUtc When the peer last published this advertisement.
 * @property latencyHintMs Optional round-trip estimate; null when unknown.
 */
data class MeshCapabilityAdvertisement(
    val peerId: String,
    val modelId: String,
    val freeKvTokens: Int,
    val tier: DeviceTier,
    val contextWindowTokens: Int,
    val advertisedAtUtc: Instant,
    val latencyHintMs: Int? = null,
)

/**
 * (RT-12 v1) Holds the latest advertisement per peer + supports filtered query.
 * The AetherNet transport (v2) feeds this registry as peers broadcast; v1 lets
 * hosting layers query and reason about availability without yet routing.
 */
interface IMeshCapabilityRegistry {
    /**
     * Publish or replace an advertisement. Called by the transport on receipt
     * of a peer broadcast.
     */
    suspend fun upsert(ad: MeshCapabilityAdvertisement)

    /** Remove a peer (e.g. on explicit disconnect). Idempotent. */
    suspend fun remove(peerId: String): Boolean

    /**
     * Return every advertisement currently known. Use [staleAfter] to filter
     * out entries older than this duration. A `null` value returns everything.
     */
    fun list(staleAfter: Duration? = null): List<MeshCapabilityAdvertisement>

    /**
     * Find every peer that has loaded [modelId] with at least [minFreeKvTokens]
     * of spare KV budget. Sorted by spare budget descending — the most-capable
     * peer comes first.
     */
    fun find(
        modelId: String,
        minFreeKvTokens: Int = 0,
        staleAfter: Duration? = null,
    ): List<MeshCapabilityAdvertisement>
}

/**
 * (RT-12 v1) Default [IMeshCapabilityRegistry] — in-memory, thread-safe. The
 * AetherNet transport plugs into this; without a transport the registry just
 * stays empty (no peers).
 *
 * @param nowUtc Optional clock override for tests. Defaults to [Instant.now].
 */
class InMemoryMeshCapabilityRegistry(
    private val nowUtc: () -> Instant = { Instant.now() },
) : IMeshCapabilityRegistry {

    private val entries = ConcurrentHashMap<String, MeshCapabilityAdvertisement>()

    override suspend fun upsert(ad: MeshCapabilityAdvertisement) {
        require(ad.peerId.isNotBlank()) { "PeerId is required." }
        entries[ad.peerId] = ad
    }

    override suspend fun remove(peerId: String): Boolean {
        require(peerId.isNotBlank()) { "peerId is required." }
        return entries.remove(peerId) != null
    }

    override fun list(staleAfter: Duration?): List<MeshCapabilityAdvertisement> {
        if (staleAfter == null) return entries.values.toList()
        val cutoff = nowUtc().minus(staleAfter)
        return entries.values.filter { !it.advertisedAtUtc.isBefore(cutoff) }
    }

    override fun find(
        modelId: String,
        minFreeKvTokens: Int,
        staleAfter: Duration?,
    ): List<MeshCapabilityAdvertisement> {
        require(modelId.isNotBlank()) { "modelId is required." }
        val cutoff = if (staleAfter != null) nowUtc().minus(staleAfter) else Instant.MIN
        return entries.values
            .filter { it.modelId.equals(modelId, ignoreCase = true) }
            .filter { it.freeKvTokens >= minFreeKvTokens }
            .filter { !it.advertisedAtUtc.isBefore(cutoff) }
            .sortedByDescending { it.freeKvTokens }
    }
}

/**
 * (RT-12 v1) Contract for the broadcaster that publishes OUR advertisement to
 * the mesh. v1 ships a no-op default; the AetherNet transport binding (v2)
 * supersedes it.
 */
fun interface IMeshCapabilityBroadcaster {
    /**
     * Publish our current advertisement to the mesh. v1 may be a no-op when no
     * transport is registered.
     */
    suspend fun broadcast(ad: MeshCapabilityAdvertisement)
}

/**
 * Default broadcaster — does nothing. Used when no AetherNet transport is bound.
 * Existing CircleAI deployments work unchanged.
 */
object NullMeshCapabilityBroadcaster : IMeshCapabilityBroadcaster {
    override suspend fun broadcast(ad: MeshCapabilityAdvertisement) {
        // no-op: no transport bound
    }
}

/**
 * A broadcaster that mirrors every broadcast into a local
 * [IMeshCapabilityRegistry] — the deterministic in-memory stand-in for the v2
 * transport. Broadcasting our own advertisement makes it queryable locally
 * (and, once a real transport is bound, on peers too). Useful for single-process
 * simulation and tests where the "mesh" is just the local registry.
 */
class LocalRegistryBroadcaster(
    private val registry: IMeshCapabilityRegistry,
) : IMeshCapabilityBroadcaster {
    override suspend fun broadcast(ad: MeshCapabilityAdvertisement) {
        registry.upsert(ad)
    }
}
