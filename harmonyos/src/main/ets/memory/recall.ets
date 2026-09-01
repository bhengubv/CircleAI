/**
 * memory/recall
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// memory/recall.ts
// Fused associative recall (Reciprocal Rank Fusion). Ported from
// Circle.AI.Companion (IRecall, FusedRecall) — the C# reference.
//
// Fuses two memory systems with incomparable score spaces — episodic cosine
// similarity and graph association (Personalised PageRank) — into one ranked
// context. RRF combines ranked lists by *position*, so it needs no shared score
// scale: each source contributes 1 / (k + rank).
//
// Cold-start is automatic: a new user has an empty graph, so only episodic
// contributes and the fused order equals the episodic order — no special case.

import type { EpisodicMemoryEntry, IEpisodicMemoryStore } from "./index";
import type { IHippoRagStore, MemoryHit, MemoryItem } from "./graph";

/** Unified memory recall — the most relevant memories for a turn. */
export interface IRecall {
  /**
   * Recall the topK most relevant memories for the current turn. `query` drives
   * graph association; `queryEmbedding` drives episodic cosine similarity (may be
   * null → episodic recency fallback).
   */
  recallAsync(
    query: string,
    queryEmbedding: number[] | null,
    topK?: number,
  ): Promise<readonly MemoryHit[]>;
}

/** Tuning for {@link FusedRecall}. */
export interface FusedRecallOptions {
  /** Candidates pulled from each source before fusion. Default 20. */
  readonly candidatePoolSize?: number;
  /** RRF damping constant k. Default 60 (the standard value). */
  readonly rrfK?: number;
  /**
   * Graph hits whose backing confidence (metadata key "confidence") is below this
   * are dropped. Applied only when a hit actually carries a confidence value.
   * Default 0.4.
   */
  readonly graphConfidenceThreshold?: number;
}

const DEFAULTS: Required<FusedRecallOptions> = {
  candidatePoolSize: 20,
  rrfK: 60,
  graphConfidenceThreshold: 0.4,
};

/** Reciprocal-Rank-Fusion recall over episodic similarity + graph association. */
export class FusedRecall implements IRecall {
  private readonly episodic: IEpisodicMemoryStore;
  private readonly graph: IHippoRagStore | null;
  private readonly opts: Required<FusedRecallOptions>;

  constructor(
    episodic: IEpisodicMemoryStore,
    graph: IHippoRagStore | null = null,
    options?: FusedRecallOptions,
  ) {
    if (!episodic) throw new Error("episodic required");
    this.episodic = episodic;
    this.graph = graph;
    this.opts = { ...DEFAULTS, ...options };
  }

  async recallAsync(
    query: string,
    queryEmbedding: number[] | null,
    topK = 5,
  ): Promise<readonly MemoryHit[]> {
    if (topK <= 0) throw new RangeError("topK must be positive");

    const pool = this.opts.candidatePoolSize;

    // Fast path: episodic similarity (or recency when the embedding is null).
    const episodic = await this.episodic.searchAsync(queryEmbedding, pool);

    // Slow path: graph association. Optional and best-effort — a missing, empty,
    // or failing graph degrades to pure episodic, never throws. An empty query
    // cannot seed a graph walk, so skip it.
    let graph: readonly MemoryHit[] = [];
    if (this.graph && query != null && query.trim().length > 0) {
      try {
        graph = await this.graph.multiHopRecallAsync(query, pool);
      } catch {
        graph = [];
      }
    }

    // Reciprocal Rank Fusion: accumulate 1 / (k + rank) per candidate across both
    // ranked lists, keyed by normalised text so a memory surfaced by both sources
    // reinforces rather than duplicates.
    const k = this.opts.rrfK;
    const fused = new Map<string, { item: MemoryItem; score: number }>();

    const accumulate = (item: MemoryItem, oneBasedRank: number): void => {
      const key = normaliseKey(item.text);
      if (key.length === 0) return;
      const contribution = 1 / (k + oneBasedRank);
      const existing = fused.get(key);
      if (existing) existing.score += contribution;
      else fused.set(key, { item, score: contribution });
    };

    for (let i = 0; i < episodic.length; i++) accumulate(adaptEpisodic(episodic[i]), i + 1);

    for (let i = 0; i < graph.length; i++) {
      if (isBelowConfidence(graph[i], this.opts.graphConfidenceThreshold)) continue;
      accumulate(graph[i].item, i + 1);
    }

    return [...fused.values()]
      .sort((a, b) => b.score - a.score)
      .slice(0, topK)
      .map((c) => ({ item: c.item, score: c.score }));
  }
}

function isBelowConfidence(hit: MemoryHit, threshold: number): boolean {
  const meta = hit.item.metadata;
  if (!meta) return false;
  const raw = meta["confidence"];
  if (raw === undefined) return false;
  const c = Number.parseFloat(raw);
  return Number.isFinite(c) && c < threshold;
}

function adaptEpisodic(e: EpisodicMemoryEntry): MemoryItem {
  const meta: Map<string, string> = {
    source: "episodic",
    recordedAt: e.recordedAtUtc.toISOString(),
  };
  if (e.assistantText) meta["assistantText"] = e.assistantText;
  if (e.appContext) meta["appContext"] = e.appContext;
  return { id: e.id, text: e.userText, metadata: meta };
}

/** Lowercase + collapse internal whitespace so equivalent texts fuse to one key. */
function normaliseKey(text: string | null | undefined): string {
  if (!text || text.trim().length === 0) return "";
  let out = "";
  let prevSpace = false;
  for (const ch of text.trim()) {
    if (/\s/.test(ch)) {
      if (!prevSpace) {
        out += " ";
        prevSpace = true;
      }
    } else {
      out += ch.toLowerCase();
      prevSpace = false;
    }
  }
  return out;
}
