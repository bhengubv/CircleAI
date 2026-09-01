/**
 * speech/end_of_turn_detectors
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// speech/end_of_turn_detectors.ts
//
// End-of-turn detectors (port of CircleAI.Speech.EndOfTurnDetectors.cs):
//   - NullEndOfTurnDetector: always "they finished" DI default.
//   - RuleBasedEndOfTurnDetector: punctuation + trailing-silence heuristics
//     with "thinking connector" hanging-word handling.
//   - SmartTurnDetector: delegates to a host ITurnModelRunner; falls back to
//     the rule-based detector.
//
// TimeSpan → milliseconds (number). float confidences → number (the values are
// literal single-precision constants; no accumulation needs frounding here).

import { endOfTurnResult, type EndOfTurnResult, type IEndOfTurnDetector } from "./contracts";

/** Always says "they finished" — DI default. */
export class NullEndOfTurnDetector implements IEndOfTurnDetector {
  static readonly instance = new NullEndOfTurnDetector();
  get backendId(): string {
    return "null";
  }

  predict(_partialTranscript: string, _trailingSilenceMs: number): EndOfTurnResult {
    return endOfTurnResult(true, 1, 0);
  }

  reset(): void {
    /* no state */
  }
}

const TERMINAL_PUNCTUATION = [".", "!", "?", "。", "！", "？"];
const HANGING_WORDS = new Set([
  "and",
  "but",
  "so",
  "or",
  "because",
  "if",
  "when",
  "while",
  "though",
  "however",
  "um",
  "uh",
  "like",
  "you",
  "the",
  "a",
  "an",
]);

/**
 * Rule-based detector. A turn is complete when the transcript ends with terminal
 * punctuation AND the user has been silent for at least the minimum hangover, OR
 * when silence exceeds the maximum-wait ceiling regardless of text. Recognises
 * common "thinking" connectors (and, but, so, um, like…) to extend the wait when
 * present at the tail.
 */
export class RuleBasedEndOfTurnDetector implements IEndOfTurnDetector {
  private readonly minSilenceMs: number;
  private readonly hangingSilenceMs: number;
  private readonly maxSilenceMs: number;

  constructor(minSilenceMs = 400, hangingSilenceMs = 900, maxSilenceMs = 2500) {
    this.minSilenceMs = minSilenceMs;
    this.hangingSilenceMs = hangingSilenceMs;
    this.maxSilenceMs = maxSilenceMs;
  }

  get backendId(): string {
    return "rules";
  }

  predict(partialTranscript: string, trailingSilenceMs: number): EndOfTurnResult {
    const text = (partialTranscript ?? "").trim();
    if (trailingSilenceMs >= this.maxSilenceMs) {
      return endOfTurnResult(true, 0.7, 0);
    }

    if (text.length === 0) {
      return endOfTurnResult(
        false,
        0.2,
        Math.trunc(Math.max(150, this.minSilenceMs - trailingSilenceMs)),
      );
    }

    const endsTerminal = TERMINAL_PUNCTUATION.some((p) => text.endsWith(p));
    const words = text.split(/[ \t\n]+/).filter((w) => w.length > 0);
    const lastWord = words.length > 0 ? words[words.length - 1] : "";
    const endsHanging = HANGING_WORDS.has(trimPunct(lastWord).toLowerCase());

    if (endsHanging) {
      const remaining = this.hangingSilenceMs - trailingSilenceMs;
      if (remaining <= 0) {
        return endOfTurnResult(true, 0.6, 0);
      }
      return endOfTurnResult(false, 0.4, Math.ceil(remaining));
    }

    if (endsTerminal && trailingSilenceMs >= this.minSilenceMs) {
      return endOfTurnResult(true, 0.9, 0);
    }

    if (trailingSilenceMs >= this.minSilenceMs) {
      return endOfTurnResult(true, 0.75, 0);
    }

    const ms = Math.trunc(Math.max(50, this.minSilenceMs - trailingSilenceMs));
    return endOfTurnResult(false, 0.6, ms);
  }

  reset(): void {
    /* no state */
  }
}

/** Trim the C# `TrimEnd('.', ',', '!', '?')` set from the end of `word`. */
function trimPunct(word: string): string {
  let end = word.length;
  while (end > 0 && (".,!?".includes(word[end - 1]))) end--;
  return word.slice(0, end);
}

/** Host-supplied semantic turn model. Mirrors `ITurnModelRunner`. */
export interface ITurnModelRunner {
  /** Score the current state; 0..1 = probability the turn is complete. */
  scoreCompletion(partialTranscript: string, trailingSilenceMs: number): number;
}

/**
 * Smart-turn wrapper. Uses the supplied semantic model when present; otherwise
 * falls back to {@link RuleBasedEndOfTurnDetector}.
 */
export class SmartTurnDetector implements IEndOfTurnDetector {
  private readonly runner: ITurnModelRunner | null;
  private readonly fallback: RuleBasedEndOfTurnDetector;
  private readonly threshold: number;

  constructor(runner: ITurnModelRunner | null = null, threshold = 0.5) {
    this.runner = runner;
    this.fallback = new RuleBasedEndOfTurnDetector();
    this.threshold = threshold;
  }

  get backendId(): string {
    return this.runner === null ? "smart-turn (fallback)" : "smart-turn-v2";
  }

  predict(partialTranscript: string, trailingSilenceMs: number): EndOfTurnResult {
    if (this.runner === null) {
      return this.fallback.predict(partialTranscript, trailingSilenceMs);
    }

    const prob = clamp01(this.runner.scoreCompletion(partialTranscript, trailingSilenceMs));
    if (prob >= this.threshold) {
      return endOfTurnResult(true, prob, 0);
    }
    const waitMs = Math.round((1 - prob) * 1000);
    return endOfTurnResult(false, prob, waitMs);
  }

  reset(): void {
    this.fallback.reset();
  }
}

function clamp01(v: number): number {
  if (v < 0) return 0;
  if (v > 1) return 1;
  return v;
}
