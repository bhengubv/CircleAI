/**
 * companion/belief
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/belief.ts
// Memory integrity: attribution + belief revision. Ported from
// Circle.AI.Companion (PersonalBelief, HeuristicBeliefExtractor, SelfBeliefStore)
// — the C# reference.
//
// Every belief carries WHOSE fact it is — the user's own (Self), someone else's
// (Other), or a general fact (World). The highest-harm rule in the whole system:
// a fact about a third party ("my mother is diabetic") must never be recorded as
// a fact about the user. Only Self beliefs become user facts; a newer self-belief
// on the same predicate supersedes the older one; a correction retracts a belief.

/** Whose fact a belief is about. */
export enum Attribution {
  Self = "Self",
  Other = "Other",
  World = "World",
}

/** A single attributed belief, with provenance and confidence. */
export interface PersonalBelief {
  readonly attribution: Attribution;
  readonly subject: string;
  readonly predicate: string;
  readonly object: string;
  readonly confidence: number;
  readonly source: string | null;
  readonly recordedAtUtc: Date;
}

/** Turns a sentence into attributed beliefs. */
export interface IBeliefExtractor {
  extractAsync(text: string, source: string | null): Promise<readonly PersonalBelief[]>;
}

const RELATIONS = new Set<string>([
  "mother", "father", "mom", "mum", "dad", "sister", "brother", "wife", "husband", "son", "daughter",
  "aunt", "uncle", "grandmother", "grandfather", "granny", "grandpa", "gran", "nan", "friend",
  "colleague", "boss", "neighbour", "neighbor", "cousin", "partner", "girlfriend", "boyfriend",
]);
const POSSESSIVE = new Set<string>(["my", "her", "his", "their", "our"]);
const STOP = new Set<string>([
  "the", "a", "an", "is", "are", "was", "were", "be", "been", "am", "to", "of", "in", "on", "at", "and", "or",
  "but", "with", "has", "have", "had", "that", "this", "it", "as", "for", "really", "very", "just", "now",
]);

/**
 * Model-free belief extractor with attribution discipline. Coarse by design — the
 * model-based extractor is far more precise — but it never collapses "my mother"
 * into "me". Attribution is decided by the sentence's leading subject.
 */
export class HeuristicBeliefExtractor implements IBeliefExtractor {
  async extractAsync(text: string, source: string | null): Promise<readonly PersonalBelief[]> {
    if (!text || text.trim().length === 0) return [];

    const tokens = text
      .toLowerCase()
      .split(/[ \t\n\r.,?!;:"()]+/)
      .filter((t) => t.length > 0);
    if (tokens.length === 0) return [];

    let attribution: Attribution;
    let subject: string;
    const skip = new Set<number>(); // subject tokens, excluded from the object

    if (tokens.length >= 2 && POSSESSIVE.has(tokens[0]) && RELATIONS.has(tokens[1])) {
      // "my mother ..." → someone else
      attribution = Attribution.Other;
      subject = tokens[1];
      skip.add(0);
      skip.add(1);
    } else if (RELATIONS.has(tokens[0])) {
      attribution = Attribution.Other;
      subject = tokens[0];
      skip.add(0);
    } else if (
      tokens[0] === "i" ||
      tokens[0] === "i'm" ||
      tokens[0] === "im" ||
      tokens[0] === "me" ||
      tokens[0] === "my"
    ) {
      // "I ..." or "my <non-relation> ..." → the user
      attribution = Attribution.Self;
      subject = "user";
      skip.add(0);
    } else {
      attribution = Attribution.World;
      subject = tokens[0];
    }

    const obj = tokens
      .filter((t, i) => !skip.has(i) && t.length >= 3 && !STOP.has(t) && !RELATIONS.has(t))
      .join(" ");
    if (obj.trim().length === 0) return [];

    return [
      { attribution, subject, predicate: "isAbout", object: obj, confidence: 0.6, source, recordedAtUtc: new Date() },
    ];
  }
}

/**
 * The user's own facts, with attribution filtering, revision, and correction.
 *
 * No lock is needed (unlike the C# reference): JavaScript is single-threaded, so
 * the encoder's background drain and the session's fact reads never interleave
 * mid-operation.
 */
export class SelfBeliefStore {
  private readonly self: PersonalBelief[] = [];
  private readonly audit: PersonalBelief[] = []; // other/world — remembered, never a user fact

  /** Record a belief. Only Self beliefs become user facts; the rest are audited. */
  record(belief: PersonalBelief): void {
    if (!belief) throw new Error("belief required");
    if (belief.attribution !== Attribution.Self) {
      this.audit.push(belief);
      return;
    }
    // Supersede an existing self-belief on the same (subject, predicate): a
    // functional fact holds one current value. The prior value drops out.
    for (let i = this.self.length - 1; i >= 0; i--) {
      if (eqCi(this.self[i].subject, belief.subject) && eqCi(this.self[i].predicate, belief.predicate)) {
        this.self.splice(i, 1);
      }
    }
    this.self.push(belief);
  }

  /** The user's own current facts. */
  selfFacts(): readonly PersonalBelief[] {
    return [...this.self];
  }

  /** Beliefs remembered but never treated as user facts (audit trail). */
  nonSelf(): readonly PersonalBelief[] {
    return [...this.audit];
  }

  /** Correction ("no, that's my mother"): drop any user fact mentioning the text. */
  retract(objectContains: string): number {
    if (!objectContains || objectContains.trim().length === 0) return 0;
    const needle = objectContains.toLowerCase();
    let removed = 0;
    for (let i = this.self.length - 1; i >= 0; i--) {
      if (this.self[i].object.toLowerCase().includes(needle)) {
        this.self.splice(i, 1);
        removed++;
      }
    }
    return removed;
  }

  /** Introspection ("why do you think that?"): the source turns behind the user's facts. */
  provenance(): readonly string[] {
    const seen = new Set<string>();
    const out: string[] = [];
    for (const b of this.self) {
      if (b.source != null && !seen.has(b.source)) {
        seen.add(b.source);
        out.push(b.source);
      }
    }
    return out;
  }
}

function eqCi(a: string, b: string): boolean {
  return a.toLowerCase() === b.toLowerCase();
}
