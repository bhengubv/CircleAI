// memory/consolidation.ts
// Hierarchical memory consolidation — the "sleep cycle" engine. Ported from
// CircleAI.Memory.Consolidation (C#): SleepKind, CoreMemory, DailyMemorySummary,
// SemanticMemoryCluster, PersonaDeltaSnapshot, the four tier stores, the
// HeuristicSummarizer, and the MemoryConsolidator orchestration engine.
//
// Promotes episodic → daily → weekly (semantic) → monthly (persona delta) →
// core, and enforces retention. All time decisions go through an injectable
// clock so tests are deterministic. This is the in-memory port: identical
// algorithms and formulas to the C# reference, no persistence.
//
// C# `DateOnly` is represented here as a "YYYY-MM-DD" UTC string. ISO date
// strings compare correctly with `<`/`<=`/`>=`, so the range/idempotency/prune
// comparisons carry over unchanged.

import type { EpisodicMemoryEntry, IEpisodicMemoryStore, IPersonaStore } from "./index.js";
import { PersonaState } from "./index.js";

// ─────────────────────────────────────────────────────────────────────────────
// SleepKind + CoreMemoryKind
// ─────────────────────────────────────────────────────────────────────────────

/** Which tier of hierarchical consolidation a tick should run. */
export enum SleepKind {
  /** End-of-day: collapse the day's episodic entries into a DailyMemorySummary. */
  Daily = "Daily",
  /** End-of-week: cluster the week's daily summaries into semantic topic groups. */
  Weekly = "Weekly",
  /** End-of-month: compute the persona delta and write a PersonaDeltaSnapshot. */
  Monthly = "Monthly",
  /** Caller-initiated pass — runs whichever tiers have work pending. */
  OnDemand = "OnDemand",
}

/** Why a memory was promoted to the core tier. */
export enum CoreMemoryKind {
  /** A fact the user explicitly asked the AI to remember. */
  UserAsserted = "UserAsserted",
  /** Inferred from interaction patterns — a long-standing preference / theme. */
  PatternInferred = "PatternInferred",
  /** Promoted because of extreme salience. */
  HighSalience = "HighSalience",
  /** Promoted by the host directly (profile sync, identity bootstrap). */
  HostProvided = "HostProvided",
}

// ─────────────────────────────────────────────────────────────────────────────
// Tier records
// ─────────────────────────────────────────────────────────────────────────────

/** A core memory the AI will not forget. Compact by design. */
export interface CoreMemory {
  /** Stable identifier. */
  readonly id: string;
  /** UTC time the memory was committed to core. */
  readonly createdAtUtc: Date;
  /** UTC time the memory was last reinforced (re-asserted, re-cited). Mutable. */
  lastReinforcedUtc: Date;
  /** Short, dense statement of the memory, third-person from the AI's view. */
  readonly statement: string;
  /** How the memory came to be in core. */
  readonly kind: CoreMemoryKind;
  /** Optional topic label (e.g. "family", "career", "health"). */
  readonly topic: string | null;
  /** Embedding of the statement for retrieval; null when unavailable. */
  readonly embedding: number[] | null;
  /** How many times this memory has been reinforced. Mutable. */
  reinforcementCount: number;
  /** Trace back to the lower-tier source memory, if one exists. */
  readonly sourceMemoryId: string | null;
}

/** Options for constructing a CoreMemory — mirrors C# object-initializer defaults. */
export interface CoreMemoryInit {
  statement?: string;
  kind?: CoreMemoryKind;
  topic?: string | null;
  embedding?: number[] | null;
  sourceMemoryId?: string | null;
  clock?: () => Date;
}

/** Builds a CoreMemory with C#-equivalent defaults (new id, now timestamps). */
export function createCoreMemory(init: CoreMemoryInit = {}): CoreMemory {
  const now = (init.clock ?? (() => new Date()))();
  return {
    id: crypto.randomUUID(),
    createdAtUtc: now,
    lastReinforcedUtc: now,
    statement: init.statement ?? "",
    kind: init.kind ?? CoreMemoryKind.UserAsserted,
    topic: init.topic ?? null,
    embedding: init.embedding ?? null,
    reinforcementCount: 0,
    sourceMemoryId: init.sourceMemoryId ?? null,
  };
}

/** Compressed record of a single calendar day's worth of episodic memory. */
export interface DailyMemorySummary {
  /** Stable identifier. */
  readonly id: string;
  /** The calendar day this summary covers ("YYYY-MM-DD", UTC). */
  readonly day: string;
  /** UTC time the summary was produced. */
  readonly generatedAtUtc: Date;
  /** Short prose summary of the day's gist. */
  readonly summary: string;
  /** The most salient verbatim exchanges from the day (typically 3–5). */
  readonly highlightEntries: readonly EpisodicMemoryEntry[];
  /** Total number of episodic entries collapsed into this summary. */
  readonly episodeCount: number;
  /** Aggregated topic weights across the day's exchanges (label → weight). */
  readonly topicWeights: ReadonlyMap<string, number>;
  /** Mean cosine-distance dispersion of the day's embeddings (0..1). */
  readonly topicDispersion: number;
  /** Salience score 0.0–1.0 assigned by the summariser. */
  readonly salience: number;
}

/** Init shape for a DailyMemorySummary — mirrors C# object-initializer defaults. */
export interface DailyMemorySummaryInit {
  day: string;
  summary?: string;
  highlightEntries?: readonly EpisodicMemoryEntry[];
  episodeCount?: number;
  topicWeights?: ReadonlyMap<string, number>;
  topicDispersion?: number;
  salience?: number;
  clock?: () => Date;
}

/** Builds a DailyMemorySummary with C#-equivalent defaults. */
export function createDailySummary(init: DailyMemorySummaryInit): DailyMemorySummary {
  const now = (init.clock ?? (() => new Date()))();
  return {
    id: crypto.randomUUID(),
    day: init.day,
    generatedAtUtc: now,
    summary: init.summary ?? "",
    highlightEntries: init.highlightEntries ?? [],
    episodeCount: init.episodeCount ?? 0,
    topicWeights: init.topicWeights ?? new Map<string, number>(),
    topicDispersion: init.topicDispersion ?? 0,
    salience: init.salience ?? 0,
  };
}

