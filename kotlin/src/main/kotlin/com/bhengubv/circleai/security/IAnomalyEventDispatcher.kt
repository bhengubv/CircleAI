// IAnomalyEventDispatcher.kt
//
// Kotlin port of src/CircleAI.Security/IAnomalyEventDispatcher.cs.
//
// Safe-by-default composer around ISecurityWatchdog.
//
// The bare ISecurityWatchdog.onAnomalyDetected path requires the caller to
// verify the signal (origin trust, schema, threshold gate) and dedupe (by id,
// by composite hash) themselves. The dispatcher folds verify -> dedup -> invoke
// into one call so a production consumer cannot accidentally accept an
// unverified or replayed signal.

package com.bhengubv.circleai.security

import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

/**
 * Verify, dedup, and dispatch an [AnomalySignal] in a single call. Returns an
 * [AnomalyDispatchResult] describing what happened — no exception is thrown on
 * rejection so the caller can branch on the outcome without try/catch.
 */
interface IAnomalyEventDispatcher {
    /**
     * Runs the verification pipeline configured on this dispatcher (origin
     * trust, optional signature check, confidence threshold) and, when all gates
     * pass, hands the signal to the wrapped [ISecurityWatchdog]. Returns the
     * dispatch outcome along with the watchdog response if invocation was
     * reached.
     */
    suspend fun verifyAndDispatch(
        signal: AnomalySignal,
        checkpoint: SecurityCheckpoint? = null,
    ): AnomalyDispatchResult
}

/** Outcome of a [IAnomalyEventDispatcher.verifyAndDispatch] call. */
enum class AnomalyDispatchOutcome(val code: Int) {
    /** Signal accepted; watchdog was invoked. */
    Dispatched(0),

    /** Signal id was already seen — deduped silently. */
    Duplicate(1),

    /** Confidence was below the configured threshold — ignored. */
    BelowThreshold(2),

    /** Signal failed the origin/signature verification step. */
    Unverified(3),

    /** Cancellation was requested before dispatch. */
    Cancelled(4),
}

/**
 * Result of a dispatch attempt.
 *
 * @property outcome What the dispatcher did with the signal.
 * @property response The watchdog response, when [outcome] is
 *   [AnomalyDispatchOutcome.Dispatched]; `null` otherwise.
 */
data class AnomalyDispatchResult(
    val outcome: AnomalyDispatchOutcome,
    val response: SecurityResponse?,
)

/**
 * Default in-process dispatcher. Threshold-gated, id-deduped, no signature
 * verification (configure your own by composing this with a signature-verifying
 * wrapper when running over an untrusted transport).
 *
 * @param watchdog The watchdog to forward verified signals to.
 * @param minimumConfidence Drop signals whose [AnomalySignal.confidence] is
 *   below this value. Default 0.30 — matches the default watchdog rotation
 *   threshold so signals that would have been no-ops aren't even dispatched.
 */
class DefaultAnomalyEventDispatcher(
    private val watchdog: ISecurityWatchdog,
    minimumConfidence: Double = 0.30,
) : IAnomalyEventDispatcher {

    private val minimumConfidence: Double = minimumConfidence.coerceIn(0.0, 1.0)
    private val seen: MutableSet<UUID> = ConcurrentHashMap.newKeySet()

    override suspend fun verifyAndDispatch(
        signal: AnomalySignal,
        checkpoint: SecurityCheckpoint?,
    ): AnomalyDispatchResult {
        if (!currentCoroutineContext().isActive) {
            return AnomalyDispatchResult(AnomalyDispatchOutcome.Cancelled, null)
        }

        if (signal.confidence < minimumConfidence) {
            return AnomalyDispatchResult(AnomalyDispatchOutcome.BelowThreshold, null)
        }

        if (!seen.add(signal.id)) {
            return AnomalyDispatchResult(AnomalyDispatchOutcome.Duplicate, null)
        }

        val response = watchdog.onAnomalyDetected(signal, checkpoint)
        return AnomalyDispatchResult(AnomalyDispatchOutcome.Dispatched, response)
    }
}
