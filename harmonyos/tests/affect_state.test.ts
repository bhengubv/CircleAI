// affect_state.test.ts
//
// Cross-language AffectState math verification — HarmonyOS/ArkTS port.
// All 12 test vectors from fixtures/affect_state.json.
// Each computed value must match the fixture within epsilon 1e-6.

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

// ---------------------------------------------------------------------------
// Load fixture
// ---------------------------------------------------------------------------

const fixturePath = path.resolve(__dirname, '../../fixtures/affect_state.json');
const fixture: AffectFixture = JSON.parse(fs.readFileSync(fixturePath, 'utf-8'));
const EPSILON = fixture.epsilon;

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('AffectState math vectors', () => {
  test.each(fixture.vectors)(
    '$id — $description',
    (vector) => {
      const state = makeState(vector.input);
      applyOperation(state, vector);

      expect(state.curiosity).toBeCloseTo(vector.expected.curiosity,    -Math.log10(EPSILON));
      expect(state.engagement).toBeCloseTo(vector.expected.engagement,  -Math.log10(EPSILON));
      expect(state.uncertainty).toBeCloseTo(vector.expected.uncertainty,-Math.log10(EPSILON));
      expect(state.rapport).toBeCloseTo(vector.expected.rapport,        -Math.log10(EPSILON));
      expect(state.energy).toBeCloseTo(vector.expected.energy,          -Math.log10(EPSILON));

      // Strict epsilon check to satisfy the "within 1e-6" contract
      expect(Math.abs(state.curiosity   - vector.expected.curiosity)).toBeLessThanOrEqual(EPSILON);
      expect(Math.abs(state.engagement  - vector.expected.engagement)).toBeLessThanOrEqual(EPSILON);
      expect(Math.abs(state.uncertainty - vector.expected.uncertainty)).toBeLessThanOrEqual(EPSILON);
      expect(Math.abs(state.rapport     - vector.expected.rapport)).toBeLessThanOrEqual(EPSILON);
      expect(Math.abs(state.energy      - vector.expected.energy)).toBeLessThanOrEqual(EPSILON);
    },
  );
});

describe('AffectState toSystemPromptHint()', () => {
  test('returns empty string for neutral state', () => {
    const s = new AffectState();
    expect(s.toSystemPromptHint()).toBe('');
  });

  test('emits curiosity hint when > 0.7', () => {
    const s = new AffectState();
    s.curiosity = 0.8;
    const hint = s.toSystemPromptHint();
    expect(hint).toContain('[Affect state]');
    expect(hint).toContain('deeply curious');
  });

  test('emits high-engagement hint when engagement > 0.7', () => {
    const s = new AffectState();
    s.engagement = 0.75;
    const hint = s.toSystemPromptHint();
    expect(hint).toContain('fully engaged');
  });

  test('emits low-engagement hint when engagement < 0.3', () => {
    const s = new AffectState();
    s.engagement = 0.2;
    const hint = s.toSystemPromptHint();
    expect(hint).toContain('brief');
  });

  test('emits rapport hint when > 0.7', () => {
    const s = new AffectState();
    s.rapport = 0.8;
    const hint = s.toSystemPromptHint();
    expect(hint).toContain('warm, familiar tone');
  });

  test('hint ends with newline when non-empty', () => {
    const s = new AffectState();
    s.curiosity = 0.9;
    const hint = s.toSystemPromptHint();
    expect(hint.endsWith('\n')).toBe(true);
  });

  test('multiple flags combine into multi-line hint', () => {
    const s = new AffectState();
    s.curiosity  = 0.8;
    s.engagement = 0.8;
    s.rapport    = 0.8;
    const hint = s.toSystemPromptHint();
    const lines = hint.split('\n').filter(l => l.length > 0);
    // header + 3 hints
    expect(lines.length).toBeGreaterThanOrEqual(4);
  });
});

describe('AffectState defaults', () => {
  test('default field values match fixture defaultState', () => {
    const s = new AffectState();
    expect(s.userId).toBe('default');
    expect(s.curiosity).toBe(0.5);
    expect(s.engagement).toBe(0.5);
    expect(s.uncertainty).toBe(0.2);
    expect(s.rapport).toBe(0.0);
    expect(s.energy).toBe(0.5);
  });
});
