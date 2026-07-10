// ISecurityWatchdog.kt
//
// Kotlin port of src/CircleAI.Security/ISecurityWatchdog.cs.
//
// The central contract for the CircleAI local runtime immune system.
//
// Detection sites (companion pipeline, biometric verifier, agent patch gate)
// call onAnomalyDetected when they observe something suspicious. The watchdog
// implementation decides the response: key rotation, session revocation, mesh
// isolation, or state rollback.
//
// The SDK ships DefaultSecurityWatchdog as the out-of-box implementation. Host
// applications can substitute their own (e.g. one that also pages an
// ops-security agent).

package com.bhengubv.circleai.security

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.receiveAsFlow
import java.util.Locale

/**
 * Central contract for the CircleAI local runtime immune system. Receives
 * [AnomalySignal] instances from detection sites and returns the
 * [SecurityResponse] describing protective action taken.
 */
interface ISecurityWatchdog {
    /**
     * Called by any detection site when a local runtime anomaly is observed.
     * The watchdog evaluates [signal] and applies the appropriate protective
     * response.
     *
     * @param signal The detected anomaly.
     * @param checkpoint The most recent [SecurityCheckpoint] for the affected
     *   module, if one is available. Passed so the watchdog can roll back state
     *   without needing to hold a reference to it itself.
     * @return A [SecurityResponse] describing what protective action was taken.
     */
    suspend fun onAnomalyDetected(
        signal: AnomalySignal,
        checkpoint: SecurityCheckpoint? = null,
    ): SecurityResponse

    /**
     * Returns a live stream of every [AnomalySignal] observed since the
     * watchdog started. The [Flow] is cold; collecting it consumes buffered and
     * subsequent signals from the watchdog channel.
     */
    fun streamSignals(): Flow<AnomalySignal>
}

/**
 * Default in-process watchdog. Applies graduated responses based on
 * [ThreatVector] and confidence level:
 * - Confidence < 0.3 -> [SecurityResponseKind.NoAction]
 * - Confidence 0.3–0.6 -> [SecurityResponseKind.KeyRotation]
 * - Confidence > 0.6 + high-severity vector -> [SecurityResponseKind.Composite]
 *   (rotation + mesh signal, plus rollback if a verified checkpoint is present)
 *
 * In-process channel; single-process correct. Not multi-replica safe — signals
 * emitted on replica A do not reach stream subscribers on replica B.
 */
class DefaultSecurityWatchdog : ISecurityWatchdog {

    val componentName: String get() = "DefaultSecurityWatchdog"

    // Unbounded so a signal published before any collector attaches is buffered
    // and delivered on first collection rather than lost.
    private val signals = Channel<AnomalySignal>(Channel.UNLIMITED)

    override suspend fun onAnomalyDetected(
        signal: AnomalySignal,
        checkpoint: SecurityCheckpoint?,
    ): SecurityResponse {
        // Broadcast to any stream subscribers. UNLIMITED channel: never blocks.
        signals.trySend(signal)

        // -- Graduated response policy --------------------------------------

        if (signal.confidence < ROTATION_THRESHOLD) {
            return SecurityResponse.noAction(
                signal.id,
                "Confidence ${p0(signal.confidence)} below rotation threshold — monitoring only.",
            )
        }

        // High-severity vectors always warrant rollback if we have a checkpoint.
        val isHighSeverity = signal.vector == ThreatVector.ControlFlowDrift ||
            signal.vector == ThreatVector.PrivilegeEscalation ||
            signal.vector == ThreatVector.NetworkPivot ||
            signal.vector == ThreatVector.StateCorruption

        if (signal.confidence > COMPOSITE_THRESHOLD) {
            val actions = mutableListOf(
                SecurityResponseKind.KeyRotation,
                SecurityResponseKind.MeshIsolationSignal,
            )

            var restored: SecurityCheckpoint? = null
            if (checkpoint != null && isHighSeverity && checkpoint.verify()) {
                actions.add(SecurityResponseKind.StateRollback)
                restored = checkpoint
            }

            return SecurityResponse.composite(
                signal.id,
                actions,
                "Composite response for ${signal.vector} (confidence ${p0(signal.confidence)}) " +
                    "in ${signal.affectedModule}.",
                restored,
            )
        }

        // Mid-range confidence: rotate keys only.
        return SecurityResponse.forKeyRotation(
            signal.id,
            "Key rotation triggered for ${signal.vector} (confidence ${p0(signal.confidence)}) " +
                "in ${signal.affectedModule}.",
        )
    }

    override fun streamSignals(): Flow<AnomalySignal> = signals.receiveAsFlow()

    companion object {
        private const val ROTATION_THRESHOLD = 0.30f
        private const val COMPOSITE_THRESHOLD = 0.60f

        /**
         * Formats a fraction as a whole-percent string matching .NET invariant
         * `{x:P0}` (e.g. 0.45 -> "45 %").
         */
        private fun p0(fraction: Float): String =
            String.format(Locale.ROOT, "%.0f %%", fraction * 100.0)
    }
}
