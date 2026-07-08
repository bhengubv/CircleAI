// memory/feedback_analyser.ts
// Analyses a window of FeedbackSignal records and produces PersonaAdaptation
// deltas. Ported from CircleAI.Memory.FeedbackAnalyser (C#).
//
// Rules (applied to the most-recent N signals, default N=20):
//   - >70% negative signals → verbosityDelta = -0.1
//   - >70% positive signals → verbosityDelta = +0.05
//   - formalityDelta is always 0 (reserved for future heuristics)
//   - preferredTopics is always empty — FeedbackSignal carries no topic tags
//
// The C# PersonaAdaptation holds `float` deltas. TypeScript has no float type,
// so the constants are narrowed through Math.fround to reproduce the exact FP32
// values the C# record would carry (-0.1f, +0.05f) — this keeps the cross-
// language fixture contract byte-identical.

import type { FeedbackSignal } from "./index.js";
import { FeedbackPolarity } from "./index.js";

/** FP32-narrowed delta constants, matching the C# `float` literals. */
const VERBOSITY_DOWN = Math.fround(-0.1);
const VERBOSITY_UP = Math.fround(0.05);

/** Deltas to apply to PersonaState after analysing feedback signals. */
export interface PersonaAdaptation {
  readonly verbosityDelta: number;
  readonly formalityDelta: number;
  readonly preferredTopics: string[];
}

/**
 * Analyses recent {@link FeedbackSignal} records and produces
 * {@link PersonaAdaptation} adjustments.
 */
export class FeedbackAnalyser {
  private readonly windowSize: number;

  /** @param windowSize Number of most-recent signals to consider. Min 1. Default 20. */
  constructor(windowSize = 20) {
    if (windowSize < 1) throw new RangeError("Window size must be at least 1.");
    this.windowSize = windowSize;
  }

  /**
   * Compute persona adaptation from the provided signals.
   *
   * `verbosityDelta` is:
   *   • -0.1  when more than 70% of the window is negative
   *   • +0.05 when more than 70% of the window is positive
   *   • 0     otherwise
   *
   * `formalityDelta` is always 0 and `preferredTopics` is always empty because
   * {@link FeedbackSignal} carries no topic metadata.
   */
  analyse(signals: Iterable<FeedbackSignal>): PersonaAdaptation {
    if (signals == null) throw new Error("signals required");

    const window = [...signals]
      .sort((a, b) => b.recordedAtUtc.getTime() - a.recordedAtUtc.getTime())
      .slice(0, this.windowSize);

    if (window.length === 0)
      return { verbosityDelta: 0, formalityDelta: 0, preferredTopics: [] };

    const positiveCount = window.filter((s) => s.polarity === FeedbackPolarity.Positive).length;
    const negativeCount = window.filter((s) => s.polarity === FeedbackPolarity.Negative).length;
    const total = window.length;

    let verbosityDelta = 0;
    const negativeRatio = negativeCount / total;
    const positiveRatio = positiveCount / total;

    if (negativeRatio > 0.7) verbosityDelta = VERBOSITY_DOWN;
    else if (positiveRatio > 0.7) verbosityDelta = VERBOSITY_UP;

    // FeedbackSignal has no tags — topic extraction is deferred.
    return { verbosityDelta, formalityDelta: 0, preferredTopics: [] };
  }
}
