// creative/index.ts
// Full-parity port of CircleAI.Creative (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Creative vertical: works, inspirations,
// critiques, and an average-critique-score rollup. Plus the static
// CreativeDomainContext.
//
// NOTE: The C# CreativeCompanionAdapter (an ICompanionSession LLM-prompt wrapper)
// is intentionally NOT ported — consistent with the sibling domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   IReadOnlyList<string> Tags       → readonly string[]
//   int Score / limit                → number
//   DateTimeOffset CreatedUtc/SeenUtc → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   WorksByTag        — works whose Tags contain the tag (ordinal case-insensitive).
//   RecentInspiration — all inspiration, SeenUtc descending, take limit.
//   AvgScore          — mean of the work's critique Scores, or 0 when none
//                       (C# DefaultIfEmpty(0).Average()).

/** A creative work. Mirrors C# `CreativeWork` record. */
export interface CreativeWork {
  readonly workId: string;
  readonly title: string;
  readonly medium: string;
  readonly author: string;
  /** UTC instant the work was created (C# `DateTimeOffset CreatedUtc`). */
  readonly createdUtc: Date;
  readonly tags: readonly string[];
}

/** Constructs a {@link CreativeWork}. */
export function creativeWork(
  workId: string,
  title: string,
  medium: string,
  author: string,
  createdUtc: Date,
  tags: readonly string[],
): CreativeWork {
  return { workId, title, medium, author, createdUtc, tags };
}

/** A captured inspiration. Mirrors C# `Inspiration` record. */
export interface Inspiration {
  readonly inspirationId: string;
  readonly promptText: string;
  readonly sourceUrl: string;
  /** UTC instant the inspiration was seen (C# `DateTimeOffset SeenUtc`). */
  readonly seenUtc: Date;
}

/** Constructs an {@link Inspiration}. */
export function inspiration(
  inspirationId: string,
  promptText: string,
  sourceUrl: string,
  seenUtc: Date,
): Inspiration {
  return { inspirationId, promptText, sourceUrl, seenUtc };
}

/** A critique of a work. Mirrors C# `Critique` record. */
export interface Critique {
  readonly critiqueId: string;
  readonly workId: string;
  readonly reviewer: string;
  readonly body: string;
  readonly score: number;
}

/** Constructs a {@link Critique}. */
export function critique(critiqueId: string, workId: string, reviewer: string, body: string, score: number): Critique {
  return { critiqueId, workId, reviewer, body, score };
}

/** The creative board contract. Mirrors C# `ICreativeBoard`. */
export interface ICreativeBoard {
  addWork(w: CreativeWork): void;
  getWork(id: string): CreativeWork | undefined;
  worksByTag(tag: string): readonly CreativeWork[];
  recordInspiration(i: Inspiration): void;
  recentInspiration(limit?: number): readonly Inspiration[];
  addCritique(c: Critique): void;
  avgScore(workId: string): number;
}

/** Deterministic in-memory {@link ICreativeBoard}. */
export class InMemoryCreativeBoard implements ICreativeBoard {
  private readonly works = new Map<string, CreativeWork>();
  private readonly inspirations: Inspiration[] = [];
  private readonly critiques: Critique[] = [];

  addWork(w: CreativeWork): void {
    if (w == null) throw new Error("w required");
    this.works.set(w.workId, w);
  }

  getWork(id: string): CreativeWork | undefined {
    return this.works.get(id);
  }

  worksByTag(tag: string): readonly CreativeWork[] {
    const needle = tag.toLowerCase();
    return [...this.works.values()].filter((w) => w.tags.some((t) => t.toLowerCase() === needle));
  }

  recordInspiration(i: Inspiration): void {
    if (i == null) throw new Error("i required");
    this.inspirations.push(i);
  }

  recentInspiration(limit = 20): readonly Inspiration[] {
    return [...this.inspirations]
      .sort((a, b) => b.seenUtc.getTime() - a.seenUtc.getTime())
      .slice(0, limit);
  }

  addCritique(c: Critique): void {
    if (c == null) throw new Error("c required");
    this.critiques.push(c);
  }

  avgScore(workId: string): number {
    const scores = this.critiques.filter((c) => c.workId === workId).map((c) => c.score);
    if (scores.length === 0) return 0; // C# DefaultIfEmpty(0).Average()
    return scores.reduce((sum, s) => sum + s, 0) / scores.length;
  }
}

/**
 * Static domain context for the Creative vertical. Mirrors C#
 * `CreativeDomainContext`.
 */
export const CreativeDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Creative] Imaginative creative arts companion. Help with storytelling, poetry, worldbuilding, visual art direction, music lyrics, creative briefs, and overcoming creative blocks. Encourage experimentation and original voice. Compliance: Copyright Act 98/1978, POPIA.",
  complianceFlags: ["Copyright_Act_98_1978", "POPIA"] as readonly string[],
  suggestedTools: ["writing_tools", "image_tools", "music_tools", "document_editor"] as readonly string[],
} as const;
