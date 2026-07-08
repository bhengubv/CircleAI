// companion/index.ts
// Circle AI Companion layer: context types, session interface, face affect mapping.
// Ported from Circle.AI.Companion (C#).

import { AffectState } from "../memory/index.js";
import { FacialMetricMatrix, FaceExpressionClassification } from "../tools/index.js";

// ── HER/Jarvis contracts + deterministic in-memory implementations ───────────
// (HerJarvisContracts.cs + HerJarvisRealImplementations.cs + IVoiceListener.cs +
//  VoiceCompanionListener.cs + SelfBenchSelfImprovementLoop.cs)
export * from "./herjarvis/index.js";

// ── External capability registry (CapabilityRegistry.cs) ─────────────────────
export * from "./capability_registry.js";

// ── Companion session factory (CompanionSessionFactory.cs) ───────────────────
export * from "./session_factory.js";

// ── Proactive briefing service (ProactiveBriefingService.cs) ─────────────────
// Named re-exports (NOT `export *`) so the briefing module's local `ChatMessage`
// stub — which duplicates the real one in ../models — does not collide in the
// package barrel. The connector record/interface names are briefing-local and
// distinct from any existing package export.
export {
  ProactiveBriefingService,
  DEFAULT_FIRE_TIMES_UTC_MINUTES,
} from "./proactive_briefing.js";
export type {
  IBriefingNotifier,
  ProactiveBriefingOptions,
  ProactiveBriefingDeps,
  ICalendarConnector,
  IEmailConnector,
  INewsSource,
  IWeatherProvider,
  IAIService as IBriefingAIService,
  CalendarEvent as BriefingCalendarEvent,
  EmailMessage as BriefingEmailMessage,
  NewsItem as BriefingNewsItem,
  WeatherSample as BriefingWeatherSample,
} from "./proactive_briefing.js";

// ─────────────────────────────────────────────────────────────────────────────
// InterfaceKind enum
// ─────────────────────────────────────────────────────────────────────────────

/**
 * The surface on which the Companion session is running.
 * Determines sensory capabilities, available UI affordances, and
 * how the Companion adapts its communication style.
 */
export enum InterfaceKind {
  /** Mobile phone or tablet (MAUI). */
  Mobile = "Mobile",
  /** Smartwatch or fitness band with a small display. */
  Wearable = "Wearable",
  /** Desktop or laptop computer (MAUI or WPF). */
  Desktop = "Desktop",
  /** Browser-based experience (Blazor). */
  Web = "Web",
  /** Embedded IoT device — voice in, voice out, minimal compute. */
  IoT = "IoT",
  /** Always-on ambient surface — smart speaker, room display, car. */
  Ambient = "Ambient",
  /** Programmatic / background / testing context (no UI). */
  Headless = "Headless",
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionContext
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Snapshot of all context injected into the Companion's system prompt.
 * Rebuilt at the start of each session and refreshed on request.
 */
export interface CompanionContext {
  readonly identityId: string;
  readonly displayName: string;
  readonly preferredLanguage: string | null;
  readonly interface: InterfaceKind;
  readonly personaHints: string;
  readonly affectSummary: string;
  readonly recentMemorySnippets: readonly string[];
  readonly activeGoals: readonly string[];
  readonly contextBuiltAt: Date;
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionTurn
// ─────────────────────────────────────────────────────────────────────────────

/**
 * A single turn in the Companion conversation log, held in memory for the
 * duration of the session.
 */
export interface CompanionTurn {
  readonly role: string; // "user" | "assistant"
  readonly content: string;
  readonly timestamp: Date;
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionProactiveEvent
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Metadata emitted when the Companion proactively initiates contact.
 */
export interface CompanionProactiveEvent {
  readonly sessionId: string;
  readonly identityId: string;
  readonly interface: InterfaceKind;
  readonly message: string;
  readonly triggerName: string;
  readonly generatedAt: Date;
}

// ─────────────────────────────────────────────────────────────────────────────
// ICompanionSession
// ─────────────────────────────────────────────────────────────────────────────

/** Callback type for proactive message events. */
export type ProactiveMessageHandler = (event: CompanionProactiveEvent) => void;

/**
 * A Companion conversation session. Combines identity awareness, cross-device
 * memory, language adaptation, affect sensing, and proactive reasoning into a
 * single coherent interface.
 */
export interface ICompanionSession {
  // ── Identity ───────────────────────────────────────────────────────────────
  readonly sessionId: string;
  readonly identityId: string;
  readonly interface: InterfaceKind;

  // ── Core conversation ──────────────────────────────────────────────────────
  /**
   * Send a message to the Companion and receive a complete reply.
   * Context enrichment is applied automatically.
   */
  sendAsync(message: string): Promise<string>;

  /**
   * Stream the Companion's reply token-by-token for low-latency rendering.
   */
  streamAsync(message: string): AsyncGenerator<string>;

