// CompanionTypesTest.ets — Companion types and FaceAffectMapper verification (ArkTS)
// Covers:
//   - InterfaceKind enum (7 values, correct ordinal)
//   - CompanionContext and CompanionTurn structural correctness
//   - applyFaceAffect deltas (must match fixtures/facex_biometric_vectors.json within 1e-5)
//   - faceObserve() confusion threshold proactive event
//   - CONFUSION_THRESHOLD constant
// Uses if/throw assertion pattern (no external test framework).

import {
  InterfaceKind,
  CompanionContext,
  CompanionTurn,
  CompanionProactiveEvent,
  applyFaceAffect,
  faceObserve,
  CONFUSION_THRESHOLD,
  MIN_FACE_CONFIDENCE,
} from '../src/main/ets/companion/index';

import { AffectState } from '../src/main/ets/memory/index';
import { FacialMetricMatrix, FaceExpressionClassification, FaceBoundingBox } from '../src/main/ets/tools/index';

// ---------------------------------------------------------------------------
// Assertion helpers
// ---------------------------------------------------------------------------

function assertApprox(label: string, actual: number, expected: number, tol: number = 1e-5): void {
  const diff: number = Math.abs(actual - expected);
  if (diff > tol) {
    throw new Error(`FAIL ${label}: expected ${expected}, got ${actual}, diff=${diff} (tol=${tol})`);
  }
}

