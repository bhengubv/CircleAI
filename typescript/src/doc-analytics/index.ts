// doc-analytics/index.ts
//
// Full-parity port of CircleAI.DocAnalytics (C#). C# is the exact spec.
//
// Document-analytics contracts: IDocumentTracker / IDocumentInsights, the
// DocumentView / DocumentInsight records, a deterministic in-memory tracker
// (implements both contracts), and the Null* defaults.
//
// Type mappings (C# → TS):
//   record                                → readonly interface (+ positional factory)
//   DateTimeOffset AtUtc                  → Date
//   TimeSpan Duration                     → number (milliseconds)
//   double AvgDurationSeconds             → number
//   DocumentInsight?                      → DocumentInsight | null
//   ValueTask<T>                          → Promise<T>

// ─────────────────────────────────────────────────────────────────────────────
// Records
// ─────────────────────────────────────────────────────────────────────────────

/** One recorded document view. Mirrors C# `DocumentView`. */
export interface DocumentView {
  readonly documentId: string;
  readonly viewerId: string;
  /** UTC instant of the view (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
  /** View duration in milliseconds (C# `TimeSpan Duration`). */
  readonly durationMs: number;
  readonly pagesViewed: number;
}

/** Constructs a {@link DocumentView}. */
export function documentView(
  documentId: string,
  viewerId: string,
  atUtc: Date,
  durationMs: number,
  pagesViewed: number,
): DocumentView {
  return { documentId, viewerId, atUtc, durationMs, pagesViewed };
}

/** Computed insight over a document's views. Mirrors C# `DocumentInsight`. */
export interface DocumentInsight {
  readonly documentId: string;
  readonly totalViews: number;
  readonly uniqueViewers: number;
  readonly avgDurationSeconds: number;
}

/** Constructs a {@link DocumentInsight}. */
export function documentInsight(
  documentId: string,
  totalViews: number,
  uniqueViewers: number,
  avgDurationSeconds: number,
): DocumentInsight {
  return { documentId, totalViews, uniqueViewers, avgDurationSeconds };
}

// ─────────────────────────────────────────────────────────────────────────────
// Contracts
// ─────────────────────────────────────────────────────────────────────────────

/** Document tracker. Mirrors C# `IDocumentTracker`. */
export interface IDocumentTracker {
  readonly backendId: string;
  recordViewAsync(view: DocumentView): Promise<void>;
  listViewsAsync(documentId: string): Promise<readonly DocumentView[]>;
}

/** Document insights. Mirrors C# `IDocumentInsights`. */
export interface IDocumentInsights {
  readonly backendId: string;
  computeAsync(documentId: string): Promise<DocumentInsight | null>;
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory implementation (both contracts)
// ─────────────────────────────────────────────────────────────────────────────

/** Thread-safe in-memory document tracker + insights. Mirrors C# `InMemoryDocumentTracker`. */
export class InMemoryDocumentTracker implements IDocumentTracker, IDocumentInsights {
  private readonly byDoc = new Map<string, DocumentView[]>();

  get backendId(): string {
    return "in-memory";
  }

  async recordViewAsync(view: DocumentView): Promise<void> {
    if (view == null) throw new Error("view required");
    if (view.documentId == null || view.documentId.trim().length === 0) throw new Error("DocumentId required");
    const list = this.byDoc.get(view.documentId) ?? [];
    list.push(view);
    this.byDoc.set(view.documentId, list);
  }

  async listViewsAsync(documentId: string): Promise<readonly DocumentView[]> {
    if (documentId == null || documentId.trim().length === 0) throw new Error("documentId required");
    const views = this.byDoc.get(documentId);
    return views === undefined ? [] : [...views];
  }

  async computeAsync(documentId: string): Promise<DocumentInsight | null> {
    if (documentId == null || documentId.trim().length === 0) throw new Error("documentId required");
    const views = this.byDoc.get(documentId);
    if (views === undefined || views.length === 0) return null;

    const total = views.length;
    const unique = new Set(views.map((v) => v.viewerId)).size;
    // Duration is stored in ms; the C# insight averages TotalSeconds.
    const avgSeconds = views.reduce((acc, v) => acc + v.durationMs / 1000, 0) / total;

    return documentInsight(documentId, total, unique, avgSeconds);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null* defaults
// ─────────────────────────────────────────────────────────────────────────────

/** Fail-safe {@link IDocumentTracker}. */
export class NullDocumentTracker implements IDocumentTracker {
  static readonly instance = new NullDocumentTracker();
  get backendId(): string {
    return "null";
  }
  async recordViewAsync(): Promise<void> {
    /* no-op */
  }
  async listViewsAsync(): Promise<readonly DocumentView[]> {
    return [];
  }
}

/** Fail-safe {@link IDocumentInsights}. */
export class NullDocumentInsights implements IDocumentInsights {
  static readonly instance = new NullDocumentInsights();
  get backendId(): string {
    return "null";
  }
  async computeAsync(): Promise<DocumentInsight | null> {
    return null;
  }
}
