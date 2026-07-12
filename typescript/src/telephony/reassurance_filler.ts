// telephony/reassurance_filler.ts
//
// When a tool call takes more than the awkward-silence threshold (~600 ms) the
// AI plays a filler line like "Give me a moment to check that…" so the caller
// doesn't think the line dropped. Faithful port of ReassuranceFiller.cs.
//
// C# runs the filler loop on a background task linked to a CTS that the work
// path cancels on completion; we mirror that with an AbortController the work
// path aborts. `Task.Delay(..., ct)` → {@link delay}. Rotation counters use the
// C# `Interlocked.Increment(ref x) - 1` then `abs(idx) % count` indexing.

import type { BriefingSynthesiser, ICallSession } from "./contracts.js";
import { audioFrame, CallMediaFormat } from "./primitives.js";
import { delay, isCancellation } from "./internal.js";

/** Phrases the filler picks from. Rotated to avoid repetition. Mirrors `ReassuranceVocabulary`. */
export interface ReassuranceVocabulary {
  readonly shortFillers: readonly string[];
  readonly longFillers: readonly string[];
}

/** Sensible English defaults. Mirrors `ReassuranceVocabulary.Default`. */
export const DEFAULT_REASSURANCE_VOCABULARY: ReassuranceVocabulary = {
  shortFillers: ["One moment.", "Let me check.", "Give me a sec.", "Just a moment."],
  longFillers: [
    "Still looking that up for you.",
    "This is taking a bit longer than usual — bear with me.",
    "Almost there — still pulling that information.",
    "Thanks for your patience, I'm checking that now.",
  ],
};

/** Configuration for the filler driver. Mirrors `ReassuranceFillerOptions`. */
export interface ReassuranceFillerOptions {
  /** Silence (ms) after which to play a short filler. Default 600 ms. */
  readonly shortFillerAfterMs?: number;
  /** Cadence (ms) for long fillers after the first short one. Default 3000 ms. */
  readonly longFillerEveryMs?: number;
  /** Phrase pool. */
  readonly vocabulary?: ReassuranceVocabulary;
}

function shortAfter(o: ReassuranceFillerOptions): number {
  return o.shortFillerAfterMs ?? 600;
}
function longEvery(o: ReassuranceFillerOptions): number {
  return o.longFillerEveryMs ?? 3000;
}
function vocab(o: ReassuranceFillerOptions): ReassuranceVocabulary {
  return o.vocabulary ?? DEFAULT_REASSURANCE_VOCABULARY;
}

/** Driver that plays fillers while a long task runs. Mirrors `IReassuranceFiller`. */
export interface IReassuranceFiller {
  /**
   * Run `work`. If it doesn't complete before the short-filler threshold, speak
   * a short phrase via `tts`; while still pending speak long phrases on the
   * configured cadence. Returns the work's result.
   */
  runWithFillerAsync<T>(
    work: (signal?: AbortSignal) => Promise<T>,
    session: ICallSession,
    tts: BriefingSynthesiser,
    signal?: AbortSignal,
  ): Promise<T>;
}

/** Default in-memory filler driver. Mirrors `DefaultReassuranceFiller`. */
export class DefaultReassuranceFiller implements IReassuranceFiller {
  private readonly options: ReassuranceFillerOptions;
  private shortRotation = 0;
  private longRotation = 0;

  constructor(options?: ReassuranceFillerOptions) {
    this.options = options ?? {};
  }

  async runWithFillerAsync<T>(
    work: (signal?: AbortSignal) => Promise<T>,
    session: ICallSession,
    tts: BriefingSynthesiser,
    signal?: AbortSignal,
  ): Promise<T> {
    if (work === null || work === undefined) throw new Error("work is required");
    if (session === null || session === undefined) throw new Error("session is required");
    if (tts === null || tts === undefined) throw new Error("tts is required");

    // Linked controller: aborted when the caller aborts OR when work finishes.
    const fillerController = new AbortController();
    const onParentAbort = (): void => fillerController.abort();
    if (signal) {
      if (signal.aborted) fillerController.abort();
      else signal.addEventListener("abort", onParentAbort, { once: true });
    }

    const fillerTask = this.speakFillersAsync(session, tts, fillerController.signal);
    try {
      const result = await work(signal);
      fillerController.abort();
      try {
        await fillerTask;
      } catch (ex) {
        if (!isCancellation(ex)) throw ex;
      }
      return result;
    } catch (workErr) {
      fillerController.abort();
      try {
        await fillerTask;
      } catch (ex) {
        if (!isCancellation(ex)) throw ex;
      }
      throw workErr;
    } finally {
      if (signal) signal.removeEventListener("abort", onParentAbort);
    }
  }

  private async speakFillersAsync(
    session: ICallSession,
    tts: BriefingSynthesiser,
    signal: AbortSignal,
  ): Promise<void> {
    const v = vocab(this.options);
    try {
      await delay(shortAfter(this.options), signal);
      await DefaultReassuranceFiller.speakAsync(session, tts, this.nextShort(v), signal);

      while (!signal.aborted) {
        await delay(longEvery(this.options), signal);
        await DefaultReassuranceFiller.speakAsync(session, tts, this.nextLong(v), signal);
      }
    } catch (ex) {
      if (!isCancellation(ex)) throw ex; // expected when work finishes
    }
  }

  private nextShort(v: ReassuranceVocabulary): string {
    if (v.shortFillers.length === 0) return "One moment.";
    const idx = this.shortRotation++; // post-increment == C# `Increment(...) - 1`
    return v.shortFillers[Math.abs(idx) % v.shortFillers.length]!;
  }

  private nextLong(v: ReassuranceVocabulary): string {
    if (v.longFillers.length === 0) return "Almost there.";
    const idx = this.longRotation++;
    return v.longFillers[Math.abs(idx) % v.longFillers.length]!;
  }

  private static async speakAsync(
    session: ICallSession,
    tts: BriefingSynthesiser,
    text: string,
    signal: AbortSignal,
  ): Promise<void> {
    const audio = await tts(text, signal);
    if (audio.length > 0) {
      await session.sendAudioAsync(audioFrame(audio, CallMediaFormat.Pcm24000, 0), signal);
    }
  }
}
