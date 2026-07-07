// memory/stores.ts
// Concrete in-memory stores for the memory-brain. Ported from
// Circle.AI.Memory (InMemoryEpisodicStore) — the C# reference.
//
// All data is lost when the process exits; a persistent (SQLite) backend is a
// later slice. The algorithms (cosine similarity, recency fallback, FIFO cap)
// are identical to the reference.

import type { EpisodicMemoryEntry, IEpisodicMemoryStore } from "./index.js";

/**
 * In-memory {@link IEpisodicMemoryStore}. Capacity is capped (FIFO eviction) to
 * prevent unbounded growth on long-running processes.
 */
export class InMemoryEpisodicStore implements IEpisodicMemoryStore {
  private readonly maxEntries: number;
  private readonly entries: EpisodicMemoryEntry[] = [];

  /**
   * @param maxEntries Cap on stored entries; when exceeded the oldest are
   *   evicted (FIFO). Default 1000.
   */
  constructor(maxEntries = 1000) {
    if (maxEntries <= 0) throw new RangeError("maxEntries must be positive");
    this.maxEntries = maxEntries;
  }

  async addAsync(entry: EpisodicMemoryEntry): Promise<void> {
    if (!entry) throw new Error("entry required");
    this.entries.push(entry);
    while (this.entries.length > this.maxEntries) this.entries.shift();
  }

  async searchAsync(
    queryEmbedding: number[] | null,
    topK = 5,
  ): Promise<readonly EpisodicMemoryEntry[]> {
    const snapshot = [...this.entries];

    if (queryEmbedding == null || queryEmbedding.length === 0) {
      // No embedding — return most recent.
      return snapshot
        .sort((a, b) => b.recordedAtUtc.getTime() - a.recordedAtUtc.getTime())
        .slice(0, topK);
    }

    // Cosine similarity, only against entries whose embedding matches the query
    // dimension. Both vectors are L2-normalised, so cosine == dot product.
    return snapshot
      .filter((e) => e.embedding != null && e.embedding.length === queryEmbedding.length)
      .map((e) => ({ entry: e, score: cosineSimilarity(queryEmbedding, e.embedding!) }))
      .sort((a, b) => b.score - a.score)
      .slice(0, topK)
      .map((x) => x.entry);
  }

  async getRecentAsync(count = 10): Promise<readonly EpisodicMemoryEntry[]> {
    return [...this.entries]
      .sort((a, b) => b.recordedAtUtc.getTime() - a.recordedAtUtc.getTime())
      .slice(0, count);
  }

  async countAsync(): Promise<number> {
    return this.entries.length;
  }

  async pruneOlderThanAsync(cutoff: Date): Promise<number> {
    const before = this.entries.length;
    const cutoffMs = cutoff.getTime();
    for (let i = this.entries.length - 1; i >= 0; i--) {
      if (this.entries[i].recordedAtUtc.getTime() < cutoffMs) this.entries.splice(i, 1);
    }
    return before - this.entries.length;
  }
}

/** Cosine similarity of two equal-length, L2-normalised vectors (== dot product). */
function cosineSimilarity(a: number[], b: number[]): number {
  let dot = 0;
  for (let i = 0; i < a.length; i++) dot += a[i] * b[i];
  return dot;
}