/** Topic-coherent cluster of daily summaries — the "semantic memory" tier. */
export interface SemanticMemoryCluster {
  /** Stable identifier. */
  readonly id: string;
  /** UTC time the cluster was produced. */
  readonly generatedAtUtc: Date;
  /** The week this cluster covers — Monday of that week ("YYYY-MM-DD", UTC). */
  readonly weekStartingMonday: string;
  /** Dominant topic label for this cluster. */
  readonly topic: string;
  /** Short prose summary of the cluster's gist. */
  readonly summary: string;
  /** Centroid embedding (mean of constituent embeddings); null when unavailable. */
  readonly centroidEmbedding: number[] | null;
  /** IDs of the daily summaries that contributed to this cluster. */
  readonly sourceDailyIds: readonly string[];
  /** Aggregate weight of the topic across constituent days. */
  readonly topicWeight: number;
  /** Salience score 0.0–1.0. */
  readonly salience: number;
}

/** Init shape for a SemanticMemoryCluster — mirrors C# object-initializer defaults. */
export interface SemanticMemoryClusterInit {
  weekStartingMonday: string;
  topic?: string;
  summary?: string;
  centroidEmbedding?: number[] | null;
  sourceDailyIds?: readonly string[];
  topicWeight?: number;
  salience?: number;
  clock?: () => Date;
}

/** Builds a SemanticMemoryCluster with C#-equivalent defaults. */
export function createSemanticCluster(init: SemanticMemoryClusterInit): SemanticMemoryCluster {
  const now = (init.clock ?? (() => new Date()))();
  return {
    id: crypto.randomUUID(),
    generatedAtUtc: now,
    weekStartingMonday: init.weekStartingMonday,
    topic: init.topic ?? "",
    summary: init.summary ?? "",
    centroidEmbedding: init.centroidEmbedding ?? null,
    sourceDailyIds: init.sourceDailyIds ?? [],
    topicWeight: init.topicWeight ?? 0,
    salience: init.salience ?? 0,
  };
}

/** Diff between a PersonaState at the start and end of a consolidation period. */
export interface PersonaDeltaSnapshot {
  /** Stable identifier. */
  readonly id: string;
  /** UTC time the delta was captured. */
  readonly generatedAtUtc: Date;
  /** Start of the period ("YYYY-MM-DD", UTC). */
  readonly periodStart: string;
  /** End of the period ("YYYY-MM-DD", UTC). */
  readonly periodEnd: string;
  /** User identifier. */
  readonly userId: string;
  /** Verbosity at period start. */
  readonly verbosityBefore: string;
  /** Verbosity at period end. */
  readonly verbosityAfter: string;
  /** Formality at period start. */
  readonly formalityBefore: string;
  /** Formality at period end. */
  readonly formalityAfter: string;
  /** New topics that emerged in the period (label → accumulated weight). */
  readonly newTopics: ReadonlyMap<string, number>;
  /** Topics that gained the most weight (label → weight delta). */
  readonly strengthenedTopics: ReadonlyMap<string, number>;
  /** Topics the user explicitly down-voted during the period. */
  readonly newlyDisfavouredTopics: readonly string[];
  /** Net positive minus negative signals across the period. */
  readonly netSignalDelta: number;
  /** Total interactions during the period. */
  readonly interactionsInPeriod: number;
  /** Short human-readable narrative of how the persona changed. */
  readonly narrative: string;
}

/** Init shape for a PersonaDeltaSnapshot — mirrors C# object-initializer defaults. */
export interface PersonaDeltaSnapshotInit {
  periodStart: string;
  periodEnd: string;
  userId?: string;
  verbosityBefore?: string;
  verbosityAfter?: string;
  formalityBefore?: string;
  formalityAfter?: string;
  newTopics?: ReadonlyMap<string, number>;
  strengthenedTopics?: ReadonlyMap<string, number>;
  newlyDisfavouredTopics?: readonly string[];
  netSignalDelta?: number;
  interactionsInPeriod?: number;
  narrative?: string;
  clock?: () => Date;
}

/** Builds a PersonaDeltaSnapshot with C#-equivalent defaults. */
export function createPersonaDelta(init: PersonaDeltaSnapshotInit): PersonaDeltaSnapshot {
  const now = (init.clock ?? (() => new Date()))();
  return {
    id: crypto.randomUUID(),
    generatedAtUtc: now,
    periodStart: init.periodStart,
    periodEnd: init.periodEnd,
    userId: init.userId ?? "default",
    verbosityBefore: init.verbosityBefore ?? "",
    verbosityAfter: init.verbosityAfter ?? "",
    formalityBefore: init.formalityBefore ?? "",
    formalityAfter: init.formalityAfter ?? "",
    newTopics: init.newTopics ?? new Map<string, number>(),
    strengthenedTopics: init.strengthenedTopics ?? new Map<string, number>(),
    newlyDisfavouredTopics: init.newlyDisfavouredTopics ?? [],
    netSignalDelta: init.netSignalDelta ?? 0,
    interactionsInPeriod: init.interactionsInPeriod ?? 0,
    narrative: init.narrative ?? "",
  };
}

/** Outcome of a single consolidator tick. */
export interface ConsolidationOutcome {
  readonly kind: SleepKind;
  readonly dailySummariesProduced: number;
  readonly semanticClustersProduced: number;
  readonly personaDeltasProduced: number;
  readonly corePromotions: number;
  readonly episodesPruned: number;
  readonly dailiesPruned: number;
  readonly semanticsPruned: number;
  readonly ranAtUtc: Date;
}

/** Retention windows + core-promotion thresholds. */
export interface MemoryConsolidationOptions {
  /** Days of episodic entries to retain after they've been summarised. */
  readonly episodicRetentionDays?: number;
  /** Days of daily summaries to retain after weekly consolidation. */
  readonly dailyRetentionDays?: number;
  /** Days of semantic clusters to retain. */
  readonly semanticRetentionDays?: number;
  /** Salience threshold above which daily summaries promote to core. */
  readonly dailyCorePromotionThreshold?: number;
  /** Salience threshold above which weekly clusters promote to core. */
  readonly weeklyCorePromotionThreshold?: number;
}

/** Defaults matching MemoryConsolidationOptions in the C# reference. */
export const DEFAULT_CONSOLIDATION_OPTIONS: Required<MemoryConsolidationOptions> = {
  episodicRetentionDays: 7,
  dailyRetentionDays: 30,
  semanticRetentionDays: 365,
  dailyCorePromotionThreshold: 0.8,
  weeklyCorePromotionThreshold: 0.75,
};

// ─────────────────────────────────────────────────────────────────────────────
// Day helpers — "YYYY-MM-DD" UTC date arithmetic
// ─────────────────────────────────────────────────────────────────────────────

