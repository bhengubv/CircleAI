// security/index.ets — Circle AI security layer (ArkTS / HarmonyOS port)
//
// Port of the Circle.AI.Security portable layer.
//
// Covers:
//   ThreatVector   — stable enum of detectable attack/anomaly vectors
//   AnomalySignal  — record describing a single detected anomaly
//
// CRITICAL: ThreatVector numeric values are part of the wire/storage contract
// (values 0..7). Do NOT reorder entries or change values — any reorder
// silently corrupts serialized signals across the C#, Kotlin, Rust, Swift,
// Python, TS, and Go ports.
//
// ArkTS port rules:
//   - No `any` type
//   - Numeric enum (not string)
//   - `as TypeName` casts on object literals
//   - Scalar number[] loops only — no Float32Array tricks

// ---------------------------------------------------------------------------
// ThreatVector
// ---------------------------------------------------------------------------

/**
 * Classification of a detected anomaly's attack / failure mode.
 *
 * Numeric values are the stable wire format — entries MUST stay in this
 * exact declaration order so values match the C# reference implementation
 * and every other-language port.
 */
export enum ThreatVector {
  /** Unexpected memory write, read pattern, or content mismatch. */
  MemoryAnomaly         = 0,
  /** Execution flow diverged from the expected control-flow graph. */
  ControlFlowDrift      = 1,
  /** Attempt to gain privileges beyond the caller's current scope. */
  PrivilegeEscalation   = 2,
  /** Biometric input flagged as spoofed (replay, mask, deepfake, etc.). */
  BiometricSpoofAttempt = 3,
  /** Lateral movement / pivot detected across network segments. */
  NetworkPivot          = 4,
  /** Persistent state failed integrity check (hash, signature, schema). */
  StateCorruption       = 5,
  /** An agent-issued patch was rejected by the validator. */
  AgentPatchRejected    = 6,
  /** Unclassified anomaly — fallback category. */
  Unknown               = 7,
}

// ---------------------------------------------------------------------------
// AnomalySignal
// ---------------------------------------------------------------------------

/**
 * Record describing a single anomaly detected by the security subsystem.
 * Produced by detectors and consumed by the response pipeline.
 *
 * Use [createAnomalySignal] to construct new signals — it assigns a fresh
 * UUID, stamps `detectedAtMs` with `Date.now()`, clamps `confidence` to
 * `[0, 1]`, and defensively copies `evidence`.
 *
 * `detectedAtMs` is epoch milliseconds UTC — ArkTS module-default context
 * does not expose `Date` instances directly, so the wire format uses a
 * plain `number` instead.
 */
export interface AnomalySignal {
  /** Stable identifier for this signal (UUID v4). */
  id: string;
  /** Threat classification. */
  vector: ThreatVector;
  /** Detector confidence, 0.0–1.0 (clamped by [createAnomalySignal]). */
  confidence: number;
  /** Logical module / component name where the anomaly was observed. */
  affectedModule: string;
  /** Human-readable description of the anomaly. */
  description: string;
  /** Detector-specific evidence (key → value), e.g. addresses, hashes, IPs. */
  evidence: Record<string, string>;
  /** UTC epoch milliseconds at which the anomaly was detected. */
  detectedAtMs: number;
}

/**
 * Builds a new [AnomalySignal] with a fresh UUID v4, `detectedAtMs` stamped
 * to `Date.now()`, `confidence` clamped to `[0, 1]`, and a defensive copy
 * of `evidence` (a `null` / `undefined` `evidence` becomes an empty map).
 */
export function createAnomalySignal(
  vector: ThreatVector,
  confidence: number,
  affectedModule: string,
  description: string,
  evidence?: Record<string, string>,
): AnomalySignal {
  const c: number = Math.min(1, Math.max(0, confidence));
  const copied: Record<string, string> = {} as Record<string, string>;
  if (evidence !== undefined && evidence !== null) {
    const keys: string[] = Object.keys(evidence);
    for (let i: number = 0; i < keys.length; i++) {
      const k: string = keys[i];
      copied[k] = evidence[k];
    }
  }
  return {
    id: uuidV4(),
    vector: vector,
    confidence: c,
    affectedModule: affectedModule,
    description: description,
    evidence: copied,
    detectedAtMs: Date.now(),
  } as AnomalySignal;
}

// ---------------------------------------------------------------------------
// UUID v4 helper
// ---------------------------------------------------------------------------

/**
 * ArkTS-safe UUID v4 generator. Uses `Math.random()` rather than a CSPRNG —
 * sufficient for client-side anomaly signal identifiers (uniqueness, not
 * unpredictability, is the requirement).
 *
 * Format: 8-4-4-4-12 lowercase hex with version nibble `4` and variant
 * nibble in {8, 9, a, b}.
 */
function uuidV4(): string {
  const hex: string = '0123456789abcdef';
  let s: string = '';
  for (let i: number = 0; i < 36; i++) {
    if (i === 8 || i === 13 || i === 18 || i === 23) {
      s += '-';
      continue;
    }
    if (i === 14) {
      s += '4';
      continue;
    }
    if (i === 19) {
      s += hex.charAt(8 + Math.floor(Math.random() * 4));
      continue;
    }
    s += hex.charAt(Math.floor(Math.random() * 16));
  }
  return s;
}
