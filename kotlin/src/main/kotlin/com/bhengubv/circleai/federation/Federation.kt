// Federation.kt
//
// Kotlin port of CircleAI.Federation — the C# reference is the EXACT spec
// (ModelDelta.cs, FederationRound.cs, IFederationParticipant.cs,
// IFederationAggregator.cs, IFederationDeltaDispatcher.cs,
// FederatedAveraging.cs, InMemoryFederationAggregator.cs).
//
// Federated-learning coordination: participants pull a base model version,
// train locally, and submit a signed ModelDelta. When MinParticipants deltas
// arrive, the aggregator commits a sample-size-weighted average and emits the
// target version. NO raw training data leaves the device — only the delta.
//
// C# -> Kotlin conventions:
//   Guid                 -> java.util.UUID
//   Task                 -> suspend fun
//   byte[] / float[]     -> ByteArray / FloatArray
//   little-endian IEEE754 -> java.nio.ByteBuffer (LITTLE_ENDIAN)
//   Func<ModelDelta,bool> -> (ModelDelta) -> Boolean
//   ConcurrentDictionary -> synchronized MutableMap
// The C# [Experimental] / [CircleAIVerificationStatus] attributes and the
// CircleAIComponentBase diagnostics wrapper carry no wire/behaviour semantics
// and are intentionally not ported.

package com.bhengubv.circleai.federation

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.time.Instant
import java.util.UUID

// ===========================================================================
// ModelDelta  (ModelDelta.cs)
// ===========================================================================

/** One participant's signed contribution to a federation round. */
data class ModelDelta(
    val id: UUID,
    val roundId: UUID,
    val contributorUhid: String,
    val modelId: String,
    val fromVersion: String,
    val deltaPayload: ByteArray,
    val sampleCount: Int,
    val signature: ByteArray,
    val submittedAt: Instant,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is ModelDelta) return false
        return id == other.id &&
            roundId == other.roundId &&
            contributorUhid == other.contributorUhid &&
            modelId == other.modelId &&
            fromVersion == other.fromVersion &&
            deltaPayload.contentEquals(other.deltaPayload) &&
            sampleCount == other.sampleCount &&
            signature.contentEquals(other.signature) &&
            submittedAt == other.submittedAt
    }

    override fun hashCode(): Int {
        var result = id.hashCode()
        result = 31 * result + roundId.hashCode()
        result = 31 * result + contributorUhid.hashCode()
        result = 31 * result + modelId.hashCode()
        result = 31 * result + fromVersion.hashCode()
        result = 31 * result + deltaPayload.contentHashCode()
        result = 31 * result + sampleCount
        result = 31 * result + signature.contentHashCode()
        result = 31 * result + submittedAt.hashCode()
        return result
    }
}

// ===========================================================================
// FederationRound  (FederationRound.cs)
// ===========================================================================

/** Lifecycle state of a [FederationRound]. */
enum class RoundStatus { Open, Aggregating, Committed, Aborted }

/**
 * One coordinated round of federated learning, bound to a specific model
 * version transition (fromVersion -> toVersion).
 */
data class FederationRound(
    val id: UUID,
    val modelId: String,
    val fromVersion: String,
    val toVersion: String,
    val minParticipants: Int,
    val maxParticipants: Int,
    val currentParticipantCount: Int,
    val status: RoundStatus,
    val openedAt: Instant,
    val committedAt: Instant?,
)

// ===========================================================================
// FederatedAveraging  (FederatedAveraging.cs)
// ===========================================================================

/**
 * Sample-size-weighted averaging over [ModelDelta.deltaPayload] arrays
 * interpreted as little-endian IEEE 754 float[].
 */
object FederatedAveraging {
    private const val FLOAT_BYTES = 4

    /**
     * Computes the sample-size-weighted average of the supplied deltas and
     * returns the encoded result as little-endian IEEE 754 bytes.
     */
    fun average(deltas: List<ModelDelta>): ByteArray {
        require(deltas.isNotEmpty()) { "Cannot average an empty delta list." }

        val expectedBytes = deltas[0].deltaPayload.size
        require(expectedBytes != 0) { "Delta payloads must be non-empty." }
        require(expectedBytes % FLOAT_BYTES == 0) {
            "Delta payload length ($expectedBytes) must be a multiple of $FLOAT_BYTES bytes."
        }

        for (i in 1 until deltas.size) {
            require(deltas[i].deltaPayload.size == expectedBytes) {
                "Delta payload length mismatch: index 0 = $expectedBytes bytes, " +
                    "index $i = ${deltas[i].deltaPayload.size} bytes."
            }
        }

        val floatCount = expectedBytes / FLOAT_BYTES
        var totalSamples = 0L
        for (d in deltas) {
            require(d.sampleCount >= 0) {
                "SampleCount must be non-negative; delta ${d.id} reported ${d.sampleCount}."
            }
            totalSamples += d.sampleCount
        }
        require(totalSamples != 0L) {
            "Total sample weight across deltas is zero — cannot perform weighted average."
        }

        val accumulator = DoubleArray(floatCount)
        for (d in deltas) {
            val weight = d.sampleCount.toDouble() / totalSamples
            val buf = ByteBuffer.wrap(d.deltaPayload).order(ByteOrder.LITTLE_ENDIAN)
            for (i in 0 until floatCount) {
                val value = buf.getFloat(i * FLOAT_BYTES)
                accumulator[i] += value * weight
            }
        }

        val output = ByteBuffer.allocate(expectedBytes).order(ByteOrder.LITTLE_ENDIAN)
        for (i in 0 until floatCount) {
            output.putFloat(i * FLOAT_BYTES, accumulator[i].toFloat())
        }
        return output.array()
    }