/** UTC calendar day of a Date, as "YYYY-MM-DD". */
export function dayKeyOf(date: Date): string {
  const y = date.getUTCFullYear().toString().padStart(4, "0");
  const m = (date.getUTCMonth() + 1).toString().padStart(2, "0");
  const d = date.getUTCDate().toString().padStart(2, "0");
  return `${y}-${m}-${d}`;
}

/** Parses a "YYYY-MM-DD" key back into a UTC Date at midnight. */
function parseDayKey(day: string): Date {
  const [y, m, d] = day.split("-").map(Number);
  return new Date(Date.UTC(y, m - 1, d));
}

/** Adds `days` (may be negative) to a "YYYY-MM-DD" key. */
export function addDays(day: string, days: number): string {
  const dt = parseDayKey(day);
  dt.setUTCDate(dt.getUTCDate() + days);
  return dayKeyOf(dt);
}

/** The Monday of the week containing `day`. Monday = d minus ((dow+6)%7) days (Sunday=0). */
export function mondayOf(day: string): string {
  const dow = parseDayKey(day).getUTCDay(); // Sun=0..Sat=6
  const delta = (dow + 6) % 7; // Sun=0..Sat=6 → Mon=0..Sun=6
  return addDays(day, -delta);
}

/** Four-digit year of a "YYYY-MM-DD" key. */
export function yearOf(day: string): number {
  return parseDayKey(day).getUTCFullYear();
}

/** 1-based month of a "YYYY-MM-DD" key. */
export function monthOf(day: string): number {
  return parseDayKey(day).getUTCMonth() + 1;
}

/** First day of the month containing `day`, as "YYYY-MM-DD". */
export function monthFirstDayOf(day: string): string {
  const y = yearOf(day).toString().padStart(4, "0");
  const m = monthOf(day).toString().padStart(2, "0");
  return `${y}-${m}-01`;
}

// ─────────────────────────────────────────────────────────────────────────────
// Cosine — FULL cosine (differs from the episodic store's dot-only cosine).
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Full cosine similarity: dot / (‖a‖·‖b‖). Returns 0 on a length mismatch or a
 * near-zero denominator. This does NOT assume the vectors are L2-normalised, so
 * it differs from the episodic store's dot-product cosine — both are kept.
 */
export function cosineFull(a: number[], b: number[]): number {
  if (a.length !== b.length) return 0;
  let dot = 0;
  let magA = 0;
  let magB = 0;
  for (let i = 0; i < a.length; i++) {
    dot += a[i] * b[i];
    magA += a[i] * a[i];
    magB += b[i] * b[i];
  }
  const denom = Math.sqrt(magA) * Math.sqrt(magB);
  return denom < Number.EPSILON ? 0 : dot / denom;
}

// ─────────────────────────────────────────────────────────────────────────────
// Store interfaces
// ─────────────────────────────────────────────────────────────────────────────

/** Persistent store for tier-2 daily summaries. */
export interface IDailyMemoryStore {
  /** Adds a daily summary. Replaces any existing entry for the same day. */
  upsertAsync(summary: DailyMemorySummary): Promise<void>;
  /** Returns the summary for the given day, or null when none exists. */
  getAsync(day: string): Promise<DailyMemorySummary | null>;
  /** Returns all summaries between fromInclusive and toInclusive (day-ordered). */
  getRangeAsync(fromInclusive: string, toInclusive: string): Promise<readonly DailyMemorySummary[]>;
  /** Removes summaries older than cutoff. Returns count removed. */
  pruneOlderThanAsync(cutoff: string): Promise<number>;
  /** Total summaries currently stored. */
  countAsync(): Promise<number>;
}

/** Persistent store for tier-3 semantic memory clusters. */
export interface ISemanticMemoryStore {
  /** Adds a cluster. */
  addAsync(cluster: SemanticMemoryCluster): Promise<void>;
  /** Returns all clusters for the given week, ordered by topicWeight desc. */
  getWeekAsync(weekStartingMonday: string): Promise<readonly SemanticMemoryCluster[]>;
  /** Top-topK clusters by centroid cosine similarity; recency fallback when null. */
  searchAsync(queryEmbedding: number[] | null, topK?: number): Promise<readonly SemanticMemoryCluster[]>;
  /** Removes clusters whose week start is before cutoff. */
  pruneOlderThanAsync(cutoff: string): Promise<number>;
  /** Total clusters currently stored. */
  countAsync(): Promise<number>;
}

/** Persistent store for tier-4 persona-delta snapshots. Retained forever. */
export interface IPersonaDeltaStore {
  /** Adds a delta snapshot. */
  addAsync(snapshot: PersonaDeltaSnapshot): Promise<void>;
  /** Returns all snapshots for the given user, ordered by periodStart. */
  getForUserAsync(userId: string): Promise<readonly PersonaDeltaSnapshot[]>;
  /** Total snapshots currently stored. */
  countAsync(): Promise<number>;
}

/** Persistent store for tier-5 core memories — things the AI will not forget. */
export interface ICoreMemoryStore {
  /** Adds a core memory. */
  addAsync(memory: CoreMemory): Promise<void>;
  /** Returns a core memory by id, or null when not found. */
  getAsync(id: string): Promise<CoreMemory | null>;
  /** Top-topK core memories by embedding cosine; reinforcement-order fallback when null. */
  searchAsync(queryEmbedding: number[] | null, topK?: number): Promise<readonly CoreMemory[]>;
  /** All core memories in reinforcement order (most reinforced first). */
  listAllAsync(): Promise<readonly CoreMemory[]>;
  /** Increments reinforcementCount and bumps lastReinforcedUtc. No-op when unknown. */
  reinforceAsync(id: string): Promise<void>;
  /** Removes a core memory. */
  removeAsync(id: string): Promise<boolean>;
  /** Total core memories currently stored. */
  countAsync(): Promise<number>;
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory store implementations
// ─────────────────────────────────────────────────────────────────────────────

/** In-memory {@link IDailyMemoryStore}. */
export class InMemoryDailyMemoryStore implements IDailyMemoryStore {
  private readonly store = new Map<string, DailyMemorySummary>();

  async upsertAsync(summary: DailyMemorySummary): Promise<void> {
    if (!summary) throw new Error("summary required");
    this.store.set(summary.day, summary);
  }

  async getAsync(day: string): Promise<DailyMemorySummary | null> {
    return this.store.get(day) ?? null;
  }

  async getRangeAsync(
    fromInclusive: string,
    toInclusive: string,
  ): Promise<readonly DailyMemorySummary[]> {
    return [...this.store.values()]
      .filter((s) => s.day >= fromInclusive && s.day <= toInclusive)
      .sort((a, b) => (a.day < b.day ? -1 : a.day > b.day ? 1 : 0));
  }

