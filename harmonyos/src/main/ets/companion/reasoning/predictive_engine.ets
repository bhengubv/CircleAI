/**
 * companion/reasoning/predictive_engine
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/reasoning/predictive_engine.ts
//
// Two IPredictiveEngine implementations, ported from the C# reference:
//
//   HistogramPredictiveEngine — HerJarvisRealImplementations.cs #14. A
//                               168-bucket (24h × 7d) time-of-day histogram
//                               per need; anticipates a need if any of its
//                               histogram slots fall inside the horizon.
//   SequencePredictiveEngine  — SequencePredictiveEngine.cs. A variable-order
//                               Markov chain (default 3-gram) over the event
//                               timeline, with back-off weighting and mean
//                               inter-arrival forecasting.
//
// Both in-memory and deterministic. All arithmetic is `double` in the C#, so
// no `Math.fround` is needed. DayOfWeek/Hour map cleanly: C# DayOfWeek is
// Sunday=0..Saturday=6, identical to JS Date.getUTCDay(); UtcDateTime.Hour ==
// getUTCHours().

import type { AnticipatedNeed, IPredictiveEngine } from "./contracts";

const SLOTS_PER_WEEK = 24 * 7;

/** C# `(int)atUtc.DayOfWeek * 24 + atUtc.UtcDateTime.Hour`. */
function slotOf(at: Date): number {
  return at.getUTCDay() * 24 + at.getUTCHours();
}

// =====================================================================
// 14. HistogramPredictiveEngine — time-of-day histogram of recurring events.
// =====================================================================

/**
 * Per description, a 168-slot histogram of when the need occurred (by
 * UTC day-of-week × hour). Anticipates the need when a slot inside the
 * horizon (sampled every 30 minutes) has a non-zero count, reporting the
 * fraction of the need's total occurrences that fall in the window.
 */
export class HistogramPredictiveEngine implements IPredictiveEngine {
  // description(lower) -> long[168], case-insensitive key (OrdinalIgnoreCase),
  // first surface form remembered for the AnticipatedNeed.Description.
  private readonly hist = new Map<string, number[]>(); // key: lower-cased
  private readonly forms = new Map<string, string>(); // lower -> first surface form

  /** Tell the engine: this need occurred at this UTC time. */
  observe(description: string, atUtc: Date): void {
    if (!description || description.trim().length === 0) throw new Error("description required");
    const lc = description.toLowerCase();
    let arr = this.hist.get(lc);
    if (arr === undefined) {
      arr = new Array<number>(SLOTS_PER_WEEK).fill(0);
      this.hist.set(lc, arr);
      this.forms.set(lc, description);
    }
    arr[slotOf(atUtc)]++;
  }

  async anticipateAsync(horizonMinutes: number): Promise<readonly AnticipatedNeed[]> {
    if (horizonMinutes <= 0) throw new Error("horizonMinutes out of range");
    const now = new Date();
    const results: AnticipatedNeed[] = [];
    for (const [lc, arr] of this.hist) {
      let total = 0;
      for (const v of arr) total += v;
      let upcoming = 0;
      for (let m = 0; m <= horizonMinutes; m += 30) {
        const when = new Date(now.getTime() + m * 60_000);
        upcoming += arr[slotOf(when)];
      }
      if (total === 0 || upcoming === 0) continue;
      results.push({
        description: this.forms.get(lc)!,
        // now.AddMinutes(horizonMinutes / 2) — C# int division truncates.
        expectedByUtc: new Date(now.getTime() + Math.trunc(horizonMinutes / 2) * 60_000),
        probability: upcoming / total,
      });
    }
    // OrderByDescending(r => r.Probability): stable → insertion order among ties.
    return stableSortByDesc(results, (r) => r.probability);
  }
}

// =====================================================================
// SequencePredictiveEngine — variable-order Markov chain over the timeline.
// =====================================================================

interface TimedEvent {
  readonly event: string;
  readonly atUtc: Date;
}

/**
 * A variable-order Markov chain over the observed event timeline. On observe
 * it builds n-gram contexts up to `order` and tracks per-event mean
 * inter-arrival time; on anticipate it backs off from the longest context to
 * the shortest (weighting longer contexts higher), then forecasts arrival
 * from each event's mean interval.
 */
export class SequencePredictiveEngine implements IPredictiveEngine {
  // (previous-n-events joined by "|") -> { next event -> count }. Ordinal
  // (case-sensitive) keys, matching StringComparer.Ordinal.
  private readonly transitions = new Map<string, Map<string, number>>();
  // event -> { count, sumSeconds } for mean-interval forecasting.
  private readonly interArrivals = new Map<string, { count: number; sumSeconds: number }>();
  private readonly history: TimedEvent[] = [];
  private readonly order: number;

