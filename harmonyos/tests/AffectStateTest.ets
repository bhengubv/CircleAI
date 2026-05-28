// AffectStateTest.ets — Cross-language AffectState math verification (ArkTS)
// All test vectors from fixtures/affect_state.json.
// Each computed value must match within epsilon 1e-5.
// Uses if/throw assertion pattern (no external test framework).

import { AffectState } from '../src/main/ets/memory/index';

// ---------------------------------------------------------------------------
// Assertion helper
// ---------------------------------------------------------------------------

function assertApprox(label: string, actual: number, expected: number, tol: number = 1e-5): void {
  const diff: number = Math.abs(actual - expected);
  if (diff > tol) {
    throw new Error(`FAIL ${label}: expected ${expected}, got ${actual}, diff=${diff} (tol=${tol})`);
  }
}

function assertExact(label: string, actual: number, expected: number): void {
  if (actual !== expected) {
    throw new Error(`FAIL ${label}: expected exactly ${expected}, got ${actual}`);
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
// Test: default field values
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = new AffectState();
  if (s.userId !== 'default') throw new Error(`FAIL default_userId: expected 'default', got '${s.userId}'`);
  assertExact('default_curiosity',   s.curiosity,   0.5);
  assertExact('default_engagement',  s.engagement,  0.5);
  assertExact('default_uncertainty', s.uncertainty, 0.2);
  assertExact('default_rapport',     s.rapport,     0.0);
  assertExact('default_energy',      s.energy,      0.5);
  console.log('PASS: default_state');
})();

// ---------------------------------------------------------------------------
// Vector: positive_signal_once
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.5, 0.5, 0.2, 0.0, 0.5);
  s.applyPositiveSignal();
  assertApprox('pos1_curiosity',   s.curiosity,   0.5);
  assertApprox('pos1_engagement',  s.engagement,  0.52);
  assertApprox('pos1_uncertainty', s.uncertainty, 0.18);
  assertApprox('pos1_rapport',     s.rapport,     0.01);
  assertApprox('pos1_energy',      s.energy,      0.5);
  console.log('PASS: positive_signal_once');
})();

// ---------------------------------------------------------------------------
// Vector: positive_signal_twice
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.5, 0.5, 0.2, 0.0, 0.5);
  s.applyPositiveSignal();
  s.applyPositiveSignal();
  assertApprox('pos2_curiosity',   s.curiosity,   0.5);
  assertApprox('pos2_engagement',  s.engagement,  0.54);
  assertApprox('pos2_uncertainty', s.uncertainty, 0.16);
  assertApprox('pos2_rapport',     s.rapport,     0.02);
  assertApprox('pos2_energy',      s.energy,      0.5);
  console.log('PASS: positive_signal_twice');
})();

// ---------------------------------------------------------------------------
// Vector: negative_signal_once
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.5, 0.5, 0.2, 0.0, 0.5);
  s.applyNegativeSignal();
  assertApprox('neg1_curiosity',   s.curiosity,   0.5);
  assertApprox('neg1_engagement',  s.engagement,  0.47);
  assertApprox('neg1_uncertainty', s.uncertainty, 0.23);
  assertApprox('neg1_rapport',     s.rapport,     0.0);
  assertApprox('neg1_energy',      s.energy,      0.5);
  console.log('PASS: negative_signal_once');
})();

// ---------------------------------------------------------------------------
// Vector: negative_signal_twice
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.5, 0.5, 0.2, 0.0, 0.5);
  s.applyNegativeSignal();
  s.applyNegativeSignal();
  assertApprox('neg2_curiosity',   s.curiosity,   0.5);
  assertApprox('neg2_engagement',  s.engagement,  0.44);
  assertApprox('neg2_uncertainty', s.uncertainty, 0.26);
  assertApprox('neg2_rapport',     s.rapport,     0.0);
  assertApprox('neg2_energy',      s.energy,      0.5);
  console.log('PASS: negative_signal_twice');
})();

