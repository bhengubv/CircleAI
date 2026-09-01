/**
 * companion/reasoning/theory_of_mind
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/reasoning/theory_of_mind.ts
//
// BeliefTrackerTheoryOfMind — HerJarvisRealImplementations.cs #10. A
// bag-of-belief inference with confidence decay: scan the interaction history
// for "<verb> <claim>" mentions, weight each by verb kind and recency decay,
// and report the accumulated belief map plus an overall confidence.
//
// Wire-format-sensitive: `likelyBeliefJson` is `JsonSerializer.Serialize` of a
// `Dictionary<string,double>`, reproduced byte-for-byte via ./stj_json.js.

import type { ITheoryOfMind, OtherMindEstimate } from "./contracts";
import { stjSerializeDoubleMap } from "./stj_json";

// Ported: new Regex(@"\b(thinks?|believes?|wants?|fears?|hopes?)\s+([^.;!?]+)",
//                    RegexOptions.IgnoreCase | RegexOptions.Compiled);
// `g` gives all matches (C# Matches()); `i` is IgnoreCase. \b, the alternation,
// \s and the negated class [^.;!?] behave identically in JS regex. The claim
// group is greedy up to the next . ; ! or ? — so a run of clauses with no such
// delimiter is captured as a single claim (verified against the C# reference).
const BELIEF_RX = /\b(thinks?|believes?|wants?|fears?|hopes?)\s+([^.;!?]+)/gi;

/**
 * A string→double accumulator keyed case-INsensitively (mirroring
 * `Dictionary<string,double>(StringComparer.OrdinalIgnoreCase)`), preserving
 * the first-inserted surface form of each key for serialisation and iterating
 * in insertion order.
 */
class CiDoubleMap {
  private readonly values = new Map<string, number>(); // key: lower-cased
  private readonly order: string[] = []; // lower-cased keys, insertion order
  private readonly forms = new Map<string, string>(); // lower -> first surface form

  add(key: string, delta: number): void {
    const lc = key.toLowerCase();
    if (!this.values.has(lc)) {
      this.values.set(lc, delta);
      this.order.push(lc);
      this.forms.set(lc, key);
    } else {
      this.values.set(lc, this.values.get(lc)! + delta);
    }
  }

  get size(): number {
    return this.order.length;
  }

  sumValues(): number {
    let s = 0;
    for (const v of this.values.values()) s += v;
    return s;
  }

  /** Insertion-ordered [surfaceForm, value] map for serialisation. */
  toSurfaceMap(): Map<string, number> {
    const m = new Map<string, number>();
    for (const lc of this.order) m.set(this.forms.get(lc)!, this.values.get(lc)!);
    return m;
  }
}

/**
 * Infers a bag of beliefs about another party from a raw interaction history.
 * Each "<verb> <claim>" mention contributes `weight × decay`, where beliefs get
 * weight 1.0, other verbs 0.7, and decay falls off with mention index.
 */
export class BeliefTrackerTheoryOfMind implements ITheoryOfMind {
  async estimateAsync(
    target: string,
    interactionHistoryJson: string,
  ): Promise<OtherMindEstimate> {
    if (!target || target.trim().length === 0) throw new Error("target required");
    if (interactionHistoryJson == null) throw new Error("interactionHistoryJson required");

    const beliefs = new CiDoubleMap();
    // Fresh lastIndex per call: the shared /g regex is stateful, so reset it.
    BELIEF_RX.lastIndex = 0;
    let idx = 0;
    let m: RegExpExecArray | null;
    while ((m = BELIEF_RX.exec(interactionHistoryJson)) !== null) {
      const verb = m[1].toLowerCase(); // ToLowerInvariant
      const claim = m[2].trim();
      const decay = 1.0 / (1.0 + idx * 0.1);
      const weight = verb.startsWith("believ") ? 1.0 : 0.7;
      const key = verb + ":" + claim;
      // Same op order as C# (weight * decay) so IEEE-754 doubles match exactly.
      beliefs.add(key, weight * decay);
      idx++;
      // Guard against a zero-width match stalling the loop (can't occur with
      // this pattern — the claim group requires >=1 char — but belt-and-braces).
      if (m.index === BELIEF_RX.lastIndex) BELIEF_RX.lastIndex++;
    }

    const json = stjSerializeDoubleMap(beliefs.toSurfaceMap());
    const conf = beliefs.size === 0 ? 0.0 : Math.min(1.0, beliefs.sumValues() / 5.0);
    return { targetIdentifier: target, likelyBeliefJson: json, confidence: conf };
  }
}
