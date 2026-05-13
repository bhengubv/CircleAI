// affect_state.test.ts
//
// Cross-language AffectState math verification — HarmonyOS/ArkTS port.
// All 12 test vectors from fixtures/affect_state.json.
// Each computed value must match the fixture within epsilon 1e-6.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { AffectState } from '../src/memory';

// ---------------------------------------------------------------------------
// Fixture types
// ---------------------------------------------------------------------------

interface AffectInput {
  curiosity:   number;
  engagement:  number;
  uncertainty: number;
  rapport:     number;
  energy:      number;
}

interface AffectVector {
  id:             string;
  description:    string;
  input:          AffectInput;
  operation:      string;
  operationParam: { count?: number; hours?: number };
  expected:       AffectInput;
}

interface AffectFixture {
  epsilon:  number;
  vectors:  AffectVector[];
}

// ---------------------------------------------------------------------------
// Helper
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

function applyOperation(state: AffectState, vector: AffectVector): void {
  const count = vector.operationParam.count ?? 1;
  switch (vector.operation) {
    case 'positive_signal':
      for (let i = 0; i < count; i++) state.applyPositiveSignal();
      break;
    case 'negative_signal':
      for (let i = 0; i < count; i++) state.applyNegativeSignal();
      break;
    case 'idle_decay': {
      const hours = vector.operationParam.hours ?? 1;
      state.applyIdleDecay(hours);
      break;
    }
    case 'positive_then_negative':
      state.applyPositiveSignal();
      state.applyNegativeSignal();
      break;
    case 'negative_then_positive':
      state.applyNegativeSignal();
      state.applyPositiveSignal();
      break;
    default:
      throw new Error(`Unknown operation: ${vector.operation}`);
  }
}

// ---------------------------------------------------------------------------
// Load fixture
// ---------------------------------------------------------------------------

const fixturePath = resolve(__dirname, '../../fixtures/affect_state.json');
const fixture: AffectFixture = JSON.parse(readFileSync(fixturePath, 'utf-8'));
const EPSILON = fixture.epsilon;

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('AffectState math vectors', () => {
  for (const vector of fixture.vectors) {
    it(`${vector.id} — ${vector.description}`, () => {
      const state = makeState(vector.input);
      applyOperation(state, vector);

      assert.ok(Math.abs(state.curiosity   - vector.expected.curiosity)   <= EPSILON, `curiosity: ${state.curiosity} ≈ ${vector.expected.curiosity}`);
      assert.ok(Math.abs(state.engagement  - vector.expected.engagement)  <= EPSILON, `engagement: ${state.engagement} ≈ ${vector.expected.engagement}`);
      assert.ok(Math.abs(state.uncertainty - vector.expected.uncertainty) <= EPSILON, `uncertainty: ${state.uncertainty} ≈ ${vector.expected.uncertainty}`);
      assert.ok(Math.abs(state.rapport     - vector.expected.rapport)     <= EPSILON, `rapport: ${state.rapport} ≈ ${vector.expected.rapport}`);
      assert.ok(Math.abs(state.energy      - vector.expected.energy)      <= EPSILON, `energy: ${state.energy} ≈ ${vector.expected.energy}`);
    });
  }
});

describe('AffectState toSystemPromptHint()', () => {
  it('returns empty string for neutral state', () => {
    const s = new AffectState();
    assert.strictEqual(s.toSystemPromptHint(), '');
  });

  it('emits curiosity hint when > 0.7', () => {
    const s = new AffectState();
    s.curiosity = 0.8;
    const hint = s.toSystemPromptHint();
    assert.ok((hint as string).includes('[Affect state]'));
    assert.ok((hint as string).includes('deeply curious'));
  });

  it('emits high-engagement hint when engagement > 0.7', () => {
    const s = new AffectState();
    s.engagement = 0.75;
    const hint = s.toSystemPromptHint();
    assert.ok((hint as string).includes('fully engaged'));
  });

  it('emits low-engagement hint when engagement < 0.3', () => {
    const s = new AffectState();
    s.engagement = 0.2;
    const hint = s.toSystemPromptHint();
    assert.ok((hint as string).includes('brief'));
  });

  it('emits rapport hint when > 0.7', () => {
    const s = new AffectState();
    s.rapport = 0.8;
    const hint = s.toSystemPromptHint();
    assert.ok((hint as string).includes('warm, familiar tone'));
  });

  it('hint ends with newline when non-empty', () => {
    const s = new AffectState();
    s.curiosity = 0.9;
    const hint = s.toSystemPromptHint();
    assert.strictEqual(hint.endsWith('\n'), true);
  });

  it('multiple flags combine into multi-line hint', () => {
    const s = new AffectState();
    s.curiosity  = 0.8;
    s.engagement = 0.8;
    s.rapport    = 0.8;
    const hint = s.toSystemPromptHint();
    const lines = hint.split('\n').filter(l => l.length > 0);
    // header + 3 hints
    assert.ok(lines.length >= 4);
  });
});

describe('AffectState defaults', () => {
  it('default field values match fixture defaultState', () => {
    const s = new AffectState();
    assert.strictEqual(s.userId, 'default');
    assert.strictEqual(s.curiosity, 0.5);
    assert.strictEqual(s.engagement, 0.5);
    assert.strictEqual(s.uncertainty, 0.2);
    assert.strictEqual(s.rapport, 0.0);
    assert.strictEqual(s.energy, 0.5);
  });
});