  async pruneOlderThanAsync(cutoff: string): Promise<number> {
    const toRemove = [...this.store.keys()].filter((d) => d < cutoff);
    for (const d of toRemove) this.store.delete(d);
    return toRemove.length;
  }

  async countAsync(): Promise<number> {
    return this.store.size;
  }
}

/** In-memory {@link ISemanticMemoryStore}. */
export class InMemorySemanticMemoryStore implements ISemanticMemoryStore {
  private readonly store: SemanticMemoryCluster[] = [];

  async addAsync(cluster: SemanticMemoryCluster): Promise<void> {
    if (!cluster) throw new Error("cluster required");
    this.store.push(cluster);
  }

  async getWeekAsync(weekStartingMonday: string): Promise<readonly SemanticMemoryCluster[]> {
    return this.store
      .filter((c) => c.weekStartingMonday === weekStartingMonday)
      .sort((a, b) => b.topicWeight - a.topicWeight);
  }

  async searchAsync(
    queryEmbedding: number[] | null,
    topK = 5,
  ): Promise<readonly SemanticMemoryCluster[]> {
    if (queryEmbedding == null) {
      return [...this.store]
        .sort((a, b) => b.generatedAtUtc.getTime() - a.generatedAtUtc.getTime())
        .slice(0, topK);
    }
    return this.store
      .filter((c) => c.centroidEmbedding != null)
      .map((c) => ({ c, score: cosineFull(queryEmbedding, c.centroidEmbedding!) }))
      .sort((a, b) => b.score - a.score)
      .slice(0, topK)
      .map((x) => x.c);
  }

  async pruneOlderThanAsync(cutoff: string): Promise<number> {
    let removed = 0;
    for (let i = this.store.length - 1; i >= 0; i--) {
      if (this.store[i].weekStartingMonday < cutoff) {
        this.store.splice(i, 1);
        removed++;
      }
    }
    return removed;
  }

  async countAsync(): Promise<number> {
    return this.store.length;
  }
}

/** In-memory {@link IPersonaDeltaStore}. */
export class InMemoryPersonaDeltaStore implements IPersonaDeltaStore {
  private readonly store: PersonaDeltaSnapshot[] = [];

  async addAsync(snapshot: PersonaDeltaSnapshot): Promise<void> {
    if (!snapshot) throw new Error("snapshot required");
    this.store.push(snapshot);
  }

  async getForUserAsync(userId: string): Promise<readonly PersonaDeltaSnapshot[]> {
    return this.store
      .filter((s) => s.userId === userId)
      .sort((a, b) =>
        a.periodStart < b.periodStart ? -1 : a.periodStart > b.periodStart ? 1 : 0,
      );
  }

  async countAsync(): Promise<number> {
    return this.store.length;
  }
}

/** In-memory {@link ICoreMemoryStore}. */
export class InMemoryCoreMemoryStore implements ICoreMemoryStore {
  private readonly store = new Map<string, CoreMemory>();

  async addAsync(memory: CoreMemory): Promise<void> {
    if (!memory) throw new Error("memory required");
    this.store.set(memory.id, memory);
  }

  async getAsync(id: string): Promise<CoreMemory | null> {
    return this.store.get(id) ?? null;
  }

  async searchAsync(queryEmbedding: number[] | null, topK = 5): Promise<readonly CoreMemory[]> {
    if (queryEmbedding == null) {
      return [...this.store.values()].sort(byReinforcement).slice(0, topK);
    }
    return [...this.store.values()]
      .filter((m) => m.embedding != null)
      .map((m) => ({ m, score: cosineFull(queryEmbedding, m.embedding!) }))
      .sort((a, b) => b.score - a.score)
      .slice(0, topK)
      .map((x) => x.m);
  }

  async listAllAsync(): Promise<readonly CoreMemory[]> {
    return [...this.store.values()].sort(byReinforcement);
  }

  async reinforceAsync(id: string): Promise<void> {
    const memory = this.store.get(id);
    if (memory) {
      memory.reinforcementCount++;
      memory.lastReinforcedUtc = new Date();
    }
  }

  async removeAsync(id: string): Promise<boolean> {
    return this.store.delete(id);
  }

  async countAsync(): Promise<number> {
    return this.store.size;
  }
}

/** Sort: reinforcementCount desc, then lastReinforcedUtc desc. */
function byReinforcement(a: CoreMemory, b: CoreMemory): number {
  if (b.reinforcementCount !== a.reinforcementCount)
    return b.reinforcementCount - a.reinforcementCount;
  return b.lastReinforcedUtc.getTime() - a.lastReinforcedUtc.getTime();
}

// ─────────────────────────────────────────────────────────────────────────────
// IMemorySummarizer + HeuristicSummarizer
// ─────────────────────────────────────────────────────────────────────────────

/** Produces the text + scores for each consolidation tier. */
export interface IMemorySummarizer {
  /** Produces a DailyMemorySummary from the day's episodic entries. */
  summarizeDayAsync(day: string, entries: readonly EpisodicMemoryEntry[]): Promise<DailyMemorySummary>;
  /** Produces zero or more SemanticMemoryCluster records from a week's dailies. */
  consolidateWeekAsync(
    weekStartingMonday: string,
    daysInWeek: readonly DailyMemorySummary[],
  ): Promise<readonly SemanticMemoryCluster[]>;
  /** Computes the PersonaDeltaSnapshot across the period. */
  derivePersonaDeltaAsync(
    before: PersonaState,
    after: PersonaState,
    daysInPeriod: readonly DailyMemorySummary[],
  ): Promise<PersonaDeltaSnapshot>;
}

/**
 * Heuristic {@link IMemorySummarizer} that requires no LLM. Produces summaries
 * entirely from structural signals — embedding clustering, topic-weight
 * aggregation, length-and-recency salience. Formulas are identical to the C#
 * HeuristicSummarizer.
 */
export class HeuristicSummarizer implements IMemorySummarizer {
  /** Max high-salience verbatim entries kept per DailyMemorySummary. */
  readonly highlightCount: number;
  /** Min contributing days a topic needs across a week to form a cluster. */
  readonly minDaysPerTopicForCluster: number;
  private readonly clock: () => Date;

  constructor(
    options?: {
      highlightCount?: number;
      minDaysPerTopicForCluster?: number;
      clock?: () => Date;
    },
  ) {
    this.highlightCount = options?.highlightCount ?? 5;
    this.minDaysPerTopicForCluster = options?.minDaysPerTopicForCluster ?? 2;
    this.clock = options?.clock ?? (() => new Date());
  }

