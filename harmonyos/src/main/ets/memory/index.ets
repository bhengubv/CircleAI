/**
 * memory/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// memory/index.ts
// B!'s affect state, episodic memory, feedback signals, persona, and goals.
// Ported from Circle.AI.Memory (C#).

// ─────────────────────────────────────────────────────────────────────────────
// AffectState
// ─────────────────────────────────────────────────────────────────────────────

/**
 * B!'s current emotional/engagement state — the "HER affect layer".
 * Five float dimensions, all 0.0–1.0. Persisted per-user and injected
 * into the system prompt to shape response tone and initiative.
 */
export class AffectState {
  userId: string = "default";
  lastUpdatedUtc: Date = new Date();

  /** 0=bored, 1=fascinated. Drives proactive questions. */
  curiosity: number = 0.5;

  /** 0=disengaged, 1=fully engaged. Rises with frequent quality interactions. */
  engagement: number = 0.5;

  /** 0=confident, 1=confused. High = ask clarifying questions. */
  uncertainty: number = 0.2;

  /** 0=stranger, 1=deep rapport. Grows slowly over many sessions. */
  rapport: number = 0.0;

  /** 0=subdued, 1=energetic. Mirrors time-of-day and interaction pace. */
  energy: number = 0.5;

  /** Apply a positive interaction: nudge Engagement and Rapport up slightly. */
  applyPositiveSignal(): void {
    this.engagement = Math.min(1, this.engagement + 0.02);
    this.rapport = Math.min(1, this.rapport + 0.01);
    this.uncertainty = Math.max(0, this.uncertainty - 0.02);
    this.lastUpdatedUtc = new Date();
  }

  /** Apply a negative interaction: nudge Engagement down. */
  applyNegativeSignal(): void {
    this.engagement = Math.max(0, this.engagement - 0.03);
    this.uncertainty = Math.min(1, this.uncertainty + 0.03);
    this.lastUpdatedUtc = new Date();
  }

  /**
   * Apply idle time decay: Engagement and Energy drift back toward 0.5.
   * @param idleHours Number of hours the user has been idle.
   */
  applyIdleDecay(idleHours: number): void {
    const decay = Math.min(0.3, idleHours * 0.02);
    this.engagement = this._lerp(this.engagement, 0.5, decay);
    this.energy = this._lerp(this.energy, 0.5, decay);
    this.lastUpdatedUtc = new Date();
  }

  /**
   * Builds a compact affect hint for injection into the system prompt.
   * Only emits lines that deviate meaningfully from neutral (0.5).
   */
  toSystemPromptHint(): string {
    const hints: string[] = [];

    if (this.curiosity > 0.7)
      hints.push("You are deeply curious about this topic — ask a follow-up question.");
    if (this.engagement > 0.7)
      hints.push("You are fully engaged — be enthusiastic and thorough.");
    if (this.engagement < 0.3)
      hints.push("Keep your response brief and to the point.");
    if (this.uncertainty > 0.6)
      hints.push("You are uncertain — ask a clarifying question before answering.");
    if (this.rapport > 0.7)
      hints.push("You know this user well — use a warm, familiar tone.");
    if (this.energy < 0.3)
      hints.push("Keep your response calm and measured.");
    if (this.energy > 0.8)
      hints.push("You are energetic — be upbeat and concise.");

    if (hints.length === 0) return "";
    return "[Affect state]\n" + hints.join("\n") + "\n";
  }

