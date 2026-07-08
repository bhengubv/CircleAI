// ServerLifecycle.kt
//
// Kotlin port of CircleAI.Inference.Server.Lifecycle. C# is the EXACT spec.
// Covers:
//   • IBridgeFactory + UnconfiguredBridgeFactory (AdminEndpoints.cs)
//   • ModelLoadDescriptor / ModelLoadState / LoadOutcome / LoadResult /
//     UnloadOutcome (ModelLifecycleTypes.cs)
//   • IModelLifecycleManager (IModelLifecycleManager.cs)
//   • ModelLifecycleManager (ModelLifecycleManager.cs) — admission gate
//   • AdminLoadRequest (AdminEndpoints.cs)
//
// The lifecycle manager is the policy gate around the in-memory registry: it
// decides whether a load is admitted (VRAM/RAM headroom + duplicate check) and
// tracks the on-host footprint. Ported faithfully including the reserve-before-
// factory overcommit guard and the roll-back on factory failure.

package com.bhengubv.circleai.server

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// ── IBridgeFactory ───────────────────────────────────────────────────────────

/**
 * DI factory — the host registers one so the admin path knows how to
 * materialise an [IInferenceBridge] for a given model id + backend. Ports
 * IBridgeFactory.
 */
interface IBridgeFactory {
    suspend fun createAsync(modelId: String, backend: BackendKind, tier: CapabilityTier): IInferenceBridge
}

/** Default implementation — refuses every load with a clear error. */
class UnconfiguredBridgeFactory : IBridgeFactory {
    override suspend fun createAsync(
        modelId: String,
        backend: BackendKind,
        tier: CapabilityTier,
    ): IInferenceBridge = throw IllegalStateException(
        "No IBridgeFactory is configured. Register one before requesting a model load.",
    )
}

// ── Lifecycle DTOs ───────────────────────────────────────────────────────────

/**
 * What the caller wants to load. The factory produces the bridge; the manager
 * handles the admission gate. Ports ModelLoadDescriptor.
 *
 * @param bridgeFactory Called only after the admission gate passes; the
 *   manager's count is incremented before it runs to prevent overcommit races.
 */
data class ModelLoadDescriptor(
    val modelId: String,
    val backend: BackendKind,
    val requestedTier: CapabilityTier,
    val vramRequiredBytes: Long,
    val ramRequiredBytes: Long,
    val bridgeFactory: suspend () -> IInferenceBridge,
)

/** Runtime view of one loaded model (ports ModelLoadState). */
data class ModelLoadState(
    val modelId: String,
    val backend: BackendKind,
    val tier: CapabilityTier,
    val vramBytes: Long,
    val ramBytes: Long,
    val loadedAt: Instant,
)

/** Outcome enum for a load attempt (ports LoadOutcome). */
enum class LoadOutcome {
    /** Bridge factory ran, registry was updated. */
    Loaded,

    /** The model was already loaded — no-op success. */
    AlreadyLoaded,

    /** Insufficient VRAM headroom for the requested footprint. */
    InsufficientVram,

    /** Insufficient RAM headroom for the requested footprint. */
    InsufficientRam,

    /** Bridge factory threw — registry untouched. */
    FactoryFailed,
}

/** Result of a load attempt (ports LoadResult). */
data class LoadResult(
    val outcome: LoadOutcome,
    val state: ModelLoadState?,
    val rationale: String,
)

/** Outcome enum for an unload attempt (ports UnloadOutcome). */
enum class UnloadOutcome {
    /** Model was loaded; bridge was disposed and removed. */
    Unloaded,

    /** Model was not loaded; nothing to do. */
    NotLoaded,
}

/** Request body for a model load (ports AdminLoadRequest). */
data class AdminLoadRequest(
    val modelId: String = "",
    val backend: String = "Cpu",
    val tier: String = "Tier1_Small",
    val vramRequiredBytes: Long = 0,
    val ramRequiredBytes: Long = 0,
)

// ── IModelLifecycleManager ───────────────────────────────────────────────────

/**
 * Admits or rejects model loads based on the host's current capacity, and keeps
 * an authoritative ledger of which models are loaded plus what they consume.
 * Ports IModelLifecycleManager.
 */
interface IModelLifecycleManager {
    /** Try to load [descriptor]. Runs the admission gate BEFORE the factory. */
    suspend fun loadAsync(descriptor: ModelLoadDescriptor): LoadResult

    /** Unload the model with the given id. Disposes the bridge if closeable. */
    suspend fun unloadAsync(modelId: String): UnloadOutcome

    /** Snapshot of every model currently held by the manager. */
    fun list(): List<ModelLoadState>

    /** Total VRAM currently allocated across all loaded models. */
    val totalAllocatedVramBytes: Long

    /** Total system RAM currently allocated across all loaded models. */
    val totalAllocatedRamBytes: Long
}

