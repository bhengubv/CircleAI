/**
 * memory/stores
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// memory/stores.ts
// Concrete in-memory stores for the memory-brain. Ported from
// Circle.AI.Memory (InMemoryEpisodicStore) — the C# reference.
//
// All data is lost when the process exits; a persistent (SQLite) backend is a
// later slice. The algorithms (cosine similarity, recency fallback, FIFO cap)
// are identical to the reference.

import type {
  EpisodicMemoryEntry,
  FeedbackSignal,
  Goal,
  IEpisodicMemoryStore,
  IFeedbackStore,
  IGoalStore,
  IPersonaStore,
} from "./index";
import { FeedbackPolarity, GoalStatus, PersonaState } from "./index";

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

/**
 * In-memory {@link IPersonaStore}. Keyed by userId; {@link loadAsync} returns a
 * fresh default {@link PersonaState} (stamped with the requested userId) when no
 * persona has been persisted for that user.
 */
export class InMemoryPersonaStore implements IPersonaStore {
  private readonly store = new Map<string, PersonaState>();

  async loadAsync(userId: string): Promise<PersonaState> {
    const existing = this.store.get(userId);
    if (existing) return existing;
    const fresh = new PersonaState();
    fresh.userId = userId;
    return fresh;
  }

  async saveAsync(persona: PersonaState): Promise<void> {
    if (!persona) throw new Error("persona required");
    this.store.set(persona.userId, persona);
  }
}

/**
 * In-memory {@link IFeedbackStore}. Ported from Circle.AI.Memory
 * (InMemoryFeedbackStore) — the C# reference. Data is lost on process exit;
 * for tests and headless CLI use. Capacity is capped (FIFO eviction).
 */
export class InMemoryFeedbackStore implements IFeedbackStore {
  private readonly maxSignals: number;
  private readonly signals: FeedbackSignal[] = [];

  /**
   * @param maxSignals Cap on stored signals; when exceeded the oldest are
   *   evicted (FIFO). Default 10000.
   */
  constructor(maxSignals = 10_000) {
    if (maxSignals <= 0) throw new RangeError("maxSignals must be positive");
    this.maxSignals = maxSignals;
  }

  async addAsync(signal: FeedbackSignal): Promise<void> {
    if (!signal) throw new Error("signal required");
    this.signals.push(signal);
    while (this.signals.length > this.maxSignals) this.signals.shift();
  }

  async getRecentAsync(count = 50): Promise<readonly FeedbackSignal[]> {
    return [...this.signals]
      .sort((a, b) => b.recordedAtUtc.getTime() - a.recordedAtUtc.getTime())
      .slice(0, count);
  }

  async countAsync(): Promise<number> {
    return this.signals.length;
  }

  async positiveRatioAsync(): Promise<number | null> {
    if (this.signals.length === 0) return null;
    const pos = this.signals.filter((s) => s.polarity === FeedbackPolarity.Positive).length;
    return pos / this.signals.length;
  }
}

/**
 * In-memory {@link IGoalStore}. Ported from Circle.AI.Memory
 * (InMemoryGoalStore) — the C# reference. Keyed by goal id (the natural key).
 * All data is lost when the process exits; use a persistent backend for
 * durability.
 */
export class InMemoryGoalStore implements IGoalStore {
  private readonly goals = new Map<string, Goal>();

  async listAsync(userId: string): Promise<readonly Goal[]> {
    if (!userId || userId.trim().length === 0) throw new Error("userId required");
    return [...this.goals.values()].filter((g) => g.userId === userId);
  }

  async getAsync(id: string): Promise<Goal | null> {
    if (!id || id.trim().length === 0) throw new Error("id required");
    return this.goals.get(id) ?? null;
  }

  async upsertAsync(goal: Goal): Promise<Goal> {
    if (!goal) throw new Error("goal required");
    this.goals.set(goal.id, goal);
    return goal;
  }

  async deleteAsync(id: string): Promise<void> {
    if (!id || id.trim().length === 0) throw new Error("id required");
    this.goals.delete(id);
  }

  async getActiveAsync(userId: string): Promise<readonly Goal[]> {
    if (!userId || userId.trim().length === 0) throw new Error("userId required");
    return [...this.goals.values()].filter(
      (g) => g.userId === userId && g.status === GoalStatus.Active,
    );
  }
}