    /** Encodes a [FloatArray] as little-endian IEEE 754 bytes. */
    fun encodeFloats(values: FloatArray): ByteArray {
        val output = ByteBuffer.allocate(values.size * FLOAT_BYTES).order(ByteOrder.LITTLE_ENDIAN)
        for (i in values.indices) {
            output.putFloat(i * FLOAT_BYTES, values[i])
        }
        return output.array()
    }

    /** Decodes little-endian IEEE 754 bytes into a [FloatArray]. */
    fun decodeFloats(payload: ByteArray): FloatArray {
        require(payload.size % FLOAT_BYTES == 0) {
            "Payload length (${payload.size}) must be a multiple of $FLOAT_BYTES bytes."
        }
        val count = payload.size / FLOAT_BYTES
        val output = FloatArray(count)
        val buf = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN)
        for (i in 0 until count) {
            output[i] = buf.getFloat(i * FLOAT_BYTES)
        }
        return output
    }
}

// ===========================================================================
// Contracts  (IFederationParticipant.cs, IFederationAggregator.cs,
//             IFederationDeltaDispatcher.cs)
// ===========================================================================

/** Contract for a device that contributes to federation rounds. */
interface IFederationParticipant {
    /** Trains locally and returns the resulting signed [ModelDelta]. */
    suspend fun produceDelta(round: FederationRound): ModelDelta

    /** Applies an aggregated model and reports whether the application succeeded. */
    suspend fun applyAggregatedModel(
        modelId: String,
        newVersion: String,
        aggregatedPayload: ByteArray,
    ): Boolean
}

/** Coordinator for federation rounds. */
interface IFederationAggregator {
    suspend fun openRound(
        modelId: String,
        fromVersion: String,
        toVersion: String,
        minParticipants: Int,
        maxParticipants: Int,
    ): FederationRound

    suspend fun submitDelta(delta: ModelDelta)

    /** Returns the aggregated payload when MinParticipants valid deltas are collected; null otherwise. */
    suspend fun tryCommit(roundId: UUID): ByteArray?

    /** Returns the current round snapshot. Throws when the round is unknown. */
    suspend fun getRound(roundId: UUID): FederationRound
}

/** Outcome of a [IFederationDeltaDispatcher.verifyAndSubmit] call. */
enum class DeltaDispatchOutcome {
    Accepted,
    SignatureInvalid,
    Duplicate,
    RoundUnknown,
    RoundClosed,
}

/** Safe-by-default federation delta dispatcher — verify, dedup, and submit in one call. */
interface IFederationDeltaDispatcher {
    suspend fun verifyAndSubmit(delta: ModelDelta): DeltaDispatchOutcome
}

/**
 * Reference [IFederationDeltaDispatcher]. Composes signature verification,
 * replay de-duplication, and submission over an [IFederationAggregator] in a
 * single call so no step can be skipped. No exception is thrown on rejection —
 * the caller branches on the returned [DeltaDispatchOutcome].
 */
class DefaultFederationDeltaDispatcher(
    private val aggregator: IFederationAggregator,
    private val signatureValidator: (ModelDelta) -> Boolean,
) : IFederationDeltaDispatcher {

    private val seen = HashSet<UUID>()
    private val lock = Any()

    override suspend fun verifyAndSubmit(delta: ModelDelta): DeltaDispatchOutcome {
        // 1. Verify the signature first — a forged or unsigned delta never touches the round.
        if (!signatureValidator(delta)) {
            return DeltaDispatchOutcome.SignatureInvalid
        }

        // 2. De-duplicate: atomically claim the delta id; a replay loses the race.
        val claimed = synchronized(lock) { seen.add(delta.id) }
        if (!claimed) {
            return DeltaDispatchOutcome.Duplicate
        }

        // 3. Submit, translating the aggregator's exceptions into outcomes so the
        //    caller can branch on the result without a try/catch of its own.
        return try {
            aggregator.submitDelta(delta)
            DeltaDispatchOutcome.Accepted
        } catch (ex: NoSuchElementException) {
            synchronized(lock) { seen.remove(delta.id) }
            DeltaDispatchOutcome.RoundUnknown
        } catch (ex: IllegalStateException) {
            synchronized(lock) { seen.remove(delta.id) }
            DeltaDispatchOutcome.RoundClosed
        }
    }
}

