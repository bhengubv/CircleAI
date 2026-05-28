// memory.ts
//
// B!'s memory layer: affect state, episodic memory, persona, feedback, and goals.
// The "HER affect layer" — five float dimensions that shape tone, initiative,
// and communication style over time.

// ---------------------------------------------------------------------------
// AffectState
// ---------------------------------------------------------------------------

/**
 * B!'s current emotional/engagement state — the "HER affect layer".
 * Five float dimensions, all 0.0–1.0. Persisted per-user and injected
 * into the system prompt to shape response tone and initiative.
 */
export class AffectState {
  /** Opaque user identifier (device ID or hashed phone number). Never contains PII. */
  userId: string = 'default';

  /** UTC time of the last update to this affect state. */
  lastUpdatedAt: Date = new Date();

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
    this.engagement  = Math.min(1.0, this.engagement  + 0.02);
    this.rapport     = Math.min(1.0, this.rapport     + 0.01);
    this.uncertainty = Math.max(0.0, this.uncertainty - 0.02);
    this.lastUpdatedAt = new Date();
  }

  /** Apply a negative interaction: nudge Engagement down. */
  applyNegativeSignal(): void {
    this.engagement  = Math.max(0.0, this.engagement  - 0.03);
    this.uncertainty = Math.min(1.0, this.uncertainty + 0.03);
    this.lastUpdatedAt = new Date();
  }

  /**
   * Apply idle time decay: Engagement and Energy drift back toward 0.5.
   * @param idleHours Number of idle hours elapsed.
   */
  applyIdleDecay(idleHours: number): void {
    const decay = Math.min(0.3, idleHours * 0.02);
    this.engagement = AffectState.lerp(this.engagement, 0.5, decay);
    this.energy     = AffectState.lerp(this.energy,     0.5, decay);
    this.lastUpdatedAt = new Date();
  }

  private static lerp(a: number, b: number, t: number): number {
    t = Math.max(0.0, Math.min(1.0, t));
    return a + (b - a) * t;
  }

  /**
   * Builds a compact affect hint for injection into the system prompt.
   * Only emits lines that deviate meaningfully from neutral.
   */
  toSystemPromptHint(): string {
    const hints: string[] = [];
    if (this.curiosity   > 0.7) hints.push('You are deeply curious about this topic — ask a follow-up question.');
    if (this.engagement  > 0.7) hints.push('You are fully engaged — be enthusiastic and thorough.');
    if (this.engagement  < 0.3) hints.push('Keep your response brief and to the point.');
    if (this.uncertainty > 0.6) hints.push('You are uncertain — ask a clarifying question before answering.');
    if (this.rapport     > 0.7) hints.push('You know this user well — use a warm, familiar tone.');
    if (this.energy      < 0.3) hints.push('Keep your response calm and measured.');
    if (this.energy      > 0.8) hints.push('You are energetic — be upbeat and concise.');
    if (hints.length === 0) return '';
    return '[Affect state]\n' + hints.join('\n') + '\n';
  }
}

// ---------------------------------------------------------------------------
// AffectVad — derived Russell-PAD view of AffectState
// ---------------------------------------------------------------------------

/**
 * Derived Russell-PAD (Pleasure-Arousal-Dominance) view of an {@link AffectState}.
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
  /**
   * Pleasure ↔ displeasure axis. 1.0 = maximally pleasant,
   * 0.0 = maximally unpleasant.
   */
  readonly valence: number;

  /**
   * Activation ↔ deactivation axis. 1.0 = maximally aroused/alert,
   * 0.0 = maximally calm/dormant.
   */
  readonly arousal: number;

  /**
   * In-control ↔ submissive axis. 1.0 = maximally in control,
   * 0.0 = maximally submissive/overwhelmed.
   */
  readonly dominance: number;
}

/**
 * Computes the {@link AffectVad} projection of an {@link AffectState} using
 * the canonical fixture derivation. Output components are clamped to [0, 1].
 */
export function affectStateToVad(state: AffectState): AffectVad {
  const v = (state.engagement + state.rapport + (1 - state.uncertainty)) / 3;
  const a = (state.energy * 2 + state.curiosity + state.uncertainty) / 4;
  const d = (state.engagement + (1 - state.uncertainty)) / 2;
  const clamp01 = (x: number) => Math.min(1, Math.max(0, x));
  return { valence: clamp01(v), arousal: clamp01(a), dominance: clamp01(d) };
}

// ---------------------------------------------------------------------------
// EpisodicMemoryEntry
// ---------------------------------------------------------------------------

/**
 * A single recorded episode (one user↔assistant exchange) stored in IEpisodicMemoryStore.
 */
export interface EpisodicMemoryEntry {
  /** Stable identifier for the entry (UUID v4). */
  readonly id: string;

  /** UTC timestamp of the assistant's response. */
  readonly recordedAt: Date;

  /** The user's message text. */
  readonly userText: string;

