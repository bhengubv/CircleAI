// companion/index.ets — Circle AI Companion layer
// ArkTS port: no `any`, explicit enum values, no abstract classes (use interfaces).
// FaceAffectMapper deltas must match fixtures/facex_biometric_vectors.json within 1e-5.

import { AffectState } from '../memory/index';
import { FacialMetricMatrix, FaceExpressionClassification } from '../tools/index';

// ---------------------------------------------------------------------------
// InterfaceKind — surface enumeration
// ---------------------------------------------------------------------------

/**
 * The surface on which the Companion session is running.
 */
export enum InterfaceKind {
  Mobile   = 0,
  Wearable = 1,
  Desktop  = 2,
  Web      = 3,
  IoT      = 4,
  Ambient  = 5,
  Headless = 6,
}

// ---------------------------------------------------------------------------
// Data types
// ---------------------------------------------------------------------------

/**
 * Snapshot of all context injected into the Companion's system prompt.
 */
export interface CompanionContext {
  identityId: string;
  displayName: string;
  preferredLanguage?: string;        // BCP-47, optional
  interface: InterfaceKind;
  personaHints: string;
  affectSummary: string;
  recentMemorySnippets: string[];
  activeGoals: string[];
  contextBuiltAt: number;            // unix ms
}

/**
 * A single turn in the Companion conversation log.
 * role is "user" or "assistant".
 */
export interface CompanionTurn {
  role: string;           // 'user' | 'assistant'
  content: string;
  timestamp: number;      // unix ms
}

/**
 * Metadata emitted when the Companion proactively initiates contact.
 */
export interface CompanionProactiveEvent {
  sessionId: string;
  identityId: string;
  interface: InterfaceKind;
  message: string;
  triggerName: string;
  generatedAt: number;    // unix ms
}

// ---------------------------------------------------------------------------
// FaceAffectMapper
// ---------------------------------------------------------------------------

/** Minimum confidence score required to apply a facial expression signal. */
export const MIN_FACE_CONFIDENCE: number = 0.5;

/**
 * Applies the affect delta for the observed facial expression to the given AffectState.
 * Does nothing when confidenceScore < MIN_FACE_CONFIDENCE.
 *
 * Exact deltas (must match fixtures/facex_biometric_vectors.json within 1e-5):
 *   Happy:     engagement += 0.03, energy += 0.02
 *   Surprised: curiosity  += 0.04
 *   Confused:  uncertainty+= 0.05
 *   Stressed:  uncertainty+= 0.08, energy -= 0.05
 *   Angry:     engagement -= 0.04, rapport -= 0.02
 *   Neutral / others: no change
 */
export function applyFaceAffect(matrix: FacialMetricMatrix, affect: AffectState): void {
  if (matrix.confidenceScore < MIN_FACE_CONFIDENCE) return;

  switch (matrix.expression) {
    case FaceExpressionClassification.Happy:
      affect.engagement = Math.min(1, affect.engagement + 0.03);
      affect.energy     = Math.min(1, affect.energy     + 0.02);
      break;
    case FaceExpressionClassification.Surprised:
      affect.curiosity  = Math.min(1, affect.curiosity  + 0.04);
      break;
    case FaceExpressionClassification.Confused:
      affect.uncertainty = Math.min(1, affect.uncertainty + 0.05);
      break;
    case FaceExpressionClassification.Stressed:
      affect.uncertainty = Math.min(1, affect.uncertainty + 0.08);
      affect.energy      = Math.max(0, affect.energy      - 0.05);
      break;
    case FaceExpressionClassification.Angry:
      affect.engagement  = Math.max(0, affect.engagement  - 0.04);
      affect.rapport     = Math.max(0, affect.rapport     - 0.02);
      break;
    default:
      return;
  }
  affect.lastUpdatedAt = Date.now();
}

/**
 * Threshold at which uncertainty triggers the confusion proactive event.
 */
export const CONFUSION_THRESHOLD: number = 0.70;

/**
 * Observes a face frame, applies the affect delta, and returns a proactive
 * event if the confusion threshold is crossed after a Confused or Stressed
 * expression.
 *
 * Returns null if no event should be triggered.
 */
export function faceObserve(
  matrix: FacialMetricMatrix,
  affect: AffectState,
  sessionId: string,
  identityId: string,
  surface: InterfaceKind,
): CompanionProactiveEvent | null {
  applyFaceAffect(matrix, affect);

  const triggered: boolean =
    affect.uncertainty >= CONFUSION_THRESHOLD &&
    (matrix.expression === FaceExpressionClassification.Confused ||
     matrix.expression === FaceExpressionClassification.Stressed);

  if (!triggered) return null;

  return {
    sessionId,
    identityId,
    interface: surface,
    message: 'I notice you might be finding this a bit tricky. Would you like me to slow down or explain it differently?',
    triggerName: 'face.confusion_detected',
    generatedAt: Date.now(),
  };
}

// ---------------------------------------------------------------------------
// ICompanionSession
// ---------------------------------------------------------------------------

/**
 * A Companion conversation session.
 * Implementations own native model state.
 */
export interface ICompanionSession {
  send(userMessage: string): Promise<string>;
  stream(userMessage: string): AsyncGenerator<string>;
  close(): Promise<void>;
}
