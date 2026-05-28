// Security.kt
//
// Kotlin port of the Circle.AI.Security portable layer.
//
// Covers:
//   ThreatVector   — stable enum of detectable attack/anomaly vectors
//   AnomalySignal  — immutable record describing a single detected anomaly
//
// CRITICAL: ThreatVector ordinal order is part of the wire/storage contract
// (ordinals 0..7). Do NOT reorder entries. Kotlin assigns enum ordinals by
// declaration order, so any reorder silently corrupts serialized signals.

package com.bhengubv.circleai.security

import java.time.Instant
import java.util.UUID

// ---------------------------------------------------------------------------
// ThreatVector
// ---------------------------------------------------------------------------

/**
 * Classification of a detected anomaly's attack/failure mode.
 *
 * Ordinal order is stable wire format — entries MUST stay in this exact
 * declaration order so ordinals match the C# reference implementation and
 * the other-language ports (Swift, Python, TS, Go, etc.).
 */
enum class ThreatVector {
    /** Unexpected memory write, read pattern, or content mismatch. (0) */
    MemoryAnomaly,
    /** Execution flow diverged from the expected control-flow graph. (1) */
    ControlFlowDrift,
    /** Attempt to gain privileges beyond the caller's current scope. (2) */
    PrivilegeEscalation,
    /** Biometric input flagged as spoofed (replay, mask, deepfake, etc.). (3) */
    BiometricSpoofAttempt,
    /** Lateral movement / pivot detected across network segments. (4) */
    NetworkPivot,
    /** Persistent state failed integrity check (hash, signature, schema). (5) */
    StateCorruption,
    /** An agent-issued patch was rejected by the validator. (6) */
    AgentPatchRejected,
    /** Unclassified anomaly — fallback category. (7) */
    Unknown,
}

// ---------------------------------------------------------------------------
// AnomalySignal
// ---------------------------------------------------------------------------

/**
 * Immutable record describing a single anomaly detected by the security
 * subsystem. Produced by detectors and consumed by the response pipeline.
 *
 * Use [create] to construct new signals — it assigns a fresh [UUID],
 * stamps [detectedAt] with the current UTC instant, clamps [confidence]
 * to `[0f, 1f]`, and defensively copies [evidence].
 */
data class AnomalySignal(
    /** Stable identifier for this signal. */
    val id: UUID,
    /** Threat classification. */
    val vector: ThreatVector,
    /** Detector confidence, 0.0–1.0 (clamped by [create]). */
    val confidence: Float,
    /** Logical module / component name where the anomaly was observed. */
    val affectedModule: String,
    /** Human-readable description of the anomaly. */
    val description: String,
    /** Detector-specific evidence (key → value), e.g. addresses, hashes, IPs. */
    val evidence: Map<String, String>,
    /** UTC time the anomaly was detected. */
    val detectedAt: Instant,
) {
    companion object {
        /**
         * Builds a new [AnomalySignal] with a fresh [UUID] id and a
         * [detectedAt] stamp of [Instant.now]. [confidence] is clamped
         * to `[0f, 1f]`. [evidence] is defensively copied; `null` becomes
         * an empty map.
         */
        fun create(
            vector: ThreatVector,
            confidence: Float,
            affectedModule: String,
            description: String,
            evidence: Map<String, String>? = null,
        ): AnomalySignal = AnomalySignal(
            id = UUID.randomUUID(),
            vector = vector,
            confidence = confidence.coerceIn(0f, 1f),
            affectedModule = affectedModule,
            description = description,
            evidence = evidence?.toMap() ?: emptyMap(),
            detectedAt = Instant.now(),
        )
    }
}