// ---------------------------------------------------------------------------
// Vector: positive_then_negative
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.5, 0.5, 0.2, 0.0, 0.5);
  s.applyPositiveSignal();
  s.applyNegativeSignal();
  assertApprox('posneg_curiosity',   s.curiosity,   0.5);
  assertApprox('posneg_engagement',  s.engagement,  0.49);
  assertApprox('posneg_uncertainty', s.uncertainty, 0.21);
  assertApprox('posneg_rapport',     s.rapport,     0.01);
  assertApprox('posneg_energy',      s.energy,      0.5);
  console.log('PASS: positive_then_negative');
})();

// ---------------------------------------------------------------------------
// Vector: negative_then_positive
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.5, 0.5, 0.2, 0.0, 0.5);
  s.applyNegativeSignal();
  s.applyPositiveSignal();
  assertApprox('negpos_curiosity',   s.curiosity,   0.5);
  assertApprox('negpos_engagement',  s.engagement,  0.49);
  assertApprox('negpos_uncertainty', s.uncertainty, 0.21);
  assertApprox('negpos_rapport',     s.rapport,     0.01);
  assertApprox('negpos_energy',      s.energy,      0.5);
  console.log('PASS: negative_then_positive');
})();

// ---------------------------------------------------------------------------
// Vector: idle_decay_1h
// decay = min(0.3, 1*0.02) = 0.02
// engagement = lerp(0.8, 0.5, 0.02) = 0.8 + (0.5-0.8)*0.02 = 0.8 - 0.006 = 0.794
// energy     = lerp(0.7, 0.5, 0.02) = 0.7 + (0.5-0.7)*0.02 = 0.7 - 0.004 = 0.696
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.5, 0.8, 0.2, 0.4, 0.7);
  s.applyIdleDecay(1.0);
  assertApprox('decay1h_curiosity',   s.curiosity,   0.5);
  assertApprox('decay1h_engagement',  s.engagement,  0.794);
  assertApprox('decay1h_uncertainty', s.uncertainty, 0.2);
  assertApprox('decay1h_rapport',     s.rapport,     0.4);
  assertApprox('decay1h_energy',      s.energy,      0.696);
  console.log('PASS: idle_decay_1h');
})();

// ---------------------------------------------------------------------------
// Vector: idle_decay_8h
// decay = min(0.3, 8*0.02) = 0.16
// engagement = lerp(0.8, 0.5, 0.16) = 0.8 + (0.5-0.8)*0.16 = 0.8 - 0.048 = 0.752
// energy     = lerp(0.7, 0.5, 0.16) = 0.7 + (0.5-0.7)*0.16 = 0.7 - 0.032 = 0.668
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.5, 0.8, 0.2, 0.4, 0.7);
  s.applyIdleDecay(8.0);
  assertApprox('decay8h_curiosity',   s.curiosity,   0.5);
  assertApprox('decay8h_engagement',  s.engagement,  0.752);
  assertApprox('decay8h_uncertainty', s.uncertainty, 0.2);
  assertApprox('decay8h_rapport',     s.rapport,     0.4);
  assertApprox('decay8h_energy',      s.energy,      0.668);
  console.log('PASS: idle_decay_8h');
})();

// ---------------------------------------------------------------------------
// Vector: idle_decay_24h
// decay = min(0.3, 24*0.02) = min(0.3, 0.48) = 0.3 (capped)
// engagement = lerp(0.8, 0.5, 0.3) = 0.8 + (0.5-0.8)*0.3 = 0.8 - 0.09 = 0.71
// energy     = lerp(0.7, 0.5, 0.3) = 0.7 + (0.5-0.7)*0.3 = 0.7 - 0.06 = 0.64
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.5, 0.8, 0.2, 0.4, 0.7);
  s.applyIdleDecay(24.0);
  assertApprox('decay24h_curiosity',   s.curiosity,   0.5);
  assertApprox('decay24h_engagement',  s.engagement,  0.71);
  assertApprox('decay24h_uncertainty', s.uncertainty, 0.2);
  assertApprox('decay24h_rapport',     s.rapport,     0.4);
  assertApprox('decay24h_energy',      s.energy,      0.64);
  console.log('PASS: idle_decay_24h');
})();

