// goal_progress.test.ts
//
// Cross-language Goal.advanceProgress() verification.
// All test vectors from fixtures/goal_progress.json.
// Formula: new_progress = clamp(progress + delta, 0.0, 1.0)

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'fs';
import * as path from 'path';
import { Goal, GoalStatus, GoalPriority } from '../src/memory';

// ---------------------------------------------------------------------------
// Fixture types
// ---------------------------------------------------------------------------

interface GoalProgressVector {
  id:               string;
  description:      string;
  initial_progress: number;
  delta:            number;
  expected_progress: number;
}

interface GoalProgressFixture {
  vectors: GoalProgressVector[];
}

// ---------------------------------------------------------------------------
// Load fixture
// ---------------------------------------------------------------------------

const fixturePath = path.join(__dirname, '..', '..', 'fixtures', 'goal_progress.json');
const fixture: GoalProgressFixture = JSON.parse(fs.readFileSync(fixturePath, 'utf-8'));

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function makeGoal(progress: number): Goal {
  const g = new Goal();
  g.id          = 'test-goal-001';
  g.userId      = 'test-user';
  g.title       = 'Test Goal';
  g.description = 'A goal for testing advance progress';
  g.status      = GoalStatus.Active;
  g.priority    = GoalPriority.Normal;
  g.createdUtc  = new Date('2026-01-01T00:00:00Z');
  g.progress    = progress;
  return g;
}

function checkClose(got: number, want: number, epsilon: number, label: string): void {
  assert.ok(
    Math.abs(got - want) <= epsilon,
    `${label}: got ${got}, want ${want} (diff ${Math.abs(got - want)} > ${epsilon})`,
  );
}

const EPSILON = 1e-5;

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('Goal.advanceProgress fixture vectors', () => {
  for (const vec of fixture.vectors) {
    it(`${vec.id} — ${vec.description}`, () => {
      const original = makeGoal(vec.initial_progress);
      const updated  = original.advanceProgress(vec.delta);

      checkClose(updated.progress, vec.expected_progress, EPSILON, 'progress');

      // advanceProgress must return a NEW Goal, not mutate the original.
      assert.equal(
        original.progress,
        vec.initial_progress,
        `advanceProgress must not mutate original goal: expected ${vec.initial_progress}, got ${original.progress}`,
      );
    });
  }
});

describe('Goal.advanceProgress immutability', () => {
  it('returns a new Goal instance', () => {
    const g1 = makeGoal(0.5);
    const g2 = g1.advanceProgress(0.1);
    assert.ok(g1 !== g2, 'advanceProgress must return a different object');
  });

  it('preserves all other fields on the returned Goal', () => {
    const g1   = makeGoal(0.3);
    const g2   = g1.advanceProgress(0.2);
    assert.equal(g2.id,          g1.id);
    assert.equal(g2.userId,      g1.userId);
    assert.equal(g2.title,       g1.title);
    assert.equal(g2.description, g1.description);
    assert.equal(g2.status,      g1.status);
    assert.equal(g2.priority,    g1.priority);
  });
});

describe('Goal.advanceProgress clamping', () => {
  it('result is always >= 0', () => {
    const g = makeGoal(0.0);
    assert.ok(g.advanceProgress(-100).progress >= 0, 'progress should not go below 0');
  });

  it('result is always <= 1', () => {
    const g = makeGoal(1.0);
    assert.ok(g.advanceProgress(100).progress <= 1, 'progress should not exceed 1');
  });
});

describe('Goal default values', () => {
  it('default progress is 0.0', () => {
    const g = new Goal();
    assert.equal(g.progress, 0.0);
  });

  it('default status is Active', () => {
    const g = new Goal();
    assert.equal(g.status, GoalStatus.Active);
  });

  it('default priority is Normal', () => {
    const g = new Goal();
    assert.equal(g.priority, GoalPriority.Normal);
  });
});
