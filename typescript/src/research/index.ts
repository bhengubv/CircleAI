// research/index.ts
//
// Full-parity port of CircleAI.Research (C#). C# is the exact spec.
//
// Research corpora contracts: IResearchCorpus / IPaperRetrieval / ICitationGraph,
// the ResearchPaper / Citation records, deterministic in-memory implementations,
// and the fail-safe Null* defaults.
//
// Type mappings (C# → TS):
//   record                                → readonly interface (+ positional factory)
//   IReadOnlyList<string> Authors         → readonly string[]
//   DateTimeOffset PublishedAtUtc         → Date
//   string? Doi                           → string | null
//   ReadOnlyMemory<byte>?                 → Uint8Array | null
//   ValueTask<T>                          → Promise<T>
//   ConcurrentDictionary (Ordinal)        → Map<string, T>
//
// SEMANTICS PARITY:
//   SearchAsync — substring scoring (title +3, abstract +1, author +1), score>0,
//                 descending score, take topK.
//   Citation graph — plain adjacency lists (forward keyed on From, backward on To).

// ─────────────────────────────────────────────────────────────────────────────
// Records
// ─────────────────────────────────────────────────────────────────────────────

/** A research paper. Mirrors C# `ResearchPaper` record. */
export interface ResearchPaper {
  readonly paperId: string;
  readonly title: string;
  readonly authors: readonly string[];
  readonly abstract: string;
  /** UTC instant the paper was published (C# `DateTimeOffset PublishedAtUtc`). */
  readonly publishedAtUtc: Date;
  readonly doi: string | null;
}

/** Constructs a {@link ResearchPaper}. */
export function researchPaper(
  paperId: string,
  title: string,
  authors: readonly string[],
  abstract: string,
  publishedAtUtc: Date,
  doi: string | null,
): ResearchPaper {
  return { paperId, title, authors, abstract, publishedAtUtc, doi };
}

/** A citation edge between two papers. Mirrors C# `Citation` record. */
export interface Citation {
  readonly fromPaperId: string;
  readonly toPaperId: string;
  readonly context: string;
}

/** Constructs a {@link Citation}. */
export function citation(fromPaperId: string, toPaperId: string, context: string): Citation {
  return { fromPaperId, toPaperId, context };
}

// ─────────────────────────────────────────────────────────────────────────────
// Contracts
// ─────────────────────────────────────────────────────────────────────────────

/** Research corpus contract. Mirrors C# `IResearchCorpus`. */
export interface IResearchCorpus {
  readonly backendId: string;
  getAsync(paperId: string): Promise<ResearchPaper | null>;
  searchAsync(query: string, topK?: number): Promise<readonly ResearchPaper[]>;
}

/** Full-text retrieval contract. Mirrors C# `IPaperRetrieval`. */
export interface IPaperRetrieval {
  readonly backendId: string;
  fetchFullTextAsync(paperId: string): Promise<Uint8Array | null>;
}

/** Citation-graph contract. Mirrors C# `ICitationGraph`. */
export interface ICitationGraph {
  readonly backendId: string;
  forwardCitationsAsync(paperId: string): Promise<readonly Citation[]>;
  backwardCitationsAsync(paperId: string): Promise<readonly Citation[]>;
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory implementations
// ─────────────────────────────────────────────────────────────────────────────

/** Deterministic in-memory {@link IResearchCorpus}. */
export class InMemoryResearchCorpus implements IResearchCorpus {
  private readonly papers = new Map<string, ResearchPaper>();

  get backendId(): string {
    return "in-memory";
  }

  add(paper: ResearchPaper): void {
    if (paper == null) throw new Error("paper required");
    this.papers.set(paper.paperId, paper);
  }

  async getAsync(paperId: string): Promise<ResearchPaper | null> {
    if (paperId == null || paperId.trim().length === 0) throw new Error("paperId required");
    return this.papers.get(paperId) ?? null;
  }