  /** The assistant's response text. */
  readonly assistantText: string;

  /**
   * Optional identifier for the app context in which the exchange happened
   * (e.g. "tgn.bidbaas").
   */
  readonly appContext: string | null;

  /**
   * L2-normalised embedding of userText + " " + assistantText, pre-computed at write time.
   * null if the embedding backend was unavailable when the entry was stored.
   */
  readonly embedding: number[] | null;

  /** Arbitrary key-value tags (e.g. locale, sentiment). */
  readonly tags: Record<string, string> | null;
}

// ---------------------------------------------------------------------------
// FeedbackPolarity & FeedbackSignal
// ---------------------------------------------------------------------------

/** Polarity of the feedback signal. */
export enum FeedbackPolarity {
  /** User explicitly approved / up-voted the response. */
  Positive  =  1,
  /** User explicitly rejected / down-voted the response. */
  Negative  = -1,
  /**
   * User provided a correction (neutral polarity, but carries the preferred
   * text in correctedText).
   */
  Correction = 0,
}

/**
 * A single user-feedback event tied to a specific B! response.
 * Stored by IFeedbackStore for later analysis and potential on-device adaptation.
 */
export interface FeedbackSignal {
  /** Stable identifier for the signal (UUID v4). */
  readonly id: string;

  /** UTC time when the user provided the signal. */
  readonly recordedAt: Date;

  /**
   * The EpisodicMemoryEntry id of the episode this feedback refers to,
   * if the exchange was also stored episodically. null otherwise.
   */
  readonly episodeId: string | null;

  /** The user's original message. */
  readonly userText: string;

  /** B!'s response that is being rated. */
  readonly assistantText: string;

  /** User's rating. */
  readonly polarity: FeedbackPolarity;

  /**
   * For Correction signals — the user's preferred response that should have been given.
   */
  readonly correctedText: string | null;

  /** Free-text comment the user optionally attached to the signal. */
  readonly comment: string | null;
}

// ---------------------------------------------------------------------------
// PersonaState
// ---------------------------------------------------------------------------

/**
 * B!'s dynamic persona state for a specific user. Persisted between sessions
 * and injected into the system prompt to shape tone, vocabulary, and topical depth.
 */
export class PersonaState {
  /** Opaque user identifier (device ID or hashed phone number). Never contains PII. */
  userId: string = 'default';

  /** UTC time of the last update to this persona. */
  lastUpdatedAt: Date = new Date();

  /**
   * Preferred response verbosity inferred from feedback:
   * "brief", "balanced" (default), or "detailed".
   */
  verbosity: string = 'balanced';

  /**
   * Formality level inferred from the user's own language:
   * "casual", "neutral" (default), or "formal".
   */
  formality: string = 'neutral';

  /**
   * Preferred response language/locale (IETF BCP-47).
   * null means "match the device locale".
   */
  preferredLocale: string | null = null;

  /**
   * Weighted topic interests accumulated from positive interactions.
   * Key = normalised topic label (e.g. "finance", "sport"),
   * Value = accumulated positive-signal weight (unbounded positive float).
   */
  topicWeights: Record<string, number> = {};

  /** Topics the user has down-voted or explicitly rejected. */
  disfavouredTopics: Set<string> = new Set();

  /** Total number of recorded interactions with this persona. */
  totalInteractions: number = 0;

  /** Cumulative positive feedback signals. */
  positiveSignals: number = 0;

  /** Cumulative negative feedback signals. */
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
   * Builds a compact persona instruction block suitable for prepending to the
   * B! system prompt. Returns an empty string when the persona is in its
   * default/unlearned state.
   */
  toSystemPromptHint(): string {
    const hints: string[] = [];
    if (this.verbosity !== 'balanced') hints.push(`Keep responses ${this.verbosity}.`);
    if (this.formality === 'casual') hints.push('Use a casual, friendly tone.');
    else if (this.formality === 'formal') hints.push('Maintain a formal, professional tone.');
    if (this.preferredLocale && this.preferredLocale.trim().length > 0) {
      hints.push(`Respond in the language appropriate for locale ${this.preferredLocale}.`);
    }
    if (hints.length === 0) return '';
    return '[User preferences]\n' + hints.join('\n') + '\n';
  }
}

// ---------------------------------------------------------------------------
// Goal
// ---------------------------------------------------------------------------

/** Lifecycle state of a Goal. */
export enum GoalStatus {
  /** Goal is currently being pursued. */
  Active    = 'Active',
  /** Goal has been achieved. */
  Completed = 'Completed',
  /** Goal has been abandoned without completion. */
  Abandoned = 'Abandoned',
}

/** Relative importance of a Goal. */
export enum GoalPriority {
  /** Nice-to-have; may be deferred. */
  Low    = 'Low',
  /** Standard importance. */
  Normal = 'Normal',
  /** Urgent or critical to the user. */
  High   = 'High',
}

