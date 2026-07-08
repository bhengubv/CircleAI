// companion/reasoning/world_model.ts
//
// Two IWorldModel implementations, ported from the C# reference:
//
//   FrequencyWorldModel  — HerJarvisRealImplementations.cs #5. Learns a raw
//                          co-occurrence tally P(outcome | observation) and
//                          returns the single most-frequent outcome.
//   BayesianWorldModel   — BayesianWorldModel.cs. An online-learning Naive
//                          Bayes classifier with Laplace smoothing; scores
//                          every seen outcome by log-posterior and softmaxes
//                          for a normalised probability.
//
// Both are in-memory and deterministic. All arithmetic in the C# is `double`,
// so no `Math.fround` is needed here — there is no `float` site in either.
// String keys are compared case-INSENSITIVELY in the C# (StringComparer
// .OrdinalIgnoreCase) for outcomes/observations/vocab; we reproduce that by
// lower-casing keys in the maps while preserving the FIRST-seen surface form
// for anything surfaced back to the caller.

import { extractObservations } from "./json_observations.js";
import type { CausalPrediction, IWorldModel } from "./contracts.js";

// A case-insensitive counter that remembers the first surface form of each key.
// Mirrors ConcurrentDictionary<string,long>(StringComparer.OrdinalIgnoreCase).
class CiCounter {
  private readonly counts = new Map<string, number>(); // key: lower-cased
  private readonly forms = new Map<string, string>(); // lower -> first surface form

  add(key: string, by = 1): void {
    const lc = key.toLowerCase();
    if (!this.forms.has(lc)) this.forms.set(lc, key);
    this.counts.set(lc, (this.counts.get(lc) ?? 0) + by);
  }

  get(key: string): number {
    return this.counts.get(key.toLowerCase()) ?? 0;
  }

  get size(): number {
    return this.counts.size;
  }

  sumValues(): number {
    let s = 0;
    for (const v of this.counts.values()) s += v;
    return s;
  }

  /** Iterate [surfaceForm, count]. */
  *entries(): IterableIterator<[string, number]> {
    for (const [lc, count] of this.counts) yield [this.forms.get(lc)!, count];
  }
}

// =====================================================================
// 5. FrequencyWorldModel — learn P(outcome|observation) from evidence.
// =====================================================================

/**
 * Learns, per observation, a tally of the outcomes seen with it; a prediction
 * sums those tallies across the scenario's observations and returns the
 * single most-frequent outcome with its share of the total.
 */
export class FrequencyWorldModel implements IWorldModel {
  // observation(lower) -> { outcome -> count }, both case-insensitive.
  private readonly counts = new Map<string, CiCounter>(); // key: lower-cased observation
  private readonly obsForms = new Map<string, string>(); // lower -> first surface form

  /** Tell the model: when these observations happen, this outcome was seen. */
  observe(observations: Iterable<string>, outcome: string): void {
    if (observations == null) throw new Error("observations required");
    if (!outcome || outcome.trim().length === 0) throw new Error("outcome required");
    for (const obs of observations) {
      const lc = obs.toLowerCase();
      if (!this.obsForms.has(lc)) this.obsForms.set(lc, obs);
      let inner = this.counts.get(lc);
      if (inner === undefined) {
        inner = new CiCounter();
        this.counts.set(lc, inner);
      }
      inner.add(outcome, 1);
    }
  }

  async predictAsync(scenarioJson: string): Promise<CausalPrediction> {
    const observations = extractObservations(scenarioJson);
    const tally = new CiCounter();
    const supporters: string[] = [];
    for (const obs of observations) {
      const inner = this.counts.get(obs.toLowerCase());
      if (inner === undefined) continue;
      supporters.push(obs);
      for (const [outcome, count] of inner.entries()) tally.add(outcome, count);
    }
    if (tally.size === 0) {
      return { outcome: "unknown", probability: 0.5, supportingFactors: supporters };
    }
    const total = tally.sumValues();
    // OrderByDescending(kv => kv.Value).First(): the highest-count outcome. On a
    // tie, C#'s OrderByDescending is a stable sort, so the first-inserted key
    // among equal counts wins; iterating in insertion order and keeping the
    // strict-greater winner reproduces that.
    let topOutcome = "";
    let topCount = -1;
    for (const [outcome, count] of tally.entries()) {
      if (count > topCount) {
        topCount = count;
        topOutcome = outcome;
      }
    }
    return { outcome: topOutcome, probability: topCount / total, supportingFactors: supporters };
  }
}