  // ── summarizeDayAsync ─────────────────────────────────────────────────────

  async summarizeDayAsync(
    day: string,
    entries: readonly EpisodicMemoryEntry[],
  ): Promise<DailyMemorySummary> {
    if (!entries) throw new Error("entries required");

    if (entries.length === 0) {
      return createDailySummary({
        day,
        summary: `No exchanges recorded on ${day}.`,
        episodeCount: 0,
        clock: this.clock,
      });
    }

    const topicWeights = aggregateTopicWeights(entries);
    const dispersion = meanPairwiseCosineDistance(entries);
    const highlights = selectHighlights(entries, this.highlightCount);
    const salience = computeDailySalience(entries.length, topicWeights, dispersion);
    const summary = buildDailySummaryText(day, entries.length, topicWeights, highlights);

    return createDailySummary({
      day,
      summary,
      highlightEntries: highlights,
      episodeCount: entries.length,
      topicWeights,
      topicDispersion: dispersion,
      salience,
      clock: this.clock,
    });
  }

  // ── consolidateWeekAsync ──────────────────────────────────────────────────

  async consolidateWeekAsync(
    weekStartingMonday: string,
    daysInWeek: readonly DailyMemorySummary[],
  ): Promise<readonly SemanticMemoryCluster[]> {
    if (!daysInWeek) throw new Error("daysInWeek required");
    if (daysInWeek.length === 0) return [];

    // Tally how many days each topic appeared in and its cumulative weight.
    // Topics are compared case-insensitively (mirrors StringComparer.OrdinalIgnoreCase);
    // topic labels arrive already lowercased from AggregateTopicWeights.
    const topicToDays = new Map<string, DailyMemorySummary[]>();
    const topicToWeight = new Map<string, number>();

    for (const d of daysInWeek) {
      for (const [topic, w] of d.topicWeights) {
        let list = topicToDays.get(topic);
        if (!list) {
          list = [];
          topicToDays.set(topic, list);
        }
        list.push(d);
        topicToWeight.set(topic, (topicToWeight.get(topic) ?? 0) + w);
      }
    }

    let totalWeight = 0;
    for (const w of topicToWeight.values()) totalWeight += w;
    if (totalWeight <= 0) totalWeight = 1;

    const clusters: SemanticMemoryCluster[] = [];
    const topicsByWeightDesc = [...topicToWeight.keys()].sort(
      (a, b) => topicToWeight.get(b)! - topicToWeight.get(a)!,
    );
    for (const topic of topicsByWeightDesc) {
      const contributingDays = topicToDays.get(topic)!;
      if (contributingDays.length < this.minDaysPerTopicForCluster) continue;

      const centroid = centroidOfHighlights(contributingDays);
      const weight = topicToWeight.get(topic)!;
      const clusterSalience = Math.min(
        1.0,
        weight / totalWeight + (contributingDays.length / 7.0) * 0.25,
      );

      clusters.push(
        createSemanticCluster({
          weekStartingMonday,
          topic,
          summary: buildWeeklyClusterText(topic, contributingDays),
          centroidEmbedding: centroid,
          sourceDailyIds: contributingDays.map((d) => d.id),
          topicWeight: weight,
          salience: clusterSalience,
          clock: this.clock,
        }),
      );
    }
    return clusters;
  }

  // ── derivePersonaDeltaAsync ───────────────────────────────────────────────

