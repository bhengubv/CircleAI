/**
 * creative/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

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
  /** Number of works catalogued. */
  readonly workCount: number;
  /** Remove a work by id, cascading its critiques. Returns whether one was removed. */
  removeWork(workId: string): boolean;
  /** Works by a given author (case-insensitive), newest first. */
  worksByAuthor(author: string): readonly CreativeWork[];
  /** Works in a given medium (case-insensitive), newest first. */
  worksByMedium(medium: string): readonly CreativeWork[];
  /** The work with the highest average critique score whose record still exists, or undefined. */
  topRatedWork(): CreativeWork | undefined;
  /** All distinct tags across works (case-insensitive), alphabetically ordered. */
  allTags(): readonly string[];
}

/**
 * Ordinal-case-insensitive compare, matching C# `StringComparer.OrdinalIgnoreCase`
 * ordering. Ties fall back to the ordinal comparison of the originals.
 */
function ordinalIgnoreCaseCompare(a: string, b: string): number {
  const la = a.toUpperCase();
  const lb = b.toUpperCase();
  if (la < lb) return -1;
  if (la > lb) return 1;
  return a < b ? -1 : a > b ? 1 : 0;
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

  /** Number of works catalogued. Mirrors C# `WorkCount`. */
  get workCount(): number {
    return this.works.size;
  }

  /**
   * Remove a work by id, cascading (deleting) its critiques. Returns whether a
   * work was removed. Mirrors C# `RemoveWork`.
   */
  removeWork(workId: string): boolean {
    const removed = this.works.delete(workId);
    if (removed) {
      for (let i = this.critiques.length - 1; i >= 0; i--) {
        if (this.critiques[i].workId === workId) this.critiques.splice(i, 1);
      }
    }
    return removed;
  }

  /** Works by a given author (case-insensitive), newest first. Mirrors C# `WorksByAuthor`. */
  worksByAuthor(author: string): readonly CreativeWork[] {
    const target = author.toLowerCase();
    return [...this.works.values()]
      .filter((w) => w.author.toLowerCase() === target)
      .sort((a, b) => b.createdUtc.getTime() - a.createdUtc.getTime());
  }

  /** Works in a given medium (case-insensitive), newest first. Mirrors C# `WorksByMedium`. */
  worksByMedium(medium: string): readonly CreativeWork[] {
    const target = medium.toLowerCase();
    return [...this.works.values()]
      .filter((w) => w.medium.toLowerCase() === target)
      .sort((a, b) => b.createdUtc.getTime() - a.createdUtc.getTime());
  }

  /**
   * The work with the highest average critique score whose record still exists,
   * or undefined. Critiques are grouped by work id (ordinal); ties keep
   * first-seen order. Mirrors C# `TopRatedWork`.
   */
  topRatedWork(): CreativeWork | undefined {
    // Group critiques by work id (ordinal), preserving first-seen order.
    const groups = new Map<string, { workId: string; sum: number; n: number }>();
    for (const c of this.critiques) {
      const g = groups.get(c.workId);
      if (g === undefined) groups.set(c.workId, { workId: c.workId, sum: c.score, n: 1 });
      else {
        g.sum += c.score;
        g.n += 1;
      }
    }
    // Highest average first, stable on ties.
    const ranked = [...groups.values()].sort((a, b) => b.sum / b.n - a.sum / a.n);
    for (const g of ranked) {
      const w = this.works.get(g.workId);
      if (w !== undefined) return w;
    }
    return undefined;
  }

  /**
   * All distinct tags across works (case-insensitive; first-seen casing wins),
   * ordered alphabetically (OrdinalIgnoreCase). Mirrors C# `AllTags`.
   */
  allTags(): readonly string[] {
    const seen = new Map<string, string>();
    for (const w of this.works.values()) {
      for (const t of w.tags) {
        const key = t.toLowerCase();
        if (!seen.has(key)) seen.set(key, t);
      }
    }
    return [...seen.values()].sort(ordinalIgnoreCaseCompare);
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