// ---------------------------------------------------------------------------
// Vector: clamp_max_positive
// From near-max: engagement clamped to 1.0, rapport clamped to 1.0, uncertainty to 0.0
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.5, 0.99, 0.01, 0.99, 0.5);
  s.applyPositiveSignal();
  assertApprox('clampmax_curiosity',   s.curiosity,   0.5);
  assertApprox('clampmax_engagement',  s.engagement,  1.0);
  assertApprox('clampmax_uncertainty', s.uncertainty, 0.0);
  assertApprox('clampmax_rapport',     s.rapport,     1.0);
  assertApprox('clampmax_energy',      s.energy,      0.5);
  console.log('PASS: clamp_max_positive');
})();

// ---------------------------------------------------------------------------
// Vector: clamp_min_negative
// From near-zero: engagement clamped to 0.0, uncertainty clamped to 1.0
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.5, 0.01, 0.98, 0.0, 0.5);
  s.applyNegativeSignal();
  assertApprox('clampmin_curiosity',   s.curiosity,   0.5);
  assertApprox('clampmin_engagement',  s.engagement,  0.0);
  assertApprox('clampmin_uncertainty', s.uncertainty, 1.0);
  assertApprox('clampmin_rapport',     s.rapport,     0.0);
  assertApprox('clampmin_energy',      s.energy,      0.5);
  console.log('PASS: clamp_min_negative');
})();

// ---------------------------------------------------------------------------
// Vector: idle_decay_neutral_no_change
// Engagement and energy already at 0.5 — lerp to 0.5 with any t → stays 0.5
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = makeState(0.5, 0.5, 0.2, 0.0, 0.5);
  s.applyIdleDecay(8.0);
  assertApprox('neutral_curiosity',   s.curiosity,   0.5);
  assertApprox('neutral_engagement',  s.engagement,  0.5);
  assertApprox('neutral_uncertainty', s.uncertainty, 0.2);
  assertApprox('neutral_rapport',     s.rapport,     0.0);
  assertApprox('neutral_energy',      s.energy,      0.5);
  console.log('PASS: idle_decay_neutral_no_change');
})();

// ---------------------------------------------------------------------------
// toSystemPromptHint tests
// ---------------------------------------------------------------------------

((): void => {
  const s: AffectState = new AffectState();
  const hint: string = s.toSystemPromptHint();
  if (hint !== '') throw new Error(`FAIL hint_neutral: expected '', got '${hint}'`);
  console.log('PASS: hint_neutral_empty');
})();

((): void => {
  const s: AffectState = new AffectState();
  s.curiosity = 0.8;
  const hint: string = s.toSystemPromptHint();
  if (!hint.includes('[Affect state]'))    throw new Error('FAIL hint_curiosity: missing header');
  if (!hint.includes('deeply curious'))    throw new Error('FAIL hint_curiosity: missing curiosity line');
  if (!hint.endsWith('\n'))                throw new Error('FAIL hint_curiosity: must end with newline');
  console.log('PASS: hint_curiosity');
})();

((): void => {
  const s: AffectState = new AffectState();
  s.engagement = 0.75;
  const hint: string = s.toSystemPromptHint();
  if (!hint.includes('fully engaged'))     throw new Error('FAIL hint_high_engagement: missing line');
  console.log('PASS: hint_high_engagement');
})();

((): void => {
  const s: AffectState = new AffectState();
  s.engagement = 0.2;
  const hint: string = s.toSystemPromptHint();
  if (!hint.includes('brief'))             throw new Error('FAIL hint_low_engagement: missing line');
  console.log('PASS: hint_low_engagement');
})();

((): void => {
  const s: AffectState = new AffectState();
  s.rapport = 0.8;
  const hint: string = s.toSystemPromptHint();
  if (!hint.includes('know this user well')) throw new Error('FAIL hint_rapport: missing line');
  console.log('PASS: hint_rapport');
})();

((): void => {
  const s: AffectState = new AffectState();
  s.curiosity  = 0.8;
  s.engagement = 0.8;
  s.rapport    = 0.8;
  const hint: string = s.toSystemPromptHint();
  const lines: string[] = hint.split('\n').filter((l: string): boolean => l.length > 0);
  // header + 3 hint lines = at least 4
  if (lines.length < 4) throw new Error(`FAIL hint_multi: expected >=4 lines, got ${lines.length}`);
  console.log('PASS: hint_multiple_flags');
})();

console.log('AffectStateTest: ALL PASS');
