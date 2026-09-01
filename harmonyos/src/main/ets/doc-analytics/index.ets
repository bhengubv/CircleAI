/**
 * doc-analytics/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

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

/**
 * A `(documentId, views)` pair, mirroring the C# named tuple
 * `(string DocumentId, int Views)` returned by `TopDocuments`.
 */
export type DocumentViewCount = readonly [documentId: string, views: number];

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

  /** Number of distinct documents with at least one recorded view. Mirrors C# `DocumentCount`. */
  get documentCount(): number {
    return this.byDoc.size;
  }

  /** Total views recorded across every tracked document. Mirrors C# `TotalViews`. */
  get totalViews(): number {
    let sum = 0;
    for (const views of this.byDoc.values()) sum += views.length;
    return sum;
  }

  /** Drop all recorded views for a document. Returns whether anything was removed. Mirrors C# `Clear`. */
  clear(documentId: string): boolean {
    if (documentId == null || documentId.trim().length === 0) throw new Error("documentId required");
    return this.byDoc.delete(documentId);
  }

  /**
   * The most-viewed documents, highest first, capped at `topK` (default 5).
   * Mirrors C# `TopDocuments`. Ties keep first-seen (insertion) order.
   */
  topDocuments(topK = 5): readonly DocumentViewCount[] {
    if (topK <= 0) throw new Error("topK must be positive");
    return [...this.byDoc.entries()]
      .map(([docId, views]): DocumentViewCount => [docId, views.length])
      .sort((a, b) => b[1] - a[1])
      .slice(0, topK);
  }

  /** Most recent views for a document, newest first, capped at `limit` (default 20). Mirrors C# `RecentViews`. */
  recentViews(documentId: string, limit = 20): readonly DocumentView[] {
    if (documentId == null || documentId.trim().length === 0) throw new Error("documentId required");
    if (limit <= 0) throw new Error("limit must be positive");
    const views = this.byDoc.get(documentId);
    if (views === undefined) return [];
    return [...views].sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime()).slice(0, limit);
  }

  /** Sum of pages viewed across every recorded view of a document. Mirrors C# `TotalPagesViewed`. */
  totalPagesViewed(documentId: string): number {
    if (documentId == null || documentId.trim().length === 0) throw new Error("documentId required");
    const views = this.byDoc.get(documentId);
    return views === undefined ? 0 : views.reduce((sum, v) => sum + v.pagesViewed, 0);
  }

  /**
   * The viewer who spent the most cumulative time on a document, or null when
   * the document has no views. Viewers are grouped ordinally (case-sensitive),
   * first-seen order preserved on ties. Mirrors C# `MostEngagedViewer`.
   */
  mostEngagedViewer(documentId: string): string | null {
    if (documentId == null || documentId.trim().length === 0) throw new Error("documentId required");
    const views = this.byDoc.get(documentId);
    if (views === undefined || views.length === 0) return null;
    // Group by viewer, summing duration (ms; relative ordering matches TotalSeconds).
    const totals = new Map<string, number>();
    for (const v of views) totals.set(v.viewerId, (totals.get(v.viewerId) ?? 0) + v.durationMs);
    let best: { viewer: string; total: number } | undefined;
    for (const [viewer, total] of totals) {
      // Strict `>` keeps the first-seen viewer on ties (stable OrderByDescending).
      if (best === undefined || total > best.total) best = { viewer, total };
    }
    return best!.viewer;
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