/**
 * Default [IModelLifecycleManager]. Admission gate:
 *   1. Already loaded under this id?        -> AlreadyLoaded (no-op success)
 *   2. Probe -> VRAM headroom >= requested? -> InsufficientVram if not
 *   3. Probe -> RAM headroom >= requested?  -> InsufficientRam if not
 *   4. Run bridgeFactory()                  -> FactoryFailed on any throw
 *   5. Register in the registry, record state
 *
 * VRAM is only enforced on GPU-class backends; the probe is cached after the
 * first call. Ports ModelLifecycleManager.
 */
class ModelLifecycleManager(
    private val registry: IInferenceServerModelRegistry,
    private val probe: ICapabilityProbe,
) : IModelLifecycleManager {

    private val gate = Mutex()
    private val loaded = ConcurrentHashMap<String, ModelLoadState>()

    @Volatile
    private var cachedProfile: HostProfile? = null

    override val totalAllocatedVramBytes: Long
        get() = loaded.values.sumOf { it.vramBytes }

    override val totalAllocatedRamBytes: Long
        get() = loaded.values.sumOf { it.ramBytes }

    override suspend fun loadAsync(descriptor: ModelLoadDescriptor): LoadResult {
        require(descriptor.modelId.isNotBlank()) { "modelId required" }

        // Idempotent fast path — already loaded is a success.
        loaded[descriptor.modelId]?.let { existing ->
            return LoadResult(
                LoadOutcome.AlreadyLoaded, existing,
                "Model '${descriptor.modelId}' is already loaded (${existing.backend}, ${existing.tier}).",
            )
        }

        val profile = getOrProbe()

        // VRAM admission — only enforced on GPU-class backends.
        if (descriptor.backend.isGpuClass) {
            val vramCeiling = profile.gpu?.vramBytes ?: 0
            val vramFree = vramCeiling - totalAllocatedVramBytes
            if (vramFree < descriptor.vramRequiredBytes) {
                val mib = 1024 * 1024
                return LoadResult(
                    LoadOutcome.InsufficientVram, null,
                    "Need ${descriptor.vramRequiredBytes / mib} MiB VRAM, " +
                        "have ${maxOf(0, vramFree) / mib} MiB free " +
                        "(${totalAllocatedVramBytes / mib} MiB of ${vramCeiling / mib} MiB in use).",
                )
            }
        }

        // RAM admission — always enforced.
        val ramFree = profile.totalPhysicalMemoryBytes - totalAllocatedRamBytes
        if (ramFree < descriptor.ramRequiredBytes) {
            val mib = 1024 * 1024
            return LoadResult(
                LoadOutcome.InsufficientRam, null,
                "Need ${descriptor.ramRequiredBytes / mib} MiB RAM, " +
                    "have ${maxOf(0, ramFree) / mib} MiB free " +
                    "(${totalAllocatedRamBytes / mib} MiB of ${profile.totalPhysicalMemoryBytes / mib} MiB in use).",
            )
        }

        val reserveState = ModelLoadState(
            descriptor.modelId, descriptor.backend, descriptor.requestedTier,
            descriptor.vramRequiredBytes, descriptor.ramRequiredBytes, Instant.now(),
        )

        // Reserve before invoking the factory so concurrent loads see the accounting.
        gate.withLock {
            loaded[descriptor.modelId]?.let { raceWinner ->
                return LoadResult(
                    LoadOutcome.AlreadyLoaded, raceWinner,
                    "Model '${descriptor.modelId}' was loaded by a concurrent request.",
                )
            }
            loaded[descriptor.modelId] = reserveState
        }

        return try {
            val bridge = descriptor.bridgeFactory()
            registry.register(descriptor.modelId, bridge)
            LoadResult(
                LoadOutcome.Loaded, reserveState,
                "Loaded '${descriptor.modelId}' on ${descriptor.backend} at ${descriptor.requestedTier}.",
            )
        } catch (e: Exception) {
            // Roll the reservation back.
            loaded.remove(descriptor.modelId)
            LoadResult(
                LoadOutcome.FactoryFailed, null,
                "Bridge factory for '${descriptor.modelId}' failed: ${e.message}",
            )
        }
    }

    override suspend fun unloadAsync(modelId: String): UnloadOutcome {
        require(modelId.isNotBlank()) { "modelId required" }
        if (loaded.remove(modelId) == null) return UnloadOutcome.NotLoaded

        val bridge = registry.resolve(modelId)
        if (bridge is AutoCloseable) bridge.close()
        registry.deregister(modelId)
        return UnloadOutcome.Unloaded
    }

    override fun list(): List<ModelLoadState> = loaded.values.toList()

    private suspend fun getOrProbe(): HostProfile {
        cachedProfile?.let { return it }
        val p = probe.probeAsync()
        // First writer wins; readers see a consistent snapshot.
        cachedProfile = cachedProfile ?: p
        return cachedProfile!!
    }
}
