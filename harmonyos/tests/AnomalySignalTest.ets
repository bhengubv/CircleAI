// AnomalySignalTest.ets — Cross-language AnomalySignal verification (ArkTS)
//
// Verifies:
//   - ThreatVector numeric values (stable wire/storage contract 0..7)
//   - createAnomalySignal clamps confidence to [0, 1] per the cross-port
//     test plan vectors (above_max, below_min, at_max, at_min, nominal)
//   - createAnomalySignal assigns a fresh UUID v4 per call
//   - createAnomalySignal stamps detectedAtMs within a tight Date.now() window
//   - createAnomalySignal defaults evidence to {} when omitted
//   - createAnomalySignal copies evidence defensively (snapshot at call time)
//   - createAnomalySignal preserves vector, affectedModule, description
//
// Uses if/throw assertion pattern (no external test framework).

import {
  AnomalySignal,
  ThreatVector,
  createAnomalySignal,
} from '../src/main/ets/security/index';

// ---------------------------------------------------------------------------
// Assertion helpers
// ---------------------------------------------------------------------------

function assertExact(label: string, actual: number, expected: number): void {
  if (actual !== expected) {
    throw new Error(`FAIL ${label}: expected exactly ${expected}, got ${actual}`);
  }
}

function assertEqualsStr(label: string, actual: string, expected: string): void {
  if (actual !== expected) {
    throw new Error(`FAIL ${label}: expected '${expected}', got '${actual}'`);
  }
}

function assertTrue(label: string, condition: boolean): void {
  if (!condition) throw new Error(`FAIL ${label}: condition was false`);
}

function assertFalse(label: string, condition: boolean): void {
  if (condition) throw new Error(`FAIL ${label}: condition was true (expected false)`);
}

// ---------------------------------------------------------------------------
// ThreatVector — stable wire contract (numeric values 0..7)
// ---------------------------------------------------------------------------

((): void => {
  assertExact('TV_MemoryAnomaly',         ThreatVector.MemoryAnomaly,         0);
  assertExact('TV_ControlFlowDrift',      ThreatVector.ControlFlowDrift,      1);
  assertExact('TV_PrivilegeEscalation',   ThreatVector.PrivilegeEscalation,   2);
  assertExact('TV_BiometricSpoofAttempt', ThreatVector.BiometricSpoofAttempt, 3);
  assertExact('TV_NetworkPivot',          ThreatVector.NetworkPivot,          4);
  assertExact('TV_StateCorruption',       ThreatVector.StateCorruption,       5);
  assertExact('TV_AgentPatchRejected',    ThreatVector.AgentPatchRejected,    6);
  assertExact('TV_Unknown',               ThreatVector.Unknown,               7);
  console.log('PASS: ThreatVector_numeric_values');
})();

// ---------------------------------------------------------------------------
// Confidence clamp vector: above_max — 1.5 → 1.0
// ---------------------------------------------------------------------------

((): void => {
  const s: AnomalySignal = createAnomalySignal(
    ThreatVector.MemoryAnomaly,
    1.5,
    'mod',
    'above max',
  );
  assertExact('clamp_above_max_confidence', s.confidence, 1.0);
  console.log('PASS: clamp_above_max');
})();

// ---------------------------------------------------------------------------
// Confidence clamp vector: below_min — -0.3 → 0.0
// ---------------------------------------------------------------------------

((): void => {
  const s: AnomalySignal = createAnomalySignal(
    ThreatVector.MemoryAnomaly,
    -0.3,
    'mod',
    'below min',
  );
  assertExact('clamp_below_min_confidence', s.confidence, 0.0);
  console.log('PASS: clamp_below_min');
})();

// ---------------------------------------------------------------------------
// Confidence clamp vector: at_max — 1.0 → 1.0 (unchanged)
// ---------------------------------------------------------------------------

((): void => {
  const s: AnomalySignal = createAnomalySignal(
    ThreatVector.MemoryAnomaly,
    1.0,
    'mod',
    'at max',
  );
  assertExact('clamp_at_max_confidence', s.confidence, 1.0);
  console.log('PASS: clamp_at_max');
})();

// ---------------------------------------------------------------------------
// Confidence clamp vector: at_min — 0.0 → 0.0 (unchanged)
// ---------------------------------------------------------------------------

((): void => {
  const s: AnomalySignal = createAnomalySignal(
    ThreatVector.MemoryAnomaly,
    0.0,
    'mod',
    'at min',
  );
  assertExact('clamp_at_min_confidence', s.confidence, 0.0);
  console.log('PASS: clamp_at_min');
})();

