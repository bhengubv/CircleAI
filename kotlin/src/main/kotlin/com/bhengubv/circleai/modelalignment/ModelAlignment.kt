// ModelAlignment.kt
//
// Kotlin port of CircleAI.ModelAlignment — the C# reference is the EXACT spec
// (Contracts.cs, InMemoryModelAlignment.cs, NullImplementations.cs).
//
// (2.6.0/3.3.0) Model-alignment surface. Pattern-port of OBLITERATUS. Targeted
// abliteration lives behind contracts so a host can apply / revert it
// deliberately — and so we can refuse to publish abliterated weights.
//
// Type map (C# -> Kotlin):
//   record AlignmentProfile              -> data class AlignmentProfile
//   record AlignmentResult               -> data class AlignmentResult
//   interface IAlignmentToolkit          -> interface IAlignmentToolkit (suspend apply/revert/list)
//   interface IAlignmentAuditor          -> interface IAlignmentAuditor (suspend assertOkToPublish)
//   class InMemoryAlignmentToolkit       -> class InMemoryAlignmentToolkit
//   class RefuseAlignedPublishAuditor    -> class RefuseAlignedPublishAuditor
//   Null*                                -> object Null* (fail-closed singletons)
//
// C# `ValueTask<T>` async maps to Kotlin `suspend fun`. C# InvalidOperationException
// maps to Kotlin IllegalStateException; ArgumentException -> IllegalArgumentException.

package com.bhengubv.circleai.modelalignment

import java.time.Instant

// ---------------------------------------------------------------------------
// AlignmentProfile + AlignmentResult
// ---------------------------------------------------------------------------

/**
 * A named alignment/abliteration delta that can be applied to a model's weights.
 */
data class AlignmentProfile(
    val profileId: String,
    val description: String,
    val refusalCategoriesRemoved: List<String>,
    val createdAtUtc: Instant,
    val isReversible: Boolean,
)

/**
 * The outcome of an apply / revert operation.
 */
data class AlignmentResult(
    val profileId: String,
    val success: Boolean,
    val failureReason: String?,
)

// ---------------------------------------------------------------------------
// Contracts
// ---------------------------------------------------------------------------

/** (2.6.0) Targeted abliteration toolkit. Apply / revert / list alignment profiles. */
interface IAlignmentToolkit {
    val backendId: String

    suspend fun applyAsync(modelId: String, profile: AlignmentProfile): AlignmentResult

    suspend fun revertAsync(modelId: String, profileId: String): AlignmentResult

    suspend fun listAppliedAsync(modelId: String): List<AlignmentProfile>
}

/** (2.6.0) Refuses to upload / publish weights that carry alignment deltas. */
interface IAlignmentAuditor {
    val backendId: String

    /** Throw or refuse if the model has applied alignment profiles and the action is "publish upstream". */
    suspend fun assertOkToPublishAsync(modelId: String)
}

// ---------------------------------------------------------------------------
// InMemoryAlignmentToolkit
// ---------------------------------------------------------------------------

/**
 * (3.3.0) Real in-memory alignment toolkit. [applyAsync] only allows reversible
 * profiles (matches the "no permanent abliteration" licence stance). Thread-safe:
 * all mutations of the per-model profile lists are guarded by a single monitor,
 * mirroring the C# `lock (_lock)` around the ConcurrentDictionary access.
 */
class InMemoryAlignmentToolkit : IAlignmentToolkit {

    private val byModel = HashMap<String, MutableList<AlignmentProfile>>()
    private val lock = Any()

    override val backendId: String get() = "in-memory"

    override suspend fun applyAsync(modelId: String, profile: AlignmentProfile): AlignmentResult {
        require(modelId.isNotBlank()) { "modelId required" }
        if (!profile.isReversible) {
            return AlignmentResult(
                profile.profileId,
                false,
                "Non-reversible alignment refused by InMemoryAlignmentToolkit",
            )
        }

        synchronized(lock) {
            val list = byModel.getOrPut(modelId) { ArrayList() }
            list.add(profile)
        }
        return AlignmentResult(profile.profileId, true, null)
    }

    override suspend fun revertAsync(modelId: String, profileId: String): AlignmentResult {
        require(modelId.isNotBlank()) { "modelId required" }
        require(profileId.isNotBlank()) { "profileId required" }
        synchronized(lock) {
            val list = byModel[modelId]
                ?: return AlignmentResult(profileId, false, "Unknown model")
            val before = list.size
            list.removeAll { it.profileId == profileId }
            val removed = before - list.size
            return if (removed > 0) {
                AlignmentResult(profileId, true, null)
            } else {
                AlignmentResult(profileId, false, "Profile not applied to this model")
            }
        }
    }

    override suspend fun listAppliedAsync(modelId: String): List<AlignmentProfile> {
        require(modelId.isNotBlank()) { "modelId required" }
        synchronized(lock) {
            val list = byModel[modelId] ?: return emptyList()
            return list.toList()
        }
    }
}

// ---------------------------------------------------------------------------
// RefuseAlignedPublishAuditor
// ---------------------------------------------------------------------------

/**
 * (3.3.0) Refuses to publish weights that carry alignment deltas. Wired by
 * default. Throws [IllegalStateException] (C# InvalidOperationException) when the
 * model has one or more applied profiles.
 */
class RefuseAlignedPublishAuditor(private val toolkit: IAlignmentToolkit) : IAlignmentAuditor {

    override val backendId: String get() = "refuse-aligned"

    override suspend fun assertOkToPublishAsync(modelId: String) {
        require(modelId.isNotBlank()) { "modelId required" }
        val applied = toolkit.listAppliedAsync(modelId)
        if (applied.isNotEmpty()) {
            throw IllegalStateException(
                "Cannot publish '$modelId': ${applied.size} alignment profile(s) applied — " +
                    "this would distribute weights with safety modifications.",
            )
        }
    }
}

// ---------------------------------------------------------------------------
// Fail-closed Null implementations
// ---------------------------------------------------------------------------

/**
 * (2.6.0) Fail-closed toolkit — refuses to apply or revert anything and lists
 * nothing as applied.
 */
object NullAlignmentToolkit : IAlignmentToolkit {
    override val backendId: String get() = "null"

    override suspend fun applyAsync(modelId: String, profile: AlignmentProfile): AlignmentResult =
        AlignmentResult(
            profileId = profile.profileId,
            success = false,
            failureReason = "NullAlignmentToolkit: no real backend wired.",
        )

    override suspend fun revertAsync(modelId: String, profileId: String): AlignmentResult =
        AlignmentResult(
            profileId = profileId,
            success = false,
            failureReason = "NullAlignmentToolkit: nothing to revert.",
        )

    override suspend fun listAppliedAsync(modelId: String): List<AlignmentProfile> = emptyList()
}

/** (2.6.0) Null auditor — always asserts ok-to-publish (nothing was applied). */
object NullAlignmentAuditor : IAlignmentAuditor {
    override val backendId: String get() = "null"
    override suspend fun assertOkToPublishAsync(modelId: String) { /* no-op */ }
}