  constructor(order = 3) {
    if (order < 1 || order > 6) throw new Error("order out of range");
    this.order = order;
  }

  /** Add one event to the user timeline. */
  observe(event: string, atUtc: Date): void {
    if (!event || event.trim().length === 0) throw new Error("event required");
    this.history.push({ event, atUtc });

    // Build n-gram contexts up to `order`.
    //   for (var k = 1; k <= _order && _history.Count > k; k++)
    for (let k = 1; k <= this.order && this.history.length > k; k++) {
      const contextStart = this.history.length - 1 - k;
      if (contextStart < 0) break;
      const contextItems = this.history.slice(contextStart, contextStart + k).map((e) => e.event);
      const key = contextItems.join("|");
      let bucket = this.transitions.get(key);
      if (bucket === undefined) {
        bucket = new Map<string, number>();
        this.transitions.set(key, bucket);
      }
      bucket.set(event, (bucket.get(event) ?? 0) + 1);
    }

    // Track inter-arrival time when the immediately preceding event repeats.
    if (this.history.length >= 2) {
      const last = this.history[this.history.length - 2];
      if (last.event === event) {
        const gap = (atUtc.getTime() - last.atUtc.getTime()) / 1000; // TotalSeconds
        const prev = this.interArrivals.get(event);
        if (prev === undefined) {
          this.interArrivals.set(event, { count: 1, sumSeconds: gap });
        } else {
          this.interArrivals.set(event, {
            count: prev.count + 1,
            sumSeconds: prev.sumSeconds + gap,
          });
        }
      }
    }
  }

  async anticipateAsync(horizonMinutes: number): Promise<readonly AnticipatedNeed[]> {
    if (horizonMinutes <= 0) throw new Error("horizonMinutes out of range");

    const snapshot = this.history.slice();
    if (snapshot.length === 0) return [];

    // Most-recent `order` events as the prediction context.
    const contextLen = Math.min(this.order, snapshot.length);
    const context = snapshot.slice(snapshot.length - contextLen).map((e) => e.event);

    // Back-off: longest context first, weighting longer contexts higher.
    const totalScore = new Map<string, number>();
    for (let k = context.length; k >= 1; k--) {
      const key = context.slice(context.length - k).join("|");
      const bucket = this.transitions.get(key);
      if (bucket === undefined) continue;
      let totalForCtx = 0;
      for (const v of bucket.values()) totalForCtx += v;
      if (totalForCtx === 0) continue;
      const weight = Math.pow(2, k);
      for (const [next, count] of bucket) {
        const prob = count / totalForCtx;
        totalScore.set(next, (totalScore.get(next) ?? 0) + weight * prob);
      }
    }

    if (totalScore.size === 0) return [];

    let totalWeight = 0;
    for (const v of totalScore.values()) totalWeight += v;
    const horizonSec = horizonMinutes * 60.0;
    const now = new Date();

    // OrderByDescending(kv => kv.Value): stable → insertion order among ties.
    const ordered = stableSortByDesc([...totalScore.entries()], (kv) => kv[1]);
    const anticipated: AnticipatedNeed[] = [];
    for (const [ev, raw] of ordered) {
      const prob = raw / totalWeight;
      if (prob <= 0) continue;
      // Mean inter-arrival to estimate when it'll happen; default half-horizon
      // when the event has no recorded repeat interval.
      const ia = this.interArrivals.get(ev);
      const meanInterval = ia && ia.count > 0 ? ia.sumSeconds / ia.count : horizonSec * 0.5;
      if (meanInterval > horizonSec) continue; // not expected within window
      anticipated.push({
        description: ev,
        expectedByUtc: new Date(now.getTime() + meanInterval * 1000),
        probability: prob,
      });
    }
    return anticipated;
  }
}

/**
 * Descending sort by a numeric key that reproduces LINQ OrderByDescending's
 * stability: equal keys keep their original relative order. Array.prototype
 * .sort is not guaranteed stable across every engine historically, but V8
 * (Node) is stable; we still implement via decorate-sort-undecorate on the
 * index to be explicit and engine-independent.
 */
function stableSortByDesc<T>(items: T[], key: (t: T) => number): T[] {
  return items
    .map((item, index) => ({ item, index }))
    .sort((a, b) => {
      const ka = key(a.item);
      const kb = key(b.item);
      if (kb > ka) return 1;
      if (kb < ka) return -1;
      return a.index - b.index; // stable tie-break
    })
    .map((x) => x.item);
}