/**
 * A user goal that B! tracks and proactively helps with.
 * Inspired by the way Samantha in *Her* remembered what Theodore cared about.
 */
export class Goal {
  /** Unique stable identifier for this goal. */
  id: string = '';
  /** Owner of this goal. */
  userId: string = '';
  /** Short, human-readable title. */
  title: string = '';
  /** Full description of what the user wants to achieve. */
  description: string = '';
  /** Current lifecycle state. */
  status: GoalStatus = GoalStatus.Active;
  /** Relative importance. */
  priority: GoalPriority = GoalPriority.Normal;
  /** When this goal was first recorded (UTC). */
  createdUtc: Date = new Date();
  /** Optional deadline (UTC). */
  dueUtc?: Date;
  /** When the goal was completed or abandoned (UTC). */
  completedUtc?: Date;
  /** Freeform notes B! or the user has attached to this goal. */
  notes?: string;

  /**
   * Fraction of the goal completed, in the range [0.0, 1.0].
   * 0.0 = not started; 1.0 = fully achieved.
   */
  progress: number = 0.0;

  /**
   * Returns a NEW Goal with progress advanced by delta, clamped to [0.0, 1.0].
   * Does not mutate this instance.
   *
   * Formula: new_progress = clamp(progress + delta, 0.0, 1.0)
   */
  advanceProgress(delta: number): Goal {
    const g = Object.assign(new Goal(), this);
    g.progress = Math.max(0, Math.min(1, this.progress + delta));
    return g;
  }
}

// ---------------------------------------------------------------------------
// Store interfaces
// ---------------------------------------------------------------------------

/** Loads and persists AffectState for a specific user. */
export abstract class IAffectStore {
  /**
   * Loads the affect state for userId.
   * Returns a fresh default state when none is found.
   */
  abstract load(userId: string): Promise<AffectState>;

  /**
   * Persists the affect state. Implementations must be crash-safe
   * (write-then-swap or similar) to avoid partial writes.
   */
  abstract save(state: AffectState): Promise<void>;
}

/**
 * Persistent store for episodic memories (conversational exchanges + embeddings).
 * Implementations may be in-memory (tests/edge), SQLite-vec (production on-device),
 * or a remote vector database.
 */
export abstract class IEpisodicMemoryStore {
  /**
   * Appends a new entry to the store.
   */
  abstract add(entry: EpisodicMemoryEntry): Promise<void>;

  /**
   * Returns the topK entries whose embeddings are most similar (cosine) to
   * queryEmbedding. When queryEmbedding is null, falls back to recency
   * (most recent topK entries).
   */
  abstract search(queryEmbedding: number[] | null, topK?: number): Promise<readonly EpisodicMemoryEntry[]>;

  /**
   * Returns the most recent count entries ordered newest-first.
   */
  abstract getRecent(count?: number): Promise<readonly EpisodicMemoryEntry[]>;

  /** Total number of entries currently stored. */
  abstract count(): Promise<number>;

  /**
   * Removes all entries older than cutoff.
   * Returns the number of entries removed.
   */
  abstract pruneOlderThan(cutoff: Date): Promise<number>;
}

/** Loads and persists PersonaState for a specific user. */
export abstract class IPersonaStore {
  /**
   * Loads the persona for userId.
   * Returns a fresh default persona when none is found.
   */
  abstract load(userId: string): Promise<PersonaState>;

  /**
   * Persists the persona. The implementation must be crash-safe
   * (write-then-swap or similar) to avoid partial writes.
   */
  abstract save(persona: PersonaState): Promise<void>;
}

/** Persists user feedback signals for later analysis and on-device adaptation. */
export abstract class IFeedbackStore {
  /** Records a new feedback signal. */
  abstract add(signal: FeedbackSignal): Promise<void>;

  /**
   * Returns the most recent count signals, newest-first.
   */
  abstract getRecent(count?: number): Promise<readonly FeedbackSignal[]>;

  /** Total number of signals stored. */
  abstract count(): Promise<number>;

  /**
   * Returns the fraction of stored signals that are Positive (0.0–1.0).
   * Returns null when no signals are available.
   */
  abstract positiveRatio(): Promise<number | null>;
}

/** Persists and retrieves Goal records for a user. */
export abstract class IGoalStore {
  /** Returns all goals for the given user, in any order. */
  abstract list(userId: string): Promise<readonly Goal[]>;

  /**
   * Returns the goal with the given id, or null if it does not exist.
   */
  abstract get(id: string): Promise<Goal | null>;

  /**
   * Inserts or replaces the goal. The goal's id is the natural key.
   * Returns the stored goal.
   */
  abstract upsert(goal: Goal): Promise<Goal>;

  /**
   * Deletes the goal with the given id. No-op if not found.
   */
  abstract delete(id: string): Promise<void>;

  /**
   * Returns all goals for userId where status is GoalStatus.Active.
   */
  abstract getActive(userId: string): Promise<readonly Goal[]>;
}
