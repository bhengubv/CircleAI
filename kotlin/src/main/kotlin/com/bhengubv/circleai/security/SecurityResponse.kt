// SecurityResponse.kt
//
// Kotlin port of src/CircleAI.Security/SecurityResponse.cs.
//
// Describes the action taken by ISecurityWatchdog in response to an
// AnomalySignal. Returned from onAnomalyDetected so calling code (e.g. the
// ops-security agent, host application) knows what was done.

package com.bhengubv.circleai.security

import java.time.Instant
import java.util.UUID

/**
 * The type of protective action taken in response to an [AnomalySignal].
 */
enum class SecurityResponseKind {
    /** No action — confidence below threshold or vector is informational. */
    NoAction,

    /**
     * The session's ephemeral UHID key ring was regenerated; prior session keys
     * are revoked and all in-flight requests using old keys will fail.
     */
    KeyRotation,

    /**
     * The affected session or execution sandbox was marked untrusted and
     * isolated from the rest of the runtime.
     */
    SessionRevocation,

    /**
     * A [PeerDirective] was issued to surrounding mesh nodes to isolate the
     * suspected attack origin.
     */
    MeshIsolationSignal,

    /** State was rolled back to the most recent verified [SecurityCheckpoint]. */
    StateRollback,

    /**
     * A combination of responses was applied (e.g. key rotation + mesh
     * isolation). See [SecurityResponse.appliedActions] for the full list.
     */
    Composite,
}

/**
 * Describes the protective action taken by [ISecurityWatchdog] in response to an
 * [AnomalySignal].
 *
 * @property signalId Identifier of the [AnomalySignal] that triggered this
 *   response.
 * @property kind Primary response kind.
 * @property appliedActions When [kind] is [SecurityResponseKind.Composite],
 *   lists each individual action applied. Empty for single-action responses.
 * @property description Human-readable description of what was done and why.
 * @property restoredCheckpoint The [SecurityCheckpoint] that was restored, if
 *   any. `null` when [kind] is not [SecurityResponseKind.StateRollback].
 * @property respondedAt UTC timestamp of the response.
 */
data class SecurityResponse(
    val signalId: UUID,
    val kind: SecurityResponseKind,
    val appliedActions: List<SecurityResponseKind>,
    val description: String,
    val restoredCheckpoint: SecurityCheckpoint?,
    val respondedAt: Instant,
) {
    companion object {
        /** Creates a no-action response for low-confidence or informational signals. */
        fun noAction(signalId: UUID, reason: String): SecurityResponse =
            SecurityResponse(
                signalId,
                SecurityResponseKind.NoAction,
                emptyList(),
                reason,
                null,
                Instant.now(),
            )

        /** Creates a key-rotation response. */
        fun forKeyRotation(signalId: UUID, description: String): SecurityResponse =
            SecurityResponse(
                signalId,
                SecurityResponseKind.KeyRotation,
                emptyList(),
                description,
                null,
                Instant.now(),
            )

        /** Creates a state-rollback response, recording the restored checkpoint. */
        fun forRollback(signalId: UUID, restored: SecurityCheckpoint): SecurityResponse =
            SecurityResponse(
                signalId,
                SecurityResponseKind.StateRollback,
                emptyList(),
                "State rolled back to checkpoint ${restored.id} (${restored.moduleLabel}).",
                restored,
                Instant.now(),
            )

        /** Creates a composite response from multiple individual actions. */
        fun composite(
            signalId: UUID,
            actions: List<SecurityResponseKind>,
            description: String,
            restoredCheckpoint: SecurityCheckpoint? = null,
        ): SecurityResponse =
            SecurityResponse(
                signalId,
                SecurityResponseKind.Composite,
                actions,
                description,
                restoredCheckpoint,
                Instant.now(),
            )
    }
}