  private _lerp(a: number, b: number, t: number): number {
    return a + (b - a) * Math.max(0, Math.min(1, t));
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// AffectVad — derived Russell-PAD view of AffectState
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Derived Russell-PAD (Pleasure-Arousal-Dominance) view of an AffectState.
 *
 * The Circle AI SDK uses a 5-dimensional affect model (curiosity, engagement,
 * uncertainty, rapport, energy). Some downstream systems — including external
 * affective-computing research tooling and HR/health analytics pipelines —
 * expect Russell's PAD/VAD model. AffectVad is the DERIVED 3-dimensional view
 * of the same underlying state; it does not replace AffectState.
 *
 * All three dimensions are in [0.0, 1.0].
 *
 * Derivation (all results clamped to [0.0, 1.0]):
 *   Valence   = (engagement + rapport + (1 - uncertainty)) / 3
 *   Arousal   = (energy * 2 + curiosity + uncertainty) / 4
 *   Dominance = (engagement + (1 - uncertainty)) / 2
 *
 * These formulas are the cross-language fixture contract — see
 * fixtures/affect_vad_derivation.json. Any change to the math must update
 * every port and every fixture vector.
 */
export interface AffectVad {
  /** Pleasure ↔ displeasure axis. 1.0 = pleasant, 0.0 = unpleasant. */
  readonly valence: number;
  /** Activation ↔ deactivation axis. 1.0 = aroused/alert, 0.0 = calm/dormant. */
  readonly arousal: number;
  /** In-control ↔ submissive axis. 1.0 = in control, 0.0 = overwhelmed. */
  readonly dominance: number;
}

/**
 * Computes the AffectVad projection of an AffectState using the canonical
 * fixture derivation. Output components are clamped to [0, 1].
 */
export function affectStateToVad(state: AffectState): AffectVad {
  const v = (state.engagement + state.rapport + (1 - state.uncertainty)) / 3;
  const a = (state.energy * 2 + state.curiosity + state.uncertainty) / 4;
  const d = (state.engagement + (1 - state.uncertainty)) / 2;
  const clamp01 = (x: number) => Math.min(1, Math.max(0, x));
  return { valence: clamp01(v), arousal: clamp01(a), dominance: clamp01(d) };
}

// ─────────────────────────────────────────────────────────────────────────────
// EpisodicMemoryEntry
// ─────────────────────────────────────────────────────────────────────────────

/**
 * A single recorded episode (one user↔assistant exchange) stored in
 * IEpisodicMemoryStore.
 */
export interface EpisodicMemoryEntry {
  readonly id: string; // UUID v4
  readonly recordedAtUtc: Date;
  readonly userText: string;
  readonly assistantText: string;
  readonly appContext?: string;
  /** L2-normalised embedding, null if embedding backend was unavailable. */
  readonly embedding?: number[];
  readonly tags?: Map<string, string>;
}

// ─────────────────────────────────────────────────────────────────────────────
// FeedbackPolarity + FeedbackSignal
// ─────────────────────────────────────────────────────────────────────────────

/** Polarity of a user feedback signal. */
export enum FeedbackPolarity {
  /** User explicitly approved / up-voted the response. */
  Positive = 1,
  /** User explicitly rejected / down-voted the response. */
  Negative = -1,
  /**
   * User provided a correction (neutral polarity, but carries the
   * preferred text in FeedbackSignal.correctedText).
   */
  Correction = 0,
}

/**
 * A single user-feedback event tied to a specific B! response.
 * Stored by IFeedbackStore for later analysis and potential on-device adaptation.
 */
export interface FeedbackSignal {
  readonly id: string; // UUID v4
  readonly recordedAtUtc: Date;
  /** The EpisodicMemoryEntry.id this feedback refers to, if applicable. */
  readonly episodeId?: string;
  readonly userText: string;
  readonly assistantText: string;
  readonly polarity: FeedbackPolarity;
  /** For Correction signals — the user's preferred response. */
  readonly correctedText?: string;
  readonly comment?: string;
}

// ─────────────────────────────────────────────────────────────────────────────
// PersonaState
// ─────────────────────────────────────────────────────────────────────────────

/**
 * B!'s dynamic persona state for a specific user. Persisted between
 * sessions and injected into the system prompt to shape tone, vocabulary,
 * and topical depth.
 */
export class PersonaState {
  userId: string = "default";
  lastUpdatedUtc: Date = new Date();

  /** "brief" | "balanced" (default) | "detailed" */
  verbosity: string = "balanced";

  /** "casual" | "neutral" (default) | "formal" */
  formality: string = "neutral";

  /**
   * Preferred response language/locale (IETF BCP-47).
   * null means "match the device locale".
   */
  preferredLocale: string | null = null;

  /**
   * Weighted topic interests accumulated from positive interactions.
   * Key = normalised topic label, Value = accumulated positive-signal weight.
   */
  topicWeights: Map<string, number> = {};

  /** Topics the user has down-voted or explicitly rejected. */
  disfavouredTopics: Set<string> = new Set();

  totalInteractions: number = 0;
  positiveSignals: number = 0;
  negativeSignals: number = 0;

  /**
   * Derived satisfaction score 0.0–1.0.
   * Returns null when insufficient data (fewer than 10 signals).
   */
  get satisfactionScore(): number | null {
    const total = this.positiveSignals + this.negativeSignals;
    if (total < 10) return null;
    return this.positiveSignals / total;
  }

  /**
   * Builds a compact persona instruction block suitable for prepending
   * to the B! system prompt. Returns an empty string when the persona
   * is in its default/unlearned state.
   */
  toSystemPromptHint(): string {
    const hints: string[] = [];

    if (this.verbosity !== "balanced")
      hints.push(`Keep responses ${this.verbosity}.`);

    if (this.formality === "casual")
      hints.push("Use a casual, friendly tone.");
    else if (this.formality === "formal")
      hints.push("Maintain a formal, professional tone.");

    if (this.preferredLocale && this.preferredLocale.trim().length > 0)
      hints.push(`Respond in the language appropriate for locale ${this.preferredLocale}.`);

    if (hints.length === 0) return "";
    return "[User preferences]\n" + hints.join("\n") + "\n";
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Goal enums + Goal class
// ─────────────────────────────────────────────────────────────────────────────

/** Lifecycle state of a Goal. */
export enum GoalStatus {
  /** Goal is currently being pursued. */
  Active = "Active",
  /** Goal has been achieved. */
  Completed = "Completed",
  /** Goal has been abandoned without completion. */
  Abandoned = "Abandoned",
}

/** Relative importance of a Goal. */
export enum GoalPriority {
  /** Nice-to-have; may be deferred. */
  Low = "Low",
  /** Standard importance. */
  Normal = "Normal",
  /** Urgent or critical to the user. */
  High = "High",
}

/**
 * A user goal that B! tracks and proactively helps with.
 * Inspired by the way Samantha in *Her* remembered what Theodore cared about.
 */
export class Goal {
  id: string = "";
  userId: string = "";
  title: string = "";
  description: string = "";
  status: GoalStatus = GoalStatus.Active;
  priority: GoalPriority = GoalPriority.Normal;
  createdUtc: Date = new Date();
  dueUtc?: Date;
  completedUtc?: Date;
  notes?: string;

  /**
   * Fraction of the goal completed, in the range [0.0, 1.0].
   * 0.0 = not started; 1.0 = fully achieved.
   */
  progress: number = 0.0;

  /**
   * Returns a new Goal with progress advanced by delta, clamped to [0.0, 1.0].
   * Does not mutate this instance.
   */
  advanceProgress(delta: number): Goal {
    const g = Object.assign(new Goal(), this);
    g.progress = Math.max(0, Math.min(1, this.progress + delta));
    return g;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Store interfaces
// ─────────────────────────────────────────────────────────────────────────────

/** Loads and persists AffectState for a specific user. */
export interface IAffectStore {
  /** Loads the affect state for userId. Returns a fresh default state when none is found. */
  loadAsync(userId: string): Promise<AffectState>;
  /** Persists the affect state. Implementations must be crash-safe. */
  saveAsync(state: AffectState): Promise<void>;
}

/** Persistent store for episodic memories. */
export interface IEpisodicMemoryStore {
  /** Appends a new entry to the store. */
  addAsync(entry: EpisodicMemoryEntry): Promise<void>;
  /**
   * Returns the topK entries most similar (cosine) to queryEmbedding.
   * Falls back to recency when queryEmbedding is null.
   */
  searchAsync(queryEmbedding: number[] | null, topK?: number): Promise<readonly EpisodicMemoryEntry[]>;
  /** Returns the most recent count entries, newest-first. */
  getRecentAsync(count?: number): Promise<readonly EpisodicMemoryEntry[]>;
  /** Total number of entries currently stored. */
  countAsync(): Promise<number>;
  /**
   * Removes all entries older than cutoff.
   * Returns the number of entries removed.
   */
  pruneOlderThanAsync(cutoff: Date): Promise<number>;
}

/** Persists user feedback signals for later analysis and on-device adaptation. */
export interface IFeedbackStore {
  /** Records a new feedback signal. */
  addAsync(signal: FeedbackSignal): Promise<void>;
  /** Returns the most recent count signals, newest-first. */
  getRecentAsync(count?: number): Promise<readonly FeedbackSignal[]>;
  /** Total number of signals stored. */
  countAsync(): Promise<number>;
  /**
   * Returns the fraction of stored signals that are Positive (0.0–1.0).
   * Returns null when no signals are available.
   */
  positiveRatioAsync(): Promise<number | null>;
}

/** Persists and retrieves Goal records for a user. */
export interface IGoalStore {
  /** Returns all goals for the given user, in any order. */
  listAsync(userId: string): Promise<readonly Goal[]>;
  /** Returns the goal with the given id, or null if it does not exist. */
  getAsync(id: string): Promise<Goal | null>;
  /** Inserts or replaces the goal. Returns the stored goal. */
  upsertAsync(goal: Goal): Promise<Goal>;
  /** Deletes the goal with the given id. No-op if not found. */
  deleteAsync(id: string): Promise<void>;
  /** Returns all active goals for userId. */
  getActiveAsync(userId: string): Promise<readonly Goal[]>;
}

/** Loads and persists PersonaState for a specific user. */
export interface IPersonaStore {
  /** Loads the persona for userId. Returns a fresh default persona when none is found. */
  loadAsync(userId: string): Promise<PersonaState>;
  /** Persists the persona. The implementation must be crash-safe. */
  saveAsync(persona: PersonaState): Promise<void>;
}