// ===========================================================================
// InMemoryFederationAggregator  (InMemoryFederationAggregator.cs)
// ===========================================================================

/**
 * In-process reference [IFederationAggregator]. Stores all round and delta
 * state in memory; not durable across process restarts. The signature
 * validator is caller-supplied so this module does not depend on a key ring —
 * pass `{ true }` in tests where signatures are not the subject of test.
 */
class InMemoryFederationAggregator(
    private val signatureValidator: (ModelDelta) -> Boolean,
) : IFederationAggregator {

    private val rounds = HashMap<UUID, RoundState>()
    private val lock = Any()

    override suspend fun openRound(
        modelId: String,
        fromVersion: String,
        toVersion: String,
        minParticipants: Int,
        maxParticipants: Int,
    ): FederationRound {
        require(modelId.isNotEmpty()) { "modelId required" }
        require(fromVersion.isNotEmpty()) { "fromVersion required" }
        require(toVersion.isNotEmpty()) { "toVersion required" }
        require(minParticipants > 0) { "minParticipants must be positive." }
        require(maxParticipants >= minParticipants) {
            "maxParticipants ($maxParticipants) must be >= minParticipants ($minParticipants)."
        }

        val round = FederationRound(
            id = UUID.randomUUID(),
            modelId = modelId,
            fromVersion = fromVersion,
            toVersion = toVersion,
            minParticipants = minParticipants,
            maxParticipants = maxParticipants,
            currentParticipantCount = 0,
            status = RoundStatus.Open,
            openedAt = Instant.now(),
            committedAt = null,
        )
        val state = RoundState(round)
        synchronized(lock) { rounds[round.id] = state }
        return state.snapshot
    }

    override suspend fun submitDelta(delta: ModelDelta) {
        val state = synchronized(lock) { rounds[delta.roundId] }
            ?: throw NoSuchElementException("Round ${delta.roundId} is not open.")

        // Treat empty payloads as invalid: do not store, do not count. The
        // aggregator does not raise — the round remains viable.
        if (delta.deltaPayload.isEmpty()) return

        synchronized(state.lock) {
            if (state.snapshot.status != RoundStatus.Open) {
                throw IllegalStateException(
                    "Round ${delta.roundId} is ${state.snapshot.status}; not accepting deltas.",
                )
            }
            if (state.deltas.size >= state.snapshot.maxParticipants) {
                throw IllegalStateException(
                    "Round ${delta.roundId} has reached MaxParticipants (${state.snapshot.maxParticipants}).",
                )
            }
            state.deltas.add(delta)
            state.snapshot = state.snapshot.copy(currentParticipantCount = state.deltas.size)
        }
    }

    override suspend fun tryCommit(roundId: UUID): ByteArray? {
        val state = synchronized(lock) { rounds[roundId] }
            ?: throw NoSuchElementException("Round $roundId is unknown.")

        synchronized(state.lock) {
            if (state.snapshot.status == RoundStatus.Committed) {
                // Idempotent: re-return the previously committed payload.
                return state.committedPayload
            }
            if (state.snapshot.status == RoundStatus.Aborted) {
                return null
            }

            val validDeltas = state.deltas.filter(signatureValidator)
            if (validDeltas.size < state.snapshot.minParticipants) {
                return null
            }

            state.snapshot = state.snapshot.copy(status = RoundStatus.Aggregating)

            val aggregated: ByteArray = try {
                FederatedAveraging.average(validDeltas)
            } catch (ex: IllegalArgumentException) {
                // Payload encoding inconsistent — fall back to the median delta by SampleCount.
                fallbackMedianPayload(validDeltas)
            }

            state.committedPayload = aggregated
            state.snapshot = state.snapshot.copy(
                status = RoundStatus.Committed,
                committedAt = Instant.now(),
            )
            return aggregated
        }
    }

    override suspend fun getRound(roundId: UUID): FederationRound {
        val state = synchronized(lock) { rounds[roundId] }
            ?: throw NoSuchElementException("Round $roundId is unknown.")
        return synchronized(state.lock) { state.snapshot }
    }

    /** Total number of rounds currently tracked. Diagnostic only. */
    val roundCount: Int get() = synchronized(lock) { rounds.size }

    private fun fallbackMedianPayload(deltas: List<ModelDelta>): ByteArray {
        val ordered = deltas.sortedBy { it.sampleCount }
        val median = ordered[ordered.size / 2]
        return median.deltaPayload.copyOf()
    }

    private class RoundState(initial: FederationRound) {
        var snapshot: FederationRound = initial
        val deltas: MutableList<ModelDelta> = ArrayList()
        var committedPayload: ByteArray? = null
        val lock = Any()
    }
}
