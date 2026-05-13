// affect_state.test.ts
//
// Cross-language AffectState math verification.
// All 12 test vectors from fixtures/affect_state.json.
// Each computed value must match the fixture within epsilon 1e-6.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'fs';
import * as path from 'path';
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

function checkClose(field: string, got: number, want: number, epsilon: number): void {
  assert.ok(
    Math.abs(got - want) <= epsilon,
    `${field}: got ${got}, want ${want} (diff ${Math.abs(got - want)} > ${epsilon})`,
  );
}

// ---------------------------------------------------------------------------
// Load fixture
// ---------------------------------------------------------------------------

const fixturePath = path.join(__dirname, '..', '..', 'fixtures', 'affect_state.json');
const fixture: AffectFixture = JSON.parse(fs.readFileSync(fixturePath, 'utf-8'));
const EPSILON = fixture.epsilon;

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('AffectState math vectors', () => {
  for (const vector of fixture.vectors) {
    it(`${vector.id} — ${vector.description}`, () => {
      const state = makeState(vector.input);
      applyOperation(state, vector);

      checkClose('curiosity',   state.curiosity,   vector.expected.curiosity,   EPSILON);
      checkClose('engagement',  state.engagement,  vector.expected.engagement,  EPSILON);
      checkClose('uncertainty', state.uncertainty, vector.expected.uncertainty, EPSILON);
      checkClose('rapport',     state.rapport,     vector.expected.rapport,     EPSILON);
      checkClose('energy',      state.energy,      vector.expected.energy,      EPSILON);
    });
  }
});

describe('AffectState toSystemPromptHint()', () => {
  it('returns empty string for neutral state', () => {
    const s = new AffectState();
    assert.equal(s.toSystemPromptHint(), '');
  });

  it('emits curiosity hint when > 0.7', () => {
    const s = new AffectState();
    s.curiosity = 0.8;
    const hint = s.toSystemPromptHint();
    assert.ok((hint as string).includes('[Affect state]'), `expected "[Affect state]" in "${hint}"`);
    assert.ok((hint as string).includes('deeply curious'), `expected "deeply curious" in "${hint}"`);
  });

  it('emits high-engagement hint when engagement > 0.7', () => {
    const s = new AffectState();
    s.engagement = 0.75;
    const hint = s.toSystemPromptHint();
    assert.ok((hint as string).includes('fully engaged'), `expected "fully engaged" in "${hint}"`);
  });

  it('emits low-engagement hint when engagement < 0.3', () => {
    const s = new AffectState();
    s.engagement = 0.2;
    const hint = s.toSystemPromptHint();
    assert.ok((hint as string).includes('brief'), `expected "brief" in "${hint}"`);
  });

  it('emits rapport hint when > 0.7', () => {
    const s = new AffectState();
    s.rapport = 0.8;
    const hint = s.toSystemPromptHint();
    assert.ok((hint as string).includes('warm, familiar tone'), `expected "warm, familiar tone" in "${hint}"`);
  });

  it('hint ends with newline when non-empty', () => {
    const s = new AffectState();
    s.curiosity = 0.9;
    const hint = s.toSystemPromptHint();
    assert.ok(hint.endsWith('\n'), `expected hint to end with newline, got: "${hint}"`);
  });

  it('multiple flags combine into multi-line hint', () => {
    const s = new AffectState();
    s.curiosity  = 0.8;
    s.engagement = 0.8;
    s.rapport    = 0.8;
    const hint = s.toSystemPromptHint();
    const lines = hint.split('\n').filter(l => l.length > 0);
    // header + 3 hints
    assert.ok(lines.length >= 4, `expected at least 4 lines, got ${lines.length}`);
  });
});

describe('AffectState defaults', () => {
  it('default field values match fixture defaultState', () => {
    const s = new AffectState();
    assert.equal(s.userId, 'default');
    assert.equal(s.curiosity, 0.5);
    assert.equal(s.engagement, 0.5);
    assert.equal(s.uncertainty, 0.2);
    assert.equal(s.rapport, 0.0);
    assert.equal(s.energy, 0.5);
  });
});
