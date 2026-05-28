// security.test.ts
//
// Verifies the Circle AI security portable schema:
//   ThreatVector ordinals (0..7) — stable across language ports
//   AnomalySignal factory — id assignment, evidence defensive copy,
//   detectedAt stamping, and the confidence clamp into [0.0, 1.0].

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  ThreatVector,
  createAnomalySignal,
  type AnomalySignal,
} from '../src/security';

// ---------------------------------------------------------------------------
// ThreatVector ordinals
// ---------------------------------------------------------------------------

describe('ThreatVector ordinals', () => {
  it('MemoryAnomaly === 0', () => {
    assert.equal(ThreatVector.MemoryAnomaly, 0);
  });

  it('ControlFlowDrift === 1', () => {
    assert.equal(ThreatVector.ControlFlowDrift, 1);
  });

  it('PrivilegeEscalation === 2', () => {
    assert.equal(ThreatVector.PrivilegeEscalation, 2);
  });

  it('BiometricSpoofAttempt === 3', () => {
    assert.equal(ThreatVector.BiometricSpoofAttempt, 3);
  });

  it('NetworkPivot === 4', () => {
    assert.equal(ThreatVector.NetworkPivot, 4);
  });

  it('StateCorruption === 5', () => {
    assert.equal(ThreatVector.StateCorruption, 5);
  });

  it('AgentPatchRejected === 6', () => {
    assert.equal(ThreatVector.AgentPatchRejected, 6);
  });

  it('Unknown === 7', () => {
    assert.equal(ThreatVector.Unknown, 7);
  });
});

// ---------------------------------------------------------------------------
// AnomalySignal.confidence clamp
// ---------------------------------------------------------------------------

interface ClampVector {
  id:       string;
  input:    number;
  expected: number;
}

const CLAMP_VECTORS: readonly ClampVector[] = [
  { id: 'above_max', input:  1.5, expected: 1.0 },
  { id: 'below_min', input: -0.3, expected: 0.0 },
  { id: 'at_max',    input:  1.0, expected: 1.0 },
  { id: 'at_min',    input:  0.0, expected: 0.0 },
  { id: 'nominal',   input:  0.7, expected: 0.7 },
];

describe('createAnomalySignal confidence clamp', () => {
  for (const v of CLAMP_VECTORS) {
    it(`${v.id}: ${v.input} → ${v.expected}`, () => {
      const sig = createAnomalySignal(
        ThreatVector.MemoryAnomaly,
        v.input,
        'Circle.AI.Test',
        'clamp test',
      );
      assert.equal(sig.confidence, v.expected);
    });
  }
});

// ---------------------------------------------------------------------------
// AnomalySignal factory behaviour
// ---------------------------------------------------------------------------

describe('createAnomalySignal factory', () => {
  it('assigns a UUID v4 string id', () => {
    const sig = createAnomalySignal(
      ThreatVector.MemoryAnomaly,
      0.5,
      'Circle.AI.Test',
      'id-shape test',
    );
    assert.equal(typeof sig.id, 'string');
    // UUID v4: 8-4-4-4-12 hex characters, version "4" at position 14,
    // variant 8/9/a/b at position 19.
    assert.match(
      sig.id,
      /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
      `expected UUID v4 format, got "${sig.id}"`,
    );
  });

  it('two signals get distinct ids', () => {
    const a = createAnomalySignal(ThreatVector.Unknown, 0.0, 'x', 'a');
    const b = createAnomalySignal(ThreatVector.Unknown, 0.0, 'x', 'b');
    assert.notEqual(a.id, b.id);
  });

  it('stamps detectedAt with a Date close to now', () => {
    const before = Date.now();
    const sig    = createAnomalySignal(ThreatVector.Unknown, 0.0, 'x', 'd');
    const after  = Date.now();
    assert.ok(sig.detectedAt instanceof Date);
    const t = sig.detectedAt.getTime();
    assert.ok(t >= before && t <= after, `detectedAt ${t} not within [${before}, ${after}]`);
  });

  it('omitted evidence becomes an empty object', () => {
    const sig = createAnomalySignal(ThreatVector.Unknown, 0.0, 'x', 'no-ev');
    assert.deepEqual(sig.evidence, {});
  });

  it('evidence is defensively copied (factory does not retain reference)', () => {
    const ev: Record<string, string> = { addr: '0xdeadbeef' };
    const sig = createAnomalySignal(
      ThreatVector.MemoryAnomaly,
      0.9,
      'Circle.AI.Companion',
      'memory drift',
      ev,
    );
    // Mutating the original input must not leak into the stored evidence.
    ev.addr = '0x00000000';
    ev.extra = 'leaked';
    assert.equal(sig.evidence['addr'], '0xdeadbeef');
    assert.equal(sig.evidence['extra'], undefined);
  });

  it('preserves vector, affectedModule, and description verbatim', () => {
    const sig: AnomalySignal = createAnomalySignal(
      ThreatVector.PrivilegeEscalation,
      0.5,
      'Circle.AI.Identity',
      'biometric trust elevation rejected',
    );
    assert.equal(sig.vector,         ThreatVector.PrivilegeEscalation);
    assert.equal(sig.affectedModule, 'Circle.AI.Identity');
    assert.equal(sig.description,    'biometric trust elevation rejected');
  });
});
