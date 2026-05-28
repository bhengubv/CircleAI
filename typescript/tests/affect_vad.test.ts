// affect_vad.test.ts
//
// Cross-language AffectVad derivation verification.
// Math must be byte-identical to the C# / Rust / Kotlin / Python ports.
//
// Derivation:
//   Valence   = (engagement + rapport + (1 - uncertainty)) / 3
//   Arousal   = (energy * 2 + curiosity + uncertainty) / 4
//   Dominance = (engagement + (1 - uncertainty)) / 2
// Outputs are clamped to [0.0, 1.0]. Compare with epsilon 1e-5.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { AffectState, affectStateToVad, type AffectVad } from '../src/memory';

// ---------------------------------------------------------------------------
// Vector types
// ---------------------------------------------------------------------------

interface AffectInput {
  curiosity:   number;
  engagement:  number;
  uncertainty: number;
  rapport:     number;
  energy:      number;
}

interface VadExpected {
  valence:   number;
  arousal:   number;
  dominance: number;
}

interface AffectVadVector {
  id:          string;
  description: string;
  input:       AffectInput;
  expected:    VadExpected;
}

// ---------------------------------------------------------------------------
// Vectors — mirror fixtures/affect_vad_derivation.json
// ---------------------------------------------------------------------------

const VECTORS: readonly AffectVadVector[] = [
  {
    id:          'default',
    description: 'Default AffectState — curiosity=0.5, engagement=0.5, uncertainty=0.2, rapport=0, energy=0.5',
    input:    { curiosity: 0.5, engagement: 0.5, uncertainty: 0.2, rapport: 0.0, energy: 0.5 },
    expected: { valence:   0.43333333, arousal: 0.425, dominance: 0.65 },
  },
  {
    id:          'all_max',
    description: 'All positive dimensions max, uncertainty zero.',
    input:    { curiosity: 1.0, engagement: 1.0, uncertainty: 0.0, rapport: 1.0, energy: 1.0 },
    expected: { valence:   1.0,        arousal: 0.75,  dominance: 1.0  },
  },
  {
    id:          'all_min_uncertain',
    description: 'All positive dimensions zero, uncertainty maxed.',
    input:    { curiosity: 0.0, engagement: 0.0, uncertainty: 1.0, rapport: 0.0, energy: 0.0 },
    expected: { valence:   0.0,        arousal: 0.25,  dominance: 0.0  },
  },
  {
    id:          'engagement_warm',
    description: 'Engaged + warm rapport + low uncertainty.',
    input:    { curiosity: 0.6, engagement: 0.9, uncertainty: 0.1, rapport: 0.8, energy: 0.7 },
    expected: { valence:   0.86666667, arousal: 0.525, dominance: 0.9  },
  },
  {
    id:          'stressed',
    description: 'High uncertainty, low rapport, low energy — overwhelmed state.',
    input:    { curiosity: 0.3, engagement: 0.2, uncertainty: 0.8, rapport: 0.0, energy: 0.2 },
    expected: { valence:   0.13333333, arousal: 0.375, dominance: 0.2  },
  },
  {
    id:          'energetic',
    description: 'High curiosity + high energy — alert / exploratory.',
    input:    { curiosity: 0.9, engagement: 0.6, uncertainty: 0.3, rapport: 0.4, energy: 0.9 },
    expected: { valence:   0.56666667, arousal: 0.75,  dominance: 0.65 },
  },
];

const EPSILON = 1e-5;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function makeState(input: AffectInput): AffectState {
  const s = new AffectState();
  s.curiosity   = input.curiosity;
  s.engagement  = input.engagement;
  s.uncertainty = input.uncertainty;
  s.rapport     = input.rapport;
  s.energy      = input.energy;
  return s;
}

function checkClose(field: string, got: number, want: number, epsilon: number): void {
  assert.ok(
    Math.abs(got - want) <= epsilon,
    `${field}: got ${got}, want ${want} (diff ${Math.abs(got - want)} > ${epsilon})`,
  );
}

// ---------------------------------------------------------------------------
// Tests — fixture-aligned derivation vectors
// ---------------------------------------------------------------------------

describe('affectStateToVad derivation vectors', () => {
  for (const v of VECTORS) {
    it(`${v.id} — ${v.description}`, () => {
      const state = makeState(v.input);
      const vad   = affectStateToVad(state);

      checkClose('valence',   vad.valence,   v.expected.valence,   EPSILON);
      checkClose('arousal',   vad.arousal,   v.expected.arousal,   EPSILON);
      checkClose('dominance', vad.dominance, v.expected.dominance, EPSILON);
    });
  }
});

// ---------------------------------------------------------------------------
// Output range — every component MUST be in [0, 1] for every fixture vector
// ---------------------------------------------------------------------------

describe('affectStateToVad output range', () => {
  for (const v of VECTORS) {
    it(`${v.id}: all components in [0, 1]`, () => {
      const vad = affectStateToVad(makeState(v.input));
      assert.ok(vad.valence   >= 0 && vad.valence   <= 1, `valence out of range: ${vad.valence}`);
      assert.ok(vad.arousal   >= 0 && vad.arousal   <= 1, `arousal out of range: ${vad.arousal}`);
      assert.ok(vad.dominance >= 0 && vad.dominance <= 1, `dominance out of range: ${vad.dominance}`);
    });
  }
});

// ---------------------------------------------------------------------------
// Purity — must not mutate the source AffectState
// ---------------------------------------------------------------------------

describe('affectStateToVad purity', () => {
  it('does not mutate the source AffectState', () => {
    const state = makeState({
      curiosity:   0.6,
      engagement:  0.9,
      uncertainty: 0.1,
      rapport:     0.8,
      energy:      0.7,
    });
    const before = {
      curiosity:   state.curiosity,
      engagement:  state.engagement,
      uncertainty: state.uncertainty,
      rapport:     state.rapport,
      energy:      state.energy,
    };
    affectStateToVad(state);
    assert.equal(state.curiosity,   before.curiosity);
    assert.equal(state.engagement,  before.engagement);
    assert.equal(state.uncertainty, before.uncertainty);
    assert.equal(state.rapport,     before.rapport);
    assert.equal(state.energy,      before.energy);
  });

  it('returns a fresh AffectVad on each call', () => {
    const state = new AffectState();
    const a: AffectVad = affectStateToVad(state);
    const b: AffectVad = affectStateToVad(state);
    assert.ok(a !== b, 'affectStateToVad must return a fresh object');
    assert.equal(a.valence,   b.valence);
    assert.equal(a.arousal,   b.arousal);
    assert.equal(a.dominance, b.dominance);
  });
});
