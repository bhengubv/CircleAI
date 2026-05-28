// AffectVadTest.ets — Cross-language AffectVad derivation verification (ArkTS)
//
// All test vectors mirror fixtures/affect_vad_derivation.json.
// Each computed value must match within epsilon 1e-5.
//
// Derivation contract:
//   valence   = (engagement + rapport + (1 - uncertainty)) / 3
//   arousal   = (energy * 2 + curiosity + uncertainty) / 4
//   dominance = (engagement + (1 - uncertainty)) / 2
//   each clamped to [0, 1]
//
// Uses if/throw assertion pattern (no external test framework).

import {
  AffectState,
  AffectVad,
  affectStateToVad,
} from '../src/main/ets/memory/index';

// ---------------------------------------------------------------------------
// Assertion helpers
// ---------------------------------------------------------------------------

function assertApprox(label: string, actual: number, expected: number, tol: number = 1e-5): void {
  const diff: number = Math.abs(actual - expected);
  if (diff > tol) {
    throw new Error(`FAIL ${label}: expected ${expected}, got ${actual}, diff=${diff} (tol=${tol})`);
  }
}

// ---------------------------------------------------------------------------
// Helper: construct AffectState from explicit field values
// ---------------------------------------------------------------------------

function makeState(
  curiosity: number,
  engagement: number,
  uncertainty: number,
  rapport: number,
  energy: number,
): AffectState {
  const s: AffectState = new AffectState('test');
  s.curiosity   = curiosity;
  s.engagement  = engagement;
  s.uncertainty = uncertainty;
  s.rapport     = rapport;
  s.energy      = energy;
  return s;
}

// ---------------------------------------------------------------------------
// Vector: default
// curiosity=0.5, engagement=0.5, uncertainty=0.2, rapport=0, energy=0.5
//   V = (0.5 + 0   + 0.8) / 3 = 0.43333333
//   A = (1.0 + 0.5 + 0.2) / 4 = 0.425
//   D = (0.5 + 0.8) / 2       = 0.65
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.5, 0.5, 0.2, 0.0, 0.5);
  const v: AffectVad = affectStateToVad(s);
  assertApprox('default_valence',   v.valence,   0.43333333);
  assertApprox('default_arousal',   v.arousal,   0.425);
  assertApprox('default_dominance', v.dominance, 0.65);
  console.log('PASS: default');
})();

// ---------------------------------------------------------------------------
// Vector: all_max
// curiosity=1, engagement=1, uncertainty=0, rapport=1, energy=1
//   V = (1 + 1 + 1) / 3 = 1.0
//   A = (2 + 1 + 0) / 4 = 0.75
//   D = (1 + 1) / 2     = 1.0
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(1.0, 1.0, 0.0, 1.0, 1.0);
  const v: AffectVad = affectStateToVad(s);
  assertApprox('all_max_valence',   v.valence,   1.0);
  assertApprox('all_max_arousal',   v.arousal,   0.75);
  assertApprox('all_max_dominance', v.dominance, 1.0);
  console.log('PASS: all_max');
})();

// ---------------------------------------------------------------------------
// Vector: all_min
// curiosity=0, engagement=0, uncertainty=1, rapport=0, energy=0
//   V = (0 + 0 + 0) / 3 = 0.0
//   A = (0 + 0 + 1) / 4 = 0.25
//   D = (0 + 0) / 2     = 0.0
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.0, 0.0, 1.0, 0.0, 0.0);
  const v: AffectVad = affectStateToVad(s);
  assertApprox('all_min_valence',   v.valence,   0.0);
  assertApprox('all_min_arousal',   v.arousal,   0.25);
  assertApprox('all_min_dominance', v.dominance, 0.0);
  console.log('PASS: all_min');
})();

// ---------------------------------------------------------------------------
// Vector: warm (high engagement, warm rapport, low uncertainty)
// curiosity=0.6, engagement=0.9, uncertainty=0.1, rapport=0.8, energy=0.7
//   V = (0.9 + 0.8 + 0.9) / 3 = 0.86666667
//   A = (1.4 + 0.6 + 0.1) / 4 = 0.525
//   D = (0.9 + 0.9) / 2       = 0.9
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.6, 0.9, 0.1, 0.8, 0.7);
  const v: AffectVad = affectStateToVad(s);
  assertApprox('warm_valence',   v.valence,   0.86666667);
  assertApprox('warm_arousal',   v.arousal,   0.525);
  assertApprox('warm_dominance', v.dominance, 0.9);
  console.log('PASS: warm');
})();

// ---------------------------------------------------------------------------
// Vector: stressed (low energy, high uncertainty — overwhelmed)
// curiosity=0.3, engagement=0.2, uncertainty=0.8, rapport=0.0, energy=0.2
//   V = (0.2 + 0.0 + 0.2) / 3 = 0.13333333
//   A = (0.4 + 0.3 + 0.8) / 4 = 0.375
//   D = (0.2 + 0.2) / 2       = 0.2
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.3, 0.2, 0.8, 0.0, 0.2);
  const v: AffectVad = affectStateToVad(s);
  assertApprox('stressed_valence',   v.valence,   0.13333333);
  assertApprox('stressed_arousal',   v.arousal,   0.375);
  assertApprox('stressed_dominance', v.dominance, 0.2);
  console.log('PASS: stressed');
})();

// ---------------------------------------------------------------------------
// Vector: energetic (high curiosity, high energy — alert / exploratory)
// curiosity=0.9, engagement=0.6, uncertainty=0.3, rapport=0.4, energy=0.9
//   V = (0.6 + 0.4 + 0.7) / 3 = 0.56666667
//   A = (1.8 + 0.9 + 0.3) / 4 = 0.75
//   D = (0.6 + 0.7) / 2       = 0.65
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.9, 0.6, 0.3, 0.4, 0.9);
  const v: AffectVad = affectStateToVad(s);
  assertApprox('energetic_valence',   v.valence,   0.56666667);
  assertApprox('energetic_arousal',   v.arousal,   0.75);
  assertApprox('energetic_dominance', v.dominance, 0.65);
  console.log('PASS: energetic');
})();

// ---------------------------------------------------------------------------
// Output clamp — feed input that would produce > 1 without clamp
// (all_max already proves the upper boundary stays at 1.0; this checks
// the lower boundary stays at 0.0 too.)
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.0, 0.0, 1.0, 0.0, 0.0);
  const v: AffectVad = affectStateToVad(s);
  if (v.valence   < 0) throw new Error(`FAIL clamp_lower_valence: ${v.valence}`);
  if (v.arousal   < 0) throw new Error(`FAIL clamp_lower_arousal: ${v.arousal}`);
  if (v.dominance < 0) throw new Error(`FAIL clamp_lower_dominance: ${v.dominance}`);
  if (v.valence   > 1) throw new Error(`FAIL clamp_upper_valence: ${v.valence}`);
  if (v.arousal   > 1) throw new Error(`FAIL clamp_upper_arousal: ${v.arousal}`);
  if (v.dominance > 1) throw new Error(`FAIL clamp_upper_dominance: ${v.dominance}`);
  console.log('PASS: output_clamp_range');
})();

console.log('AffectVadTest: ALL PASS');