  async derivePersonaDeltaAsync(
    before: PersonaState,
    after: PersonaState,
    daysInPeriod: readonly DailyMemorySummary[],
  ): Promise<PersonaDeltaSnapshot> {
    if (!before) throw new Error("before required");
    if (!after) throw new Error("after required");
    if (!daysInPeriod) throw new Error("daysInPeriod required");

    const newTopics = new Map<string, number>();
    const strengthened = new Map<string, number>();
    for (const [topic, afterW] of Object.entries(after.topicWeights)) {
      const beforeW = before.topicWeights[topic] ?? 0;
      const delta = afterW - beforeW;
      if (beforeW <= 0 && afterW > 0) {
        newTopics.set(topic, afterW);
      } else if (delta > 0) {
        strengthened.set(topic, delta);
      }
    }

    const disfavouredNew = [...after.disfavouredTopics].filter(
      (t) => !before.disfavouredTopics.has(t),
    );

    const netSignals =
      after.positiveSignals - before.positiveSignals -
      (after.negativeSignals - before.negativeSignals);
    const interactions = after.totalInteractions - before.totalInteractions;

    const periodStart =
      daysInPeriod.length > 0
        ? minDay(daysInPeriod)
        : dayKeyOf(after.lastUpdatedUtc);
    const periodEnd =
      daysInPeriod.length > 0
        ? maxDay(daysInPeriod)
        : dayKeyOf(after.lastUpdatedUtc);

    const narrative = buildPersonaNarrative(
      before,
      after,
      newTopics,
      strengthened,
      disfavouredNew,
      netSignals,
      interactions,
      periodStart,
      periodEnd,
    );

    return createPersonaDelta({
      userId: after.userId,
      periodStart,
      periodEnd,
      verbosityBefore: before.verbosity,
      verbosityAfter: after.verbosity,
      formalityBefore: before.formality,
      formalityAfter: after.formality,
      newTopics,
      strengthenedTopics: strengthened,
      newlyDisfavouredTopics: disfavouredNew,
      netSignalDelta: netSignals,
      interactionsInPeriod: interactions,
      narrative,
      clock: this.clock,
    });
  }
}

// ── Summarizer helpers — topic + dispersion ─────────────────────────────────

/** Topic weights from "topic" (+1) and pipe-split "topics" (each +1), lowercased. */
function aggregateTopicWeights(entries: readonly EpisodicMemoryEntry[]): Map<string, number> {
  const weights = new Map<string, number>();
  for (const e of entries) {
    if (e.tags == null) continue;
    const t = e.tags["topic"];
    if (t != null && t.trim().length > 0) accumulateTopic(weights, t, 1);
    const multi = e.tags["topics"];
    if (multi != null && multi.trim().length > 0) {
      for (const p of multi.split("|")) {
        if (p.length === 0) continue; // RemoveEmptyEntries
        accumulateTopic(weights, p, 1);
      }
    }
  }
  return weights;
}

function accumulateTopic(dict: Map<string, number>, topic: string, weight: number): void {
  const key = topic.trim().toLowerCase();
  if (key.length === 0) return;
  dict.set(key, (dict.get(key) ?? 0) + weight);
}

/** Mean over all pairs of (1 - clamp(fullCosine,-1,1)); 0 when <2 embedded entries. */
function meanPairwiseCosineDistance(entries: readonly EpisodicMemoryEntry[]): number {
  const withEmbeddings = entries.filter((e) => hasEmbedding(e));
  if (withEmbeddings.length < 2) return 0;

  let total = 0;
  let pairs = 0;
  for (let i = 0; i < withEmbeddings.length; i++) {
    for (let j = i + 1; j < withEmbeddings.length; j++) {
      const sim = cosineFull(withEmbeddings[i].embedding!, withEmbeddings[j].embedding!);
      total += 1.0 - clamp(sim, -1.0, 1.0);
      pairs++;
    }
  }
  return pairs === 0 ? 0 : clamp(total / pairs, 0.0, 1.0);
}

/** Top-`count` entries by salience proxy (or all when ≤count), re-sorted by time. */
function selectHighlights(
  entries: readonly EpisodicMemoryEntry[],
  count: number,
): readonly EpisodicMemoryEntry[] {
  if (entries.length <= count) {
    return [...entries].sort(byTimeAsc);
  }
  return [...entries]
    .map((e) => ({ entry: e, score: entrySalienceProxy(e, entries) }))
    // OrderByDescending(score).ThenByDescending(recordedAt)
    .sort((a, b) => {
      if (b.score !== a.score) return b.score - a.score;
      return b.entry.recordedAtUtc.getTime() - a.entry.recordedAtUtc.getTime();
    })
    .slice(0, count)
    .map((x) => x.entry)
    .sort(byTimeAsc);
}

function entrySalienceProxy(
  entry: EpisodicMemoryEntry,
  all: readonly EpisodicMemoryEntry[],
): number {
  const lengthScore = Math.min(
    1.0,
    (entry.userText.length + entry.assistantText.length) / 800.0,
  );
  let uniquenessScore = 0.5;
  if (hasEmbedding(entry)) {
    const others = all.filter((e) => e.id !== entry.id && hasEmbedding(e));
    if (others.length > 0) {
      let sum = 0;
      for (const e of others) sum += cosineFull(entry.embedding!, e.embedding!);
      const meanSim = sum / others.length;
      uniquenessScore = 1.0 - clamp(meanSim, -1.0, 1.0);
    }
  }
  return lengthScore * 0.6 + uniquenessScore * 0.4;
}

/** Daily salience = volume·0.4 + dispersion·0.3 + topicConcentration·0.3. */
function computeDailySalience(
  episodeCount: number,
  topicWeights: ReadonlyMap<string, number>,
  dispersion: number,
): number {
  const volumeScore = Math.min(1.0, episodeCount / 30.0);
  let topicConcentration: number;
  if (topicWeights.size === 0) {
    topicConcentration = 0.5;
  } else {
    let maxW = -Infinity;
    let sumW = 0;
    for (const w of topicWeights.values()) {
      if (w > maxW) maxW = w;
      sumW += w;
    }
    topicConcentration = Math.min(1.0, maxW / Math.max(1, sumW));
  }
  return volumeScore * 0.4 + dispersion * 0.3 + topicConcentration * 0.3;
}

/** Mean of all highlight embeddings across contributing days; null when none. */
function centroidOfHighlights(days: readonly DailyMemorySummary[]): number[] | null {
  const allEmbeddings: number[][] = [];
  for (const d of days) {
    for (const e of d.highlightEntries) {
      if (hasEmbedding(e)) allEmbeddings.push(e.embedding!);
    }
  }
  if (allEmbeddings.length === 0) return null;
  const dim = allEmbeddings[0].length;
  const centroid = new Array<number>(dim).fill(0);
  for (const e of allEmbeddings) {
    for (let i = 0; i < dim && i < e.length; i++) centroid[i] += e[i];
  }
  for (let i = 0; i < dim; i++) centroid[i] /= allEmbeddings.length;
  return centroid;
}

// ── Summarizer helpers — text builders ──────────────────────────────────────

function buildDailySummaryText(
  day: string,
  count: number,
  topics: ReadonlyMap<string, number>,
  highlights: readonly EpisodicMemoryEntry[],
): string {
  const topTopics = [...topics.entries()]
    .sort((a, b) => b[1] - a[1])
    .slice(0, 3)
    .map((kv) => kv[0]);

  const topicsClause = topTopics.length > 0 ? ` Top topics: ${topTopics.join(", ")}.` : "";

  const highlightClause =
    highlights.length > 0
      ? ` Standout moment: "${truncate(highlights[0].userText, 120)}".`
      : "";

  return (
    `On ${day} you had ${count} ` +
    (count === 1 ? "exchange." : "exchanges.") +
    topicsClause +
    highlightClause
  );
}

function buildWeeklyClusterText(
  topic: string,
  contributingDays: readonly DailyMemorySummary[],
): string {
  let totalEpisodes = 0;
  for (const d of contributingDays) totalEpisodes += d.episodeCount;
  return (
    `Across ${contributingDays.length} days this week you returned to ` +
    `"${topic}" — ${totalEpisodes} exchanges in total.`
  );
}

function buildPersonaNarrative(
  before: PersonaState,
  after: PersonaState,
  newTopics: ReadonlyMap<string, number>,
  strengthened: ReadonlyMap<string, number>,
  disfavoured: readonly string[],
  netSignals: number,
  interactions: number,
  periodStart: string,
  periodEnd: string,
): string {
  const parts: string[] = [];
  parts.push(`Between ${periodStart} and ${periodEnd}, ${interactions} interactions were recorded.`);
  if (newTopics.size > 0) {
    parts.push(
      "New interests appeared: " +
        topNKeys(newTopics, 3).join(", ") +
        ".",
    );
  }
  if (strengthened.size > 0) {
    parts.push(
      "Existing interests deepened around " +
        topNKeys(strengthened, 3).join(", ") +
        ".",
    );
  }
  if (disfavoured.length > 0) {
    parts.push("Topics now avoided: " + disfavoured.join(", ") + ".");
  }
  if (before.verbosity !== after.verbosity) {
    parts.push(`Preferred verbosity shifted from ${before.verbosity} to ${after.verbosity}.`);
  }
  if (before.formality !== after.formality) {
    parts.push(`Preferred tone shifted from ${before.formality} to ${after.formality}.`);
  }
  if (netSignals !== 0) {
    parts.push(
      netSignals > 0
        ? `Net feedback was positive (+${netSignals}).`
        : `Net feedback was negative (${netSignals}).`,
    );
  }
  return parts.join(" ");
}

/** Keys of `map` ordered by value desc, top-n. */
function topNKeys(map: ReadonlyMap<string, number>, n: number): string[] {
  return [...map.entries()]
    .sort((a, b) => b[1] - a[1])
    .slice(0, n)
    .map((kv) => kv[0]);
}

function truncate(s: string, max: number): string {
  if (s == null || s.length === 0) return "";
  if (s.length <= max) return s;
  return s.slice(0, max).replace(/\s+$/, "") + "…";
}

// ── Shared small helpers ────────────────────────────────────────────────────

function hasEmbedding(e: EpisodicMemoryEntry): boolean {
  return e.embedding != null && e.embedding.length > 0;
}

function clamp(x: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, x));
}