  /**
   * Agentic mode: sends the instruction, detects tool calls in the reply,
   * executes them, and re-prompts until the model produces a plain-text answer.
   */
  agentAsync(instruction: string): Promise<string>;

  // ── Context ────────────────────────────────────────────────────────────────
  getContext(): CompanionContext;
  refreshContextAsync(): Promise<void>;

  // ── History ────────────────────────────────────────────────────────────────
  readonly history: readonly CompanionTurn[];

  // ── Feedback ───────────────────────────────────────────────────────────────
  signalFeedbackAsync(positive: boolean, note?: string): Promise<void>;

  // ── Proactive ──────────────────────────────────────────────────────────────
  onProactiveMessageReady: ProactiveMessageHandler | null;
}

// ─────────────────────────────────────────────────────────────────────────────
// FaceAffectMapper
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Minimum confidence score for a face detection to be used as an affect signal.
 * Detections below this threshold are silently discarded.
 */
export const FACE_AFFECT_CONFIDENCE_THRESHOLD = 0.5;

/**
 * Maps a FacialMetricMatrix expression observation to mutations of AffectState.
 * Mutates affect in place. No-op when confidence < 0.5 or expression is
 * NEUTRAL or UNKNOWN.
 *
 * Mapping table (validated against fixtures/facex_biometric_vectors.json):
 *   Happy     → engagement += 0.03, energy     += 0.02
 *   Surprised → curiosity  += 0.04
 *   Confused  → uncertainty += 0.05
 *   Stressed  → uncertainty += 0.08, energy    -= 0.05
 *   Angry     → engagement -= 0.04, rapport    -= 0.02
 *   Neutral   → no change
 *   Unknown   → no change
 *
 * All values are clamped to [0.0, 1.0] consistent with AffectState conventions.
 */
export function applyFaceToAffect(matrix: FacialMetricMatrix, affect: AffectState): void {
  if (matrix.confidenceScore < FACE_AFFECT_CONFIDENCE_THRESHOLD) return;

  switch (matrix.expression) {
    case FaceExpressionClassification.HAPPY:
      affect.engagement = Math.min(1, affect.engagement + 0.03);
      affect.energy = Math.min(1, affect.energy + 0.02);
      break;

    case FaceExpressionClassification.SURPRISED:
      affect.curiosity = Math.min(1, affect.curiosity + 0.04);
      break;

    case FaceExpressionClassification.CONFUSED:
      affect.uncertainty = Math.min(1, affect.uncertainty + 0.05);
      break;

    case FaceExpressionClassification.STRESSED:
      affect.uncertainty = Math.min(1, affect.uncertainty + 0.08);
      affect.energy = Math.max(0, affect.energy - 0.05);
      break;

    case FaceExpressionClassification.ANGRY:
      affect.engagement = Math.max(0, affect.engagement - 0.04);
      affect.rapport = Math.max(0, affect.rapport - 0.02);
      break;

    case FaceExpressionClassification.NEUTRAL:
    case FaceExpressionClassification.UNKNOWN:
    default:
      // No affect change for neutral or unclassifiable expressions.
      return;
  }

  affect.lastUpdatedUtc = new Date();
}

// ─────────────────────────────────────────────────────────────────────────────
// FaceCompanionBridge
// ─────────────────────────────────────────────────────────────────────────────

/**
 * AffectState.uncertainty level at or above which a proactive companion message
 * is triggered, provided the observed expression is also CONFUSED or STRESSED.
 */
export const CONFUSION_THRESHOLD = 0.70;

/**
 * Apply a face observation to the affect state and optionally surface
 * a proactive companion event.
 *
 * Steps:
 * 1. Apply affect mutations via applyFaceToAffect.
 * 2. Check if post-mutation uncertainty >= CONFUSION_THRESHOLD AND
 *    expression is CONFUSED or STRESSED.
 * 3. Return a CompanionProactiveEvent with trigger "face.confusion_detected" if so.
 *
 * Returns null when no threshold is crossed.
 */
export function observeFace(
  matrix: FacialMetricMatrix,
  affect: AffectState,
  sessionId: string,
  identityId: string,
  surface: InterfaceKind,
): CompanionProactiveEvent | null {
  applyFaceToAffect(matrix, affect);

  const isConfused =
    affect.uncertainty >= CONFUSION_THRESHOLD &&
    (matrix.expression === FaceExpressionClassification.CONFUSED ||
      matrix.expression === FaceExpressionClassification.STRESSED);

  if (!isConfused) return null;

  return {
    sessionId,
    identityId,
    interface: surface,
    message:
      "I notice you might be finding this a bit tricky. " +
      "Would you like me to slow down or explain it differently?",
    triggerName: "face.confusion_detected",
    generatedAt: new Date(),
  };
}