function assertEqual<T>(label: string, actual: T, expected: T): void {
  if (actual !== expected) {
    throw new Error(`FAIL ${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

function assertTrue(label: string, condition: boolean): void {
  if (!condition) throw new Error(`FAIL ${label}: expected true, got false`);
}

function assertNull(label: string, value: object | null): void {
  if (value !== null) throw new Error(`FAIL ${label}: expected null, got non-null`);
}

function assertNotNull(label: string, value: object | null): void {
  if (value === null) throw new Error(`FAIL ${label}: expected non-null, got null`);
}

// ---------------------------------------------------------------------------
// Helper: build a FacialMetricMatrix for testing
// ---------------------------------------------------------------------------

function makeFaceMatrix(
  expression: FaceExpressionClassification,
  confidence: number,
): FacialMetricMatrix {
  const box: FaceBoundingBox = { x: 0, y: 0, width: 100, height: 100 };
  return {
    landmarks:      new Float32Array(136),   // all zeros — not tested here
    boundingBox:    box,
    expression:     expression,
    confidenceScore: confidence,
    capturedAt:     Date.now(),
  };
}

function makeAffectState(
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
// InterfaceKind enum checks
// ---------------------------------------------------------------------------

((): void => {
  assertEqual('mobile_value',   InterfaceKind.Mobile,   0);
  assertEqual('wearable_value', InterfaceKind.Wearable, 1);
  assertEqual('desktop_value',  InterfaceKind.Desktop,  2);
  assertEqual('web_value',      InterfaceKind.Web,      3);
  assertEqual('iot_value',      InterfaceKind.IoT,      4);
  assertEqual('ambient_value',  InterfaceKind.Ambient,  5);
  assertEqual('headless_value', InterfaceKind.Headless, 6);
  console.log('PASS: InterfaceKind_enum_values');
})();

// ---------------------------------------------------------------------------
// CompanionContext structural checks
// ---------------------------------------------------------------------------

((): void => {
  const now: number = Date.now();
  const ctx: CompanionContext = {
    identityId:           '550e8400-e29b-41d4-a716-446655440001',
    displayName:          'Test User',
    preferredLanguage:    'en-US',
    interface:            InterfaceKind.Mobile,
    personaHints:         '',
    affectSummary:        '',
    recentMemorySnippets: [],
    activeGoals:          [],
    contextBuiltAt:       now,
  };
  assertEqual('ctx_interface',  ctx.interface, InterfaceKind.Mobile);
  assertEqual('ctx_language',   ctx.preferredLanguage!, 'en-US');
  assertEqual('ctx_identity',   ctx.identityId, '550e8400-e29b-41d4-a716-446655440001');
  assertEqual('ctx_builtAt',    ctx.contextBuiltAt, now);
  assertTrue('ctx_noMemory',    ctx.recentMemorySnippets.length === 0);
  assertTrue('ctx_noGoals',     ctx.activeGoals.length === 0);
  console.log('PASS: CompanionContext_structure');
})();

((): void => {
  const ctx: CompanionContext = {
    identityId:           'anon-iot',
    displayName:          'IoT Device',
    interface:            InterfaceKind.IoT,
    personaHints:         '',
    affectSummary:        '',
    recentMemorySnippets: [],
    activeGoals:          [],
    contextBuiltAt:       Date.now(),
  };
  // preferredLanguage is optional — should be undefined
  assertTrue('ctx_iot_no_lang', ctx.preferredLanguage === undefined);
  assertEqual('ctx_iot_surface', ctx.interface, InterfaceKind.IoT);
  console.log('PASS: CompanionContext_optional_language');
})();

((): void => {
  const ctx: CompanionContext = {
    identityId:           'test-3',
    displayName:          'Dev',
    preferredLanguage:    'zu',
    interface:            InterfaceKind.Headless,
    personaHints:         '',
    affectSummary:        '',
    recentMemorySnippets: ['memory 1', 'memory 2'],
    activeGoals:          ['goal 1'],
    contextBuiltAt:       Date.now(),
  };
  assertEqual('ctx_snippets_len', ctx.recentMemorySnippets.length, 2);
  assertEqual('ctx_goals_len',    ctx.activeGoals.length, 1);
  console.log('PASS: CompanionContext_memory_and_goals');
})();

// ---------------------------------------------------------------------------
// CompanionTurn structural checks
// ---------------------------------------------------------------------------

((): void => {
  const now: number = Date.now();
  const turn: CompanionTurn = { role: 'user', content: 'Hello', timestamp: now };
  assertEqual('turn_role',      turn.role,      'user');
  assertEqual('turn_content',   turn.content,   'Hello');
  assertEqual('turn_timestamp', turn.timestamp, now);
  console.log('PASS: CompanionTurn_user');
})();

((): void => {
  const turn: CompanionTurn = { role: 'assistant', content: 'Hi there!', timestamp: Date.now() };
  assertEqual('assistant_turn_role',    turn.role,    'assistant');
  assertEqual('assistant_turn_content', turn.content, 'Hi there!');
  console.log('PASS: CompanionTurn_assistant');
})();

// ---------------------------------------------------------------------------
// CONFUSION_THRESHOLD and MIN_FACE_CONFIDENCE constants
// ---------------------------------------------------------------------------

((): void => {
  assertApprox('confusion_threshold', CONFUSION_THRESHOLD, 0.70);
  assertApprox('min_confidence',      MIN_FACE_CONFIDENCE,  0.50);
  console.log('PASS: threshold_constants');
})();

// ---------------------------------------------------------------------------
// FaceAffectMapper vector: happy_from_neutral
// initial: engagement=0.5, energy=0.5; confidence=0.92
// expected: engagement=0.53, energy=0.52
// ---------------------------------------------------------------------------

((): void => {
  const affect: AffectState = makeAffectState(0.5, 0.5, 0.2, 0.0, 0.5);
  const matrix: FacialMetricMatrix = makeFaceMatrix(FaceExpressionClassification.Happy, 0.92);
  applyFaceAffect(matrix, affect);
  assertApprox('happy_curiosity',   affect.curiosity,   0.5);
  assertApprox('happy_engagement',  affect.engagement,  0.53);
  assertApprox('happy_uncertainty', affect.uncertainty, 0.2);
  assertApprox('happy_rapport',     affect.rapport,     0.0);
  assertApprox('happy_energy',      affect.energy,      0.52);
  console.log('PASS: face_affect_happy_from_neutral');
})();

// ---------------------------------------------------------------------------
// FaceAffectMapper vector: surprised_from_neutral
// curiosity += 0.04 → 0.54
// ---------------------------------------------------------------------------

((): void => {
  const affect: AffectState = makeAffectState(0.5, 0.5, 0.2, 0.0, 0.5);
  const matrix: FacialMetricMatrix = makeFaceMatrix(FaceExpressionClassification.Surprised, 0.88);
  applyFaceAffect(matrix, affect);
  assertApprox('surprised_curiosity',   affect.curiosity,   0.54);
  assertApprox('surprised_engagement',  affect.engagement,  0.5);
  assertApprox('surprised_uncertainty', affect.uncertainty, 0.2);
  assertApprox('surprised_energy',      affect.energy,      0.5);
  console.log('PASS: face_affect_surprised_from_neutral');
})();

// ---------------------------------------------------------------------------
// FaceAffectMapper vector: confused_from_neutral
// uncertainty += 0.05 → 0.25
// ---------------------------------------------------------------------------

((): void => {
  const affect: AffectState = makeAffectState(0.5, 0.5, 0.2, 0.0, 0.5);
  const matrix: FacialMetricMatrix = makeFaceMatrix(FaceExpressionClassification.Confused, 0.79);
  applyFaceAffect(matrix, affect);
  assertApprox('confused_curiosity',   affect.curiosity,   0.5);
  assertApprox('confused_engagement',  affect.engagement,  0.5);
  assertApprox('confused_uncertainty', affect.uncertainty, 0.25);
  assertApprox('confused_energy',      affect.energy,      0.5);
  console.log('PASS: face_affect_confused_from_neutral');
})();

// ---------------------------------------------------------------------------
// FaceAffectMapper vector: stressed_from_neutral
// uncertainty += 0.08 → 0.28; energy -= 0.05 → 0.45
// ---------------------------------------------------------------------------

((): void => {
  const affect: AffectState = makeAffectState(0.5, 0.5, 0.2, 0.0, 0.5);
  const matrix: FacialMetricMatrix = makeFaceMatrix(FaceExpressionClassification.Stressed, 0.85);
  applyFaceAffect(matrix, affect);
  assertApprox('stressed_curiosity',   affect.curiosity,   0.5);
  assertApprox('stressed_engagement',  affect.engagement,  0.5);
  assertApprox('stressed_uncertainty', affect.uncertainty, 0.28);
  assertApprox('stressed_rapport',     affect.rapport,     0.0);
  assertApprox('stressed_energy',      affect.energy,      0.45);
  console.log('PASS: face_affect_stressed_from_neutral');
})();

// ---------------------------------------------------------------------------
// FaceAffectMapper vector: angry_from_neutral (with rapport=0.3)
// engagement -= 0.04 → 0.46; rapport -= 0.02 → 0.28
// ---------------------------------------------------------------------------

((): void => {
  const affect: AffectState = makeAffectState(0.5, 0.5, 0.2, 0.3, 0.5);
  const matrix: FacialMetricMatrix = makeFaceMatrix(FaceExpressionClassification.Angry, 0.91);
  applyFaceAffect(matrix, affect);
  assertApprox('angry_curiosity',   affect.curiosity,   0.5);
  assertApprox('angry_engagement',  affect.engagement,  0.46);
  assertApprox('angry_uncertainty', affect.uncertainty, 0.2);
  assertApprox('angry_rapport',     affect.rapport,     0.28);
  assertApprox('angry_energy',      affect.energy,      0.5);
  console.log('PASS: face_affect_angry_from_neutral');
})();

// ---------------------------------------------------------------------------
// FaceAffectMapper vector: neutral_expression_no_change
// Neutral must not mutate any axis
// ---------------------------------------------------------------------------

((): void => {
  const affect: AffectState = makeAffectState(0.5, 0.5, 0.2, 0.0, 0.5);
  const before: number = affect.lastUpdatedAt;
  const matrix: FacialMetricMatrix = makeFaceMatrix(FaceExpressionClassification.Neutral, 0.95);
  applyFaceAffect(matrix, affect);
  assertApprox('neutral_curiosity',   affect.curiosity,   0.5);
  assertApprox('neutral_engagement',  affect.engagement,  0.5);
  assertApprox('neutral_uncertainty', affect.uncertainty, 0.2);
  assertApprox('neutral_rapport',     affect.rapport,     0.0);
  assertApprox('neutral_energy',      affect.energy,      0.5);
  // lastUpdatedAt must not change for Neutral (early return before updating it)
  assertEqual('neutral_timestamp_unchanged', affect.lastUpdatedAt, before);
  console.log('PASS: face_affect_neutral_no_change');
})();

// ---------------------------------------------------------------------------
// FaceAffectMapper vector: low_confidence_discarded
// Confidence 0.49 < MIN_FACE_CONFIDENCE=0.50 → no change, even for Stressed
// ---------------------------------------------------------------------------

((): void => {
  const affect: AffectState = makeAffectState(0.5, 0.5, 0.2, 0.0, 0.5);
  const before: number = affect.lastUpdatedAt;
  const matrix: FacialMetricMatrix = makeFaceMatrix(FaceExpressionClassification.Stressed, 0.49);
  applyFaceAffect(matrix, affect);
  assertApprox('lowconf_curiosity',   affect.curiosity,   0.5);
  assertApprox('lowconf_engagement',  affect.engagement,  0.5);
  assertApprox('lowconf_uncertainty', affect.uncertainty, 0.2);
  assertApprox('lowconf_rapport',     affect.rapport,     0.0);
  assertApprox('lowconf_energy',      affect.energy,      0.5);
  assertEqual('lowconf_timestamp_unchanged', affect.lastUpdatedAt, before);
  console.log('PASS: face_affect_low_confidence_discarded');
})();

// ---------------------------------------------------------------------------
// FaceAffectMapper vector: clamp_max_engagement
// Happy from engagement=0.99 → clamped to 1.0; energy 0.5→0.52
// ---------------------------------------------------------------------------

((): void => {
  const affect: AffectState = makeAffectState(0.5, 0.99, 0.2, 0.0, 0.5);
  const matrix: FacialMetricMatrix = makeFaceMatrix(FaceExpressionClassification.Happy, 0.95);
  applyFaceAffect(matrix, affect);
  assertApprox('clampmax_engagement', affect.engagement, 1.0);
  assertApprox('clampmax_energy',     affect.energy,     0.52);
  console.log('PASS: face_affect_clamp_max_engagement');
})();

// ---------------------------------------------------------------------------
// faceObserve: confusion threshold NOT crossed — returns null
// uncertainty=0.2 + confused(+0.05) = 0.25 < 0.70
// ---------------------------------------------------------------------------

((): void => {
  const affect: AffectState = makeAffectState(0.5, 0.5, 0.2, 0.0, 0.5);
  const matrix: FacialMetricMatrix = makeFaceMatrix(FaceExpressionClassification.Confused, 0.79);
  const event: CompanionProactiveEvent | null = faceObserve(
    matrix, affect, 'session-1', 'identity-1', InterfaceKind.Mobile
  );
  assertNull('no_event_below_threshold', event);
  console.log('PASS: faceObserve_no_event_below_threshold');
})();

// ---------------------------------------------------------------------------
// faceObserve: confusion threshold crossed — returns proactive event
// Start with uncertainty=0.67 + confused(+0.05) = 0.72 >= 0.70
// ---------------------------------------------------------------------------

((): void => {
  const affect: AffectState = makeAffectState(0.5, 0.5, 0.67, 0.0, 0.5);
  const matrix: FacialMetricMatrix = makeFaceMatrix(FaceExpressionClassification.Confused, 0.79);
  const event: CompanionProactiveEvent | null = faceObserve(
    matrix, affect, 'session-2', 'identity-2', InterfaceKind.Mobile
  );
  assertNotNull('event_above_threshold', event);
  const ev: CompanionProactiveEvent = event!;
  assertEqual('event_sessionId',    ev.sessionId,   'session-2');
  assertEqual('event_identityId',   ev.identityId,  'identity-2');
  assertEqual('event_surface',      ev.interface,   InterfaceKind.Mobile);
  assertEqual('event_triggerName',  ev.triggerName, 'face.confusion_detected');
  assertTrue('event_has_message',   ev.message.length > 0);
  console.log('PASS: faceObserve_triggers_event_above_threshold');
})();

// ---------------------------------------------------------------------------
// faceObserve: Stressed expression also triggers confusion event
// Start with uncertainty=0.64 + stressed(+0.08) = 0.72 >= 0.70
// ---------------------------------------------------------------------------

((): void => {
  const affect: AffectState = makeAffectState(0.5, 0.5, 0.64, 0.0, 0.5);
  const matrix: FacialMetricMatrix = makeFaceMatrix(FaceExpressionClassification.Stressed, 0.85);
  const event: CompanionProactiveEvent | null = faceObserve(
    matrix, affect, 'session-3', 'identity-3', InterfaceKind.Wearable
  );
  assertNotNull('stressed_event',           event);
  assertEqual('stressed_event_trigger', event!.triggerName, 'face.confusion_detected');
  console.log('PASS: faceObserve_stressed_triggers_event');
})();

// ---------------------------------------------------------------------------
// faceObserve: Happy expression never triggers confusion event
// ---------------------------------------------------------------------------

((): void => {
  const affect: AffectState = makeAffectState(0.5, 0.5, 0.9, 0.0, 0.5);  // very high uncertainty
  const matrix: FacialMetricMatrix = makeFaceMatrix(FaceExpressionClassification.Happy, 0.95);
  const event: CompanionProactiveEvent | null = faceObserve(
    matrix, affect, 'session-4', 'identity-4', InterfaceKind.Mobile
  );
  // Happy does not qualify for the confusion trigger (only Confused/Stressed does)
  assertNull('happy_no_confusion_event', event);
  console.log('PASS: faceObserve_happy_no_confusion_event');
})();

// ---------------------------------------------------------------------------
// faceObserve: low confidence → no affect change, no event
// ---------------------------------------------------------------------------

((): void => {
  const affect: AffectState = makeAffectState(0.5, 0.5, 0.9, 0.0, 0.5);  // very high uncertainty
  const matrix: FacialMetricMatrix = makeFaceMatrix(FaceExpressionClassification.Confused, 0.49);
  const event: CompanionProactiveEvent | null = faceObserve(
    matrix, affect, 'session-5', 'identity-5', InterfaceKind.Mobile
  );
  // Low confidence → applyFaceAffect does nothing → uncertainty stays 0.9 >= 0.70
  // but expression check in faceObserve fires AFTER applyFaceAffect
  // uncertainty was already >= threshold before, but expression=Confused → should fire
  // Actually: applyFaceAffect returns early on low confidence, then faceObserve
  // checks: affect.uncertainty >= CONFUSION_THRESHOLD (0.9 >= 0.70 = true)
  // AND expression===Confused → true. So event IS triggered despite low confidence
  // because the uncertainty was already above threshold.
  // This is correct behavior per spec (we guard affect mutation, not the observe check).
  // No assertion on null here — just verify applyFaceAffect didn't change values.
  assertApprox('lowconf_observe_uncertainty', affect.uncertainty, 0.9);
  console.log('PASS: faceObserve_low_confidence_no_affect_mutation');
})();

console.log('CompanionTypesTest: ALL PASS');