function byTimeAsc(a: EpisodicMemoryEntry, b: EpisodicMemoryEntry): number {
  return a.recordedAtUtc.getTime() - b.recordedAtUtc.getTime();
}

function minDay(days: readonly DailyMemorySummary[]): string {
  let m = days[0].day;
  for (const d of days) if (d.day < m) m = d.day;
  return m;
}

function maxDay(days: readonly DailyMemorySummary[]): string {
  let m = days[0].day;
  for (const d of days) if (d.day > m) m = d.day;
  return m;
}

// ─────────────────────────────────────────────────────────────────────────────
// IMemoryConsolidator + MemoryConsolidator
// ─────────────────────────────────────────────────────────────────────────────

/** Promotes lower-tier memory into higher tiers and enforces retention. */
export interface IMemoryConsolidator {
  /**
   * Runs the consolidation pass for the given kind. OnDemand runs every tier
   * with work pending. Returns the breakdown of what was produced and pruned.
   */
  tickAsync(kind: SleepKind): Promise<ConsolidationOutcome>;
}

/** Default {@link IMemoryConsolidator} implementation. */
export class MemoryConsolidator implements IMemoryConsolidator {
  private readonly episodic: IEpisodicMemoryStore;
  private readonly daily: IDailyMemoryStore;
  private readonly semantic: ISemanticMemoryStore;
  private readonly personaDelta: IPersonaDeltaStore;
  private readonly core: ICoreMemoryStore;
  private readonly personaStore: IPersonaStore;
  private readonly summarizer: IMemorySummarizer;
  private readonly options: Required<MemoryConsolidationOptions>;
  private readonly clock: () => Date;
  private readonly userId: string;

  constructor(
    episodic: IEpisodicMemoryStore,
    daily: IDailyMemoryStore,
    semantic: ISemanticMemoryStore,
    personaDelta: IPersonaDeltaStore,
    core: ICoreMemoryStore,
    personaStore: IPersonaStore,
    summarizer: IMemorySummarizer,
    options?: MemoryConsolidationOptions,
    clock?: () => Date,
    userId = "default",
  ) {
    if (!episodic) throw new Error("episodic required");
    if (!daily) throw new Error("daily required");
    if (!semantic) throw new Error("semantic required");
    if (!personaDelta) throw new Error("personaDelta required");
    if (!core) throw new Error("core required");
    if (!personaStore) throw new Error("personaStore required");
    if (!summarizer) throw new Error("summarizer required");
    this.episodic = episodic;
    this.daily = daily;
    this.semantic = semantic;
    this.personaDelta = personaDelta;
    this.core = core;
    this.personaStore = personaStore;
    this.summarizer = summarizer;
    this.options = { ...DEFAULT_CONSOLIDATION_OPTIONS, ...options };
    this.clock = clock ?? (() => new Date());
    this.userId = userId;
  }

  async tickAsync(kind: SleepKind): Promise<ConsolidationOutcome> {
    const now = this.clock();
    let dailies = 0;
    let clusters = 0;
    let deltas = 0;
    let corePromoted = 0;
    let episodesPruned = 0;
    let dailiesPruned = 0;
    let semanticsPruned = 0;

    if (kind === SleepKind.Daily || kind === SleepKind.OnDemand) {
      const [produced, promotedFromDaily] = await this.runDaily(now);
      dailies = produced;
      corePromoted += promotedFromDaily;
      episodesPruned += await this.pruneEpisodic(now);
    }

    if (kind === SleepKind.Weekly || kind === SleepKind.OnDemand) {
      const [produced, promotedFromWeekly] = await this.runWeekly(now);
      clusters = produced;
      corePromoted += promotedFromWeekly;
      dailiesPruned += await this.pruneDailies(now);
    }

    if (kind === SleepKind.Monthly || kind === SleepKind.OnDemand) {
      deltas = await this.runMonthly(now);
      semanticsPruned += await this.pruneSemantics(now);
    }

    return {
      kind,
      dailySummariesProduced: dailies,
      semanticClustersProduced: clusters,
      personaDeltasProduced: deltas,
      corePromotions: corePromoted,
      episodesPruned,
      dailiesPruned,
      semanticsPruned,
      ranAtUtc: now,
    };
  }

  // ── Daily pass ─────────────────────────────────────────────────────────────

  private async runDaily(now: Date): Promise<[number, number]> {
    const recent = await this.episodic.getRecentAsync(Number.MAX_SAFE_INTEGER);
    if (recent.length === 0) return [0, 0];

    // Group episodes by their calendar day (UTC).
    const today = dayKeyOf(now);
    const byDay = new Map<string, EpisodicMemoryEntry[]>();
    for (const e of recent) {
      const key = dayKeyOf(e.recordedAtUtc);
      let list = byDay.get(key);
      if (!list) {
        list = [];
        byDay.set(key, list);
      }
      list.push(e);
    }

    let produced = 0;
    let promoted = 0;
    for (const [day, group] of byDay) {
      if (!(day < today)) continue; // only fully completed days

      const existing = await this.daily.getAsync(day);
      if (existing != null && existing.episodeCount === group.length) {
        continue; // idempotent skip — already consolidated this day
      }

      const ordered = [...group].sort(byTimeAsc);
      const summary = await this.summarizer.summarizeDayAsync(day, ordered);
      await this.daily.upsertAsync(summary);
      produced++;

      if (summary.salience >= this.options.dailyCorePromotionThreshold) {
        promoted += await this.promoteDailyToCore(summary);
      }
    }
    return [produced, promoted];
  }

  // ── Weekly pass ────────────────────────────────────────────────────────────