// =====================================================================
// BayesianWorldModel — online Naive Bayes with Laplace smoothing.
// =====================================================================

/**
 * A real (small but honest) probabilistic classifier. Learns P(outcome) and
 * per-outcome P(obs|outcome), then at predict time scores every seen outcome
 * by its Laplace-smoothed log-posterior and softmaxes for the reported
 * probability of the winner.
 */
export class BayesianWorldModel implements IWorldModel {
  private readonly outcomeCounts = new CiCounter();
  // outcome(lower) -> { observation -> count }
  private readonly condCounts = new Map<string, CiCounter>(); // key: lower-cased outcome
  private readonly outcomeForms = new Map<string, string>(); // lower -> first surface form
  private readonly vocab = new Set<string>(); // lower-cased observations
  private totalObservations = 0;
  private readonly alpha: number; // Laplace smoothing strength

  constructor(laplaceAlpha = 1.0) {
    if (laplaceAlpha <= 0) throw new Error("laplaceAlpha out of range");
    this.alpha = laplaceAlpha;
  }

  /** Update the model with one (observations → outcome) example. */
  observe(observations: Iterable<string>, outcome: string): void {
    if (observations == null) throw new Error("observations required");
    if (!outcome || outcome.trim().length === 0) throw new Error("outcome required");

    const lcOutcome = outcome.toLowerCase();
    if (!this.outcomeForms.has(lcOutcome)) this.outcomeForms.set(lcOutcome, outcome);
    this.outcomeCounts.add(outcome, 1);
    this.totalObservations++;

    let cond = this.condCounts.get(lcOutcome);
    if (cond === undefined) {
      cond = new CiCounter();
      this.condCounts.set(lcOutcome, cond);
    }
    for (const obs of observations) {
      if (!obs || obs.trim().length === 0) continue;
      cond.add(obs, 1);
      this.vocab.add(obs.toLowerCase());
    }
  }

  async predictAsync(scenarioJson: string): Promise<CausalPrediction> {
    const observations = extractObservations(scenarioJson);
    if (observations.length === 0 || this.outcomeCounts.size === 0) {
      return { outcome: "unknown", probability: 0.5, supportingFactors: [] };
    }

    const vocabSize = Math.max(1, this.vocab.size);
    const totalEx = Math.max(1, this.totalObservations);
    const numOutcomes = this.outcomeCounts.size;

    // Score every outcome by its log-posterior. Preserve insertion order so
    // the OrderByDescending tie-break (stable) matches the C#.
    const scored: { outcome: string; logPosterior: number }[] = [];
    for (const [outcome, outcomeCount] of this.outcomeCounts.entries()) {
      // Log P(outcome) — Laplace-smoothed prior.
      const logPrior = Math.log((outcomeCount + this.alpha) / (totalEx + this.alpha * numOutcomes));

      const cond = this.condCounts.get(outcome.toLowerCase());
      const totalForOutcome = cond ? cond.sumValues() : 0;
      let logLikelihood = 0.0;
      for (const obs of observations) {
        const n = cond ? cond.get(obs) : 0;
        const p = (n + this.alpha) / (totalForOutcome + this.alpha * vocabSize);
        logLikelihood += Math.log(p);
      }
      scored.push({ outcome, logPosterior: logPrior + logLikelihood });
    }

    // Softmax over log-posteriors for a normalised probability.
    let maxLogPost = Number.NEGATIVE_INFINITY;
    for (const s of scored) if (s.logPosterior > maxLogPost) maxLogPost = s.logPosterior;
    let expSum = 0;
    for (const s of scored) expSum += Math.exp(s.logPosterior - maxLogPost);

    // OrderByDescending(s => s.LogPosterior).First(): stable → first-inserted
    // among equal log-posteriors wins.
    let top = scored[0];
    for (const s of scored) if (s.logPosterior > top.logPosterior) top = s;

    const prob = Math.exp(top.logPosterior - maxLogPost) / expSum;
    return { outcome: top.outcome, probability: prob, supportingFactors: observations };
  }
}