  async searchAsync(query: string, topK = 10): Promise<readonly ResearchPaper[]> {
    if (query == null) throw new Error("query required");
    if (topK <= 0) throw new Error("topK out of range");
    return [...this.papers.values()]
      .map((p) => ({ p, score: InMemoryResearchCorpus.score(p, query) }))
      .filter((x) => x.score > 0)
      .sort((a, b) => b.score - a.score)
      .slice(0, topK)
      .map((x) => x.p);
  }

  private static score(p: ResearchPaper, q: string): number {
    const ql = q.toLowerCase();
    let s = 0;
    if (p.title != null && p.title.toLowerCase().includes(ql)) s += 3;
    if (p.abstract != null && p.abstract.toLowerCase().includes(ql)) s += 1;
    if (p.authors != null && p.authors.some((a) => a.toLowerCase().includes(ql))) s += 1;
    return s;
  }
}

/** Deterministic in-memory {@link IPaperRetrieval}. */
export class InMemoryPaperRetrieval implements IPaperRetrieval {
  private readonly texts = new Map<string, Uint8Array>();

  get backendId(): string {
    return "in-memory";
  }

  add(paperId: string, fullText: Uint8Array): void {
    if (paperId == null || paperId.trim().length === 0) throw new Error("paperId required");
    this.texts.set(paperId, fullText);
  }

  async fetchFullTextAsync(paperId: string): Promise<Uint8Array | null> {
    if (paperId == null || paperId.trim().length === 0) throw new Error("paperId required");
    return this.texts.get(paperId) ?? null;
  }
}

/** Deterministic in-memory {@link ICitationGraph} (adjacency lists). */
export class InMemoryCitationGraph implements ICitationGraph {
  private readonly forward = new Map<string, Citation[]>();
  private readonly backward = new Map<string, Citation[]>();

  get backendId(): string {
    return "in-memory";
  }

  link(c: Citation): void {
    if (c == null) throw new Error("c required");
    const f = this.forward.get(c.fromPaperId) ?? [];
    f.push(c);
    this.forward.set(c.fromPaperId, f);
    const b = this.backward.get(c.toPaperId) ?? [];
    b.push(c);
    this.backward.set(c.toPaperId, b);
  }

  async forwardCitationsAsync(paperId: string): Promise<readonly Citation[]> {
    if (paperId == null || paperId.trim().length === 0) throw new Error("paperId required");
    const l = this.forward.get(paperId);
    return l === undefined ? [] : [...l];
  }

  async backwardCitationsAsync(paperId: string): Promise<readonly Citation[]> {
    if (paperId == null || paperId.trim().length === 0) throw new Error("paperId required");
    const l = this.backward.get(paperId);
    return l === undefined ? [] : [...l];
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null* defaults
// ─────────────────────────────────────────────────────────────────────────────

/** Fail-safe {@link IResearchCorpus} — returns nothing. */
export class NullResearchCorpus implements IResearchCorpus {
  static readonly instance = new NullResearchCorpus();
  get backendId(): string {
    return "null";
  }
  async getAsync(): Promise<ResearchPaper | null> {
    return null;
  }
  async searchAsync(): Promise<readonly ResearchPaper[]> {
    return [];
  }
}

/** Fail-safe {@link IPaperRetrieval} — returns nothing. */
export class NullPaperRetrieval implements IPaperRetrieval {
  static readonly instance = new NullPaperRetrieval();
  get backendId(): string {
    return "null";
  }
  async fetchFullTextAsync(): Promise<Uint8Array | null> {
    return null;
  }
}

/** Fail-safe {@link ICitationGraph} — returns nothing. */
export class NullCitationGraph implements ICitationGraph {
  static readonly instance = new NullCitationGraph();
  get backendId(): string {
    return "null";
  }
  async forwardCitationsAsync(): Promise<readonly Citation[]> {
    return [];
  }
  async backwardCitationsAsync(): Promise<readonly Citation[]> {
    return [];
  }
}