  private async runWeekly(now: Date): Promise<[number, number]> {
    const today = dayKeyOf(now);
    const thisMonday = mondayOf(today);
    const lastMonday = addDays(thisMonday, -7);
    const lastSunday = addDays(lastMonday, 6);

    const lastWeek = await this.daily.getRangeAsync(lastMonday, lastSunday);
    if (lastWeek.length === 0) return [0, 0];

    // Idempotency: if we already have clusters for this week, skip.
    const existing = await this.semantic.getWeekAsync(lastMonday);
    if (existing.length > 0) return [0, 0];

    const clusters = await this.summarizer.consolidateWeekAsync(lastMonday, lastWeek);
    let promoted = 0;
    for (const c of clusters) {
      await this.semantic.addAsync(c);
      if (c.salience >= this.options.weeklyCorePromotionThreshold) {
        promoted += await this.promoteClusterToCore(c);
      }
    }
    return [clusters.length, promoted];
  }

  // ── Monthly pass ───────────────────────────────────────────────────────────

  private async runMonthly(now: Date): Promise<number> {
    const today = dayKeyOf(now);
    // Consider the most recently completed full month.
    const firstOfThisMonth = monthFirstDayOf(today);
    const lastMonthEnd = addDays(firstOfThisMonth, -1);
    const lastMonthStart = monthFirstDayOf(lastMonthEnd);

    // Idempotency: skip if we already have a delta whose PeriodStart falls in
    // the previous month (compared by month-year, not exact dates).
    const existingDeltas = await this.personaDelta.getForUserAsync(this.userId);
    if (
      existingDeltas.some(
        (d) =>
          yearOf(d.periodStart) === yearOf(lastMonthStart) &&
          monthOf(d.periodStart) === monthOf(lastMonthStart),
      )
    ) {
      return 0;
    }

    const days = await this.daily.getRangeAsync(lastMonthStart, lastMonthEnd);
    if (days.length === 0) return 0;

    const loaded = await this.personaStore.loadAsync(this.userId);
    const after = loaded ?? newPersona(this.userId);

    // For "before", reconstruct from the most recent prior delta if one exists;
    // otherwise treat as a fresh persona.
    const priors = existingDeltas
      .filter((d) => d.periodEnd < lastMonthStart)
      .sort((a, b) => (a.periodEnd < b.periodEnd ? 1 : a.periodEnd > b.periodEnd ? -1 : 0));
    const prior = priors.length > 0 ? priors[0] : null;
    const before =
      prior == null ? newPersona(this.userId) : reconstructPersonaBefore(after, days, prior);

    const delta = await this.summarizer.derivePersonaDeltaAsync(before, after, days);
    await this.personaDelta.addAsync(delta);
    return 1;
  }

  // ── Core promotions ──────────────────────────────────────────────────────

  private async promoteDailyToCore(summary: DailyMemorySummary): Promise<number> {
    // FirstOrDefault on TopicWeights.OrderByDescending — null Key when empty.
    let topTopic: string | null = null;
    let topWeight = -Infinity;
    for (const [k, v] of summary.topicWeights) {
      if (v > topWeight) {
        topWeight = v;
        topTopic = k;
      }
    }

    const statement =
      topTopic == null
        ? `On ${summary.day} an unusually meaningful day was recorded.`
        : `"${topTopic}" mattered enough on ${summary.day} to be remembered.`;

    let embedding: number[] | null = null;
    for (const h of summary.highlightEntries) {
      if (h.embedding != null && h.embedding.length > 0) {
        embedding = h.embedding;
        break;
      }
    }

    const memory = createCoreMemory({
      statement,
      kind: CoreMemoryKind.HighSalience,
      topic: topTopic,
      embedding,
      sourceMemoryId: summary.id,
      clock: this.clock,
    });
    await this.core.addAsync(memory);
    return 1;
  }

  private async promoteClusterToCore(cluster: SemanticMemoryCluster): Promise<number> {
    const memory = createCoreMemory({
      statement:
        `"${cluster.topic}" has been a recurring theme ` +
        `(week of ${cluster.weekStartingMonday}).`,
      kind: CoreMemoryKind.PatternInferred,
      topic: cluster.topic,
      embedding: cluster.centroidEmbedding,
      sourceMemoryId: cluster.id,
      clock: this.clock,
    });
    await this.core.addAsync(memory);
    return 1;
  }

  // ── Retention ────────────────────────────────────────────────────────────

  private async pruneEpisodic(now: Date): Promise<number> {
    const cutoff = new Date(now.getTime());
    cutoff.setUTCDate(cutoff.getUTCDate() - this.options.episodicRetentionDays);
    return this.episodic.pruneOlderThanAsync(cutoff);
  }

  private async pruneDailies(now: Date): Promise<number> {
    const cutoff = addDays(dayKeyOf(now), -this.options.dailyRetentionDays);
    return this.daily.pruneOlderThanAsync(cutoff);
  }

  private async pruneSemantics(now: Date): Promise<number> {
    const cutoff = addDays(dayKeyOf(now), -this.options.semanticRetentionDays);
    return this.semantic.pruneOlderThanAsync(cutoff);
  }
}

/**
 * Approximates the persona at the start of the period by subtracting the
 * in-period gains from the current persona. Conservative — when in doubt it
 * shows no change. Faithful port of ReconstructPersonaBeforeAsync.
 */
function reconstructPersonaBefore(
  after: PersonaState,
  daysInPeriod: readonly DailyMemorySummary[],
  prior: PersonaDeltaSnapshot,
): PersonaState {
  const before = new PersonaState();
  before.userId = after.userId;
  before.verbosity = prior.verbosityAfter;
  before.formality = prior.formalityAfter;
  before.preferredLocale = after.preferredLocale;
  let episodeSum = 0;
  for (const d of daysInPeriod) episodeSum += d.episodeCount;
  before.totalInteractions = after.totalInteractions - episodeSum;
  before.positiveSignals = Math.max(
    0,
    after.positiveSignals - clampPositive(prior.netSignalDelta),
  );
  before.negativeSignals = after.negativeSignals;

  // Carry over topic weights minus the strongest in-period gains.
  before.topicWeights = {};
  for (const [topic, w] of Object.entries(after.topicWeights)) {
    const delta = prior.strengthenedTopics.get(topic);
    before.topicWeights[topic] = delta != null ? Math.max(0, w - delta) : w;
  }
  before.disfavouredTopics = new Set(after.disfavouredTopics);
  return before;
}

function newPersona(userId: string): PersonaState {
  const p = new PersonaState();
  p.userId = userId;
  return p;
}

function clampPositive(v: number): number {
  return v < 0 ? 0 : v;
}