// ---------------------------------------------------------------------------
// Confidence clamp vector: nominal — 0.7 → 0.7 (unchanged)
// ---------------------------------------------------------------------------

((): void => {
  const s: AnomalySignal = createAnomalySignal(
    ThreatVector.MemoryAnomaly,
    0.7,
    'mod',
    'nominal',
  );
  assertExact('clamp_nominal_confidence', s.confidence, 0.7);
  console.log('PASS: clamp_nominal');
})();

// ---------------------------------------------------------------------------
// Fresh id per call
// ---------------------------------------------------------------------------

((): void => {
  const a: AnomalySignal = createAnomalySignal(ThreatVector.Unknown, 0.5, 'mod', 'first');
  const b: AnomalySignal = createAnomalySignal(ThreatVector.Unknown, 0.5, 'mod', 'second');
  if (a.id === b.id) {
    throw new Error(`FAIL fresh_id: both signals share id '${a.id}'`);
  }
  // Sanity-check UUID v4 shape: 36 chars, hyphens at 8/13/18/23, version '4' at 14
  assertExact('id_length',          a.id.length,        36);
  assertEqualsStr('id_hyphen_8',    a.id.charAt(8),     '-');
  assertEqualsStr('id_hyphen_13',   a.id.charAt(13),    '-');
  assertEqualsStr('id_hyphen_18',   a.id.charAt(18),    '-');
  assertEqualsStr('id_hyphen_23',   a.id.charAt(23),    '-');
  assertEqualsStr('id_version_4',   a.id.charAt(14),    '4');
  console.log('PASS: fresh_uuid_per_call');
})();

// ---------------------------------------------------------------------------
// detectedAtMs stamped within a tight window of Date.now()
// ---------------------------------------------------------------------------

((): void => {
  const before: number = Date.now();
  const s: AnomalySignal = createAnomalySignal(
    ThreatVector.NetworkPivot,
    0.5,
    'net',
    'probe',
  );
  const after: number = Date.now();
  // Allow a 1s slack window on either side to absorb clock-resolution jitter
  assertTrue('detectedAtMs_after_before',  s.detectedAt.getTime() >= before - 1000);
  assertTrue('detectedAtMs_before_after',  s.detectedAt.getTime() <= after + 1000);
  console.log('PASS: detectedAtMs_within_window');
})();

// ---------------------------------------------------------------------------
// evidence defaults to {} when omitted
// ---------------------------------------------------------------------------

((): void => {
  const s: AnomalySignal = createAnomalySignal(
    ThreatVector.Unknown,
    0.5,
    'mod',
    'no evidence',
  );
  const keys: string[] = Object.keys(s.evidence);
  assertExact('evidence_default_empty_size', keys.length, 0);
  console.log('PASS: evidence_default_empty');
})();

// ---------------------------------------------------------------------------
// evidence is defensively copied — mutating source must not affect signal
// ---------------------------------------------------------------------------

((): void => {
  const src: Record<string, string> = { ip: '10.0.0.1', port: '443' } as Record<string, string>;
  const s: AnomalySignal = createAnomalySignal(
    ThreatVector.NetworkPivot,
    0.9,
    'net',
    'pivot',
    src,
  );
  // Snapshot captured at construction
  assertExact('evidence_size_initial',  Object.keys(s.evidence).length, 2);
  assertEqualsStr('evidence_ip_initial',   s.evidence['ip'],   '10.0.0.1');
  assertEqualsStr('evidence_port_initial', s.evidence['port'], '443');

  // Mutate the source — signal must remain unchanged
  src['new']  = 'value';
  delete src['ip'];
  assertExact('evidence_size_after_mutation',  Object.keys(s.evidence).length, 2);
  assertEqualsStr('evidence_ip_after_mutation', s.evidence['ip'], '10.0.0.1');
  assertFalse('evidence_no_new_key', 'new' in s.evidence);
  console.log('PASS: evidence_defensive_copy');
})();

// ---------------------------------------------------------------------------
// Preserves vector, affectedModule, description
// ---------------------------------------------------------------------------

((): void => {
  const s: AnomalySignal = createAnomalySignal(
    ThreatVector.PrivilegeEscalation,
    0.5,
    'auth',
    'sudo without challenge',
  );
  assertExact('preserve_vector',         s.vector,         ThreatVector.PrivilegeEscalation);
  assertEqualsStr('preserve_module',     s.affectedModule, 'auth');
  assertEqualsStr('preserve_description', s.description,    'sudo without challenge');
  console.log('PASS: preserve_fields');
})();

console.log('AnomalySignalTest: ALL PASS');
