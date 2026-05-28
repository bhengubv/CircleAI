// security/index.ts
// Circle AI security layer — portable schema only.
// Ported from Circle.AI.Security (C#).
//
// Covers:
//   ThreatVector   — stable enum of detectable attack/anomaly vectors (ordinals 0..7)
//   AnomalySignal  — immutable record describing a single detected anomaly
//
// CRITICAL: ThreatVector ordinals are part of the wire/storage contract.
// Entries MUST stay in this exact declaration order so ordinals match the C#
// reference implementation and every other language port.

import { randomUUID } from "node:crypto";

// ─────────────────────────────────────────────────────────────────────────────
// ThreatVector
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Classification of a locally-detected runtime threat or anomaly.
 *
 * Ordinals are stable across language ports; new values must be appended.
 */
export enum ThreatVector {
  /** Unexpected change in a runtime data structure's layout or content. */
  MemoryAnomaly = 0,
  /** Execution flow deviated from the expected call graph. */
  ControlFlowDrift = 1,
  /** A module or session attempted to acquire unauthorised capabilities. */
  PrivilegeEscalation = 2,
  /** A biometric authentication attempt was detected as spoofing or replay. */
  BiometricSpoofAttempt = 3,
  /** Lateral movement detected from a compromised mesh node. */
  NetworkPivot = 4,
  /** Core state vector mutated outside the trusted companion pipeline. */
  StateCorruption = 5,
  /** An agent-generated patch failed the BugHunter quality gate. */
  AgentPatchRejected = 6,
  /** Catch-all for anomalies that do not map to a specific vector. */
  Unknown = 7,
}

// ─────────────────────────────────────────────────────────────────────────────
// AnomalySignal
// ─────────────────────────────────────────────────────────────────────────────

/**
 * An immutable record describing a locally-detected runtime anomaly.
 * Created at the detection site and consumed by the host-side security watchdog.
 */
export interface AnomalySignal {
  /** Stable identifier (UUID v4). */
  readonly id: string;
  /** Classification of the detected threat. */
  readonly vector: ThreatVector;
  /** Confidence that this is a genuine threat, in [0.0, 1.0]. */
  readonly confidence: number;
  /** The module or subsystem where the anomaly was detected. */
  readonly affectedModule: string;
  /** Human-readable description of the anomaly. */
  readonly description: string;
  /** Structured evidence attached by the detection site. */
  readonly evidence: Readonly<Record<string, string>>;
  /** UTC timestamp of detection. */
  readonly detectedAt: Date;
}

/**
 * Creates a new {@link AnomalySignal} with a fresh UUID v4 id and a
 * `detectedAt` stamp of the current UTC time. `confidence` is clamped
 * to `[0.0, 1.0]`. `evidence` is defensively copied; when omitted, an
 * empty object is used.
 */
export function createAnomalySignal(
  vector: ThreatVector,
  confidence: number,
  affectedModule: string,
  description: string,
  evidence?: Record<string, string>,
): AnomalySignal {
  return {
    id: randomUUID(),
    vector,
    confidence: Math.min(1, Math.max(0, confidence)),
    affectedModule,
    description,
    evidence: evidence ? { ...evidence } : {},
    detectedAt: new Date(),
  };
}
