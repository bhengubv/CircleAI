// GoalProgressTest.ets — Goal.advanceProgress() math verification (ArkTS)
// All 7 test vectors from fixtures/goal_progress.json.
// Formula: new_progress = clamp(progress + delta, 0.0, 1.0)
// Each result must match fixture exactly (or within 1e-6).
// Uses if/throw assertion pattern (no external test framework).

import { Goal, GoalStatus, GoalPriority, advanceProgress } from '../src/main/ets/memory/index';

// ---------------------------------------------------------------------------
// Assertion helpers
// ---------------------------------------------------------------------------

function assertApprox(label: string, actual: number, expected: number, tol: number = 1e-6): void {
  const diff: number = Math.abs(actual - expected);
  if (diff > tol) {
    throw new Error(`FAIL ${label}: expected ${expected}, got ${actual}, diff=${diff} (tol=${tol})`);
  }
}

// ---------------------------------------------------------------------------
// Helper: build a minimal Goal for testing
// ---------------------------------------------------------------------------

function makeGoal(progress: number): Goal {
  return {
    id:          'test-goal-id',
    userId:      'test-user',
    title:       'Test Goal',
    description: 'A test goal for progress verification',
    status:      GoalStatus.Active,
    priority:    GoalPriority.Normal,
    createdAt:   Date.now(),
    progress:    progress,
  };
}

// ---------------------------------------------------------------------------
// Vector 1: zero_initial — Advance by 0 from 0 — no change
// initial=0.0, delta=0.0, expected=0.0
// ---------------------------------------------------------------------------

((): void => {
  const g: Goal = makeGoal(0.0);
  const result: Goal = advanceProgress(g, 0.0);
  assertApprox('zero_initial', result.progress, 0.0);
  // Original goal must be immutable
  assertApprox('zero_initial_original_unchanged', g.progress, 0.0);
  console.log('PASS: zero_initial');
})();

// ---------------------------------------------------------------------------
// Vector 2: partial_advance — Advance 30% from zero
// initial=0.0, delta=0.3, expected=0.3
// ---------------------------------------------------------------------------

((): void => {
  const g: Goal = makeGoal(0.0);
  const result: Goal = advanceProgress(g, 0.3);
  assertApprox('partial_advance', result.progress, 0.3);
  console.log('PASS: partial_advance');
})();

// ---------------------------------------------------------------------------
// Vector 3: clamp_max — Advance past 1.0 — clamped to 1.0
// initial=0.9, delta=0.5, expected=1.0
// ---------------------------------------------------------------------------

((): void => {
  const g: Goal = makeGoal(0.9);
  const result: Goal = advanceProgress(g, 0.5);
  assertApprox('clamp_max', result.progress, 1.0);
  console.log('PASS: clamp_max');
})();

// ---------------------------------------------------------------------------
// Vector 4: clamp_min — Negative delta past 0.0 — clamped to 0.0
// initial=0.1, delta=-0.5, expected=0.0
// ---------------------------------------------------------------------------

((): void => {
  const g: Goal = makeGoal(0.1);
  const result: Goal = advanceProgress(g, -0.5);
  assertApprox('clamp_min', result.progress, 0.0);
  console.log('PASS: clamp_min');
})();

// ---------------------------------------------------------------------------
// Vector 5: zero_delta — Zero delta mid-progress — unchanged
// initial=0.5, delta=0.0, expected=0.5
// ---------------------------------------------------------------------------

((): void => {
  const g: Goal = makeGoal(0.5);
  const result: Goal = advanceProgress(g, 0.0);
  assertApprox('zero_delta', result.progress, 0.5);
  console.log('PASS: zero_delta');
})();

// ---------------------------------------------------------------------------
// Vector 6: advance_to_full — Exact advance to 1.0 — not clamped
// initial=0.75, delta=0.25, expected=1.0
// ---------------------------------------------------------------------------

((): void => {
  const g: Goal = makeGoal(0.75);
  const result: Goal = advanceProgress(g, 0.25);
  assertApprox('advance_to_full', result.progress, 1.0);
  console.log('PASS: advance_to_full');
})();

// ---------------------------------------------------------------------------
// Vector 7: negative_delta — Regression: subtract without hitting floor
// initial=0.6, delta=-0.2, expected=0.4
// ---------------------------------------------------------------------------

((): void => {
  const g: Goal = makeGoal(0.6);
  const result: Goal = advanceProgress(g, -0.2);
  assertApprox('negative_delta', result.progress, 0.4);
  console.log('PASS: negative_delta');
})();

// ---------------------------------------------------------------------------
// Immutability: original goal must not be mutated
// ---------------------------------------------------------------------------

((): void => {
  const g: Goal = makeGoal(0.5);
  const originalProgress: number = g.progress;
  advanceProgress(g, 0.3);
  assertApprox('immutability_original', g.progress, originalProgress);
  console.log('PASS: original_goal_immutable');
})();

// ---------------------------------------------------------------------------
// Return value carries all other Goal fields unchanged
// ---------------------------------------------------------------------------

((): void => {
  const g: Goal = makeGoal(0.4);
  const result: Goal = advanceProgress(g, 0.1);
  if (result.id !== g.id)               throw new Error('FAIL carry_id');
  if (result.userId !== g.userId)       throw new Error('FAIL carry_userId');
  if (result.title !== g.title)         throw new Error('FAIL carry_title');
  if (result.status !== g.status)       throw new Error('FAIL carry_status');
  if (result.priority !== g.priority)   throw new Error('FAIL carry_priority');
  if (result.createdAt !== g.createdAt) throw new Error('FAIL carry_createdAt');
  assertApprox('carry_fields_progress', result.progress, 0.5);
  console.log('PASS: carry_all_fields');
})();

// ---------------------------------------------------------------------------
// GoalStatus and GoalPriority enum value checks
// ---------------------------------------------------------------------------

((): void => {
  if (GoalStatus.Active    !== 0) throw new Error(`FAIL GoalStatus.Active: expected 0, got ${GoalStatus.Active}`);
  if (GoalStatus.Completed !== 1) throw new Error(`FAIL GoalStatus.Completed: expected 1, got ${GoalStatus.Completed}`);
  if (GoalStatus.Abandoned !== 2) throw new Error(`FAIL GoalStatus.Abandoned: expected 2, got ${GoalStatus.Abandoned}`);
  console.log('PASS: GoalStatus_enum_values');
})();

((): void => {
  if (GoalPriority.Low    !== 0) throw new Error(`FAIL GoalPriority.Low: expected 0, got ${GoalPriority.Low}`);
  if (GoalPriority.Normal !== 1) throw new Error(`FAIL GoalPriority.Normal: expected 1, got ${GoalPriority.Normal}`);
  if (GoalPriority.High   !== 2) throw new Error(`FAIL GoalPriority.High: expected 2, got ${GoalPriority.High}`);
  console.log('PASS: GoalPriority_enum_values');
})();

console.log('GoalProgressTest: ALL PASS');
