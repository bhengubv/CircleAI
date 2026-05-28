// memory/index.ets — Circle AI memory layer
// ArkTS port: no `any`, class-based AffectState, explicit enum types.

// ---------------------------------------------------------------------------
// AffectState
// ---------------------------------------------------------------------------

/**
 * Circle AI's current emotional/engagement state — the "HER affect layer".
 * Five float dimensions, all 0.0–1.0. Persisted per-user and injected
 * into the system prompt to shape response tone and initiative.
 *
 * CRITICAL MATH: all operations must match fixtures/affect_state.json within 1e-5.
 */
export class AffectState {
  /** Opaque user identifier. Never contains PII. */
  userId: string;

  /** Unix millisecond timestamp of the last update. */
  lastUpdatedAt: number = Date.now();

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

  constructor(userId: string = 'default') {
    this.userId = userId;
  }

  /**
   * Apply a positive interaction signal.
   * engagement += 0.02, rapport += 0.01, uncertainty -= 0.02
   */
  applyPositiveSignal(): void {
    this.engagement  = Math.min(1, Math.max(0, this.engagement  + 0.02));
    this.rapport     = Math.min(1, Math.max(0, this.rapport     + 0.01));
    this.uncertainty = Math.min(1, Math.max(0, this.uncertainty - 0.02));
    this.lastUpdatedAt = Date.now();
  }

  /**
   * Apply a negative interaction signal.
   * engagement -= 0.03, uncertainty += 0.03
   */
  applyNegativeSignal(): void {
    this.engagement  = Math.min(1, Math.max(0, this.engagement  - 0.03));
    this.uncertainty = Math.min(1, Math.max(0, this.uncertainty + 0.03));
    this.lastUpdatedAt = Date.now();
  }

  /**
   * Apply idle time decay.
   * decay = min(0.3, hours * 0.02)
   * engagement and energy lerp toward 0.5 by `decay`.
   * lerp(a, b, t) = a + (b - a) * clamp(t, 0, 1)
   *
   * @param hours Number of idle hours elapsed.
   */
  applyIdleDecay(hours: number): void {
    const decay = Math.min(0.3, hours * 0.02);
    this.engagement = affectLerp(this.engagement, 0.5, decay);
    this.energy     = affectLerp(this.energy,     0.5, decay);
    this.lastUpdatedAt = Date.now();
  }

  /**
   * Builds a compact affect hint for injection into the system prompt.
   * Only emits lines that deviate meaningfully from neutral.
   */
  toSystemPromptHint(): string {
    const hints: string[] = [];
    if (this.curiosity   > 0.7) hints.push('You are deeply curious about this topic.');
    if (this.engagement  > 0.7) hints.push('You are fully engaged.');
    if (this.engagement  < 0.3) hints.push('Keep your response brief.');
    if (this.uncertainty > 0.6) hints.push('You are uncertain.');
    if (this.rapport     > 0.7) hints.push('You know this user well.');
    if (hints.length === 0) return '';
    return '[Affect state]\n' + hints.join('\n') + '\n';
  }
}

/**
 * Linear interpolation used in idle decay.
 * t is clamped to [0, 1].
 * CRITICAL: uses plain scalar number arithmetic only — no Float32Array shortcuts.
 */
function affectLerp(a: number, b: number, t: number): number {
  const tc: number = Math.min(1, Math.max(0, t));
  return a + (b - a) * tc;
}

// ---------------------------------------------------------------------------
// Goal
// ---------------------------------------------------------------------------

/** Lifecycle state of a Goal. */
export enum GoalStatus {
  Active    = 0,
  Completed = 1,
  Abandoned = 2,
}

/** Relative importance of a Goal. */
export enum GoalPriority {
  Low    = 0,
  Normal = 1,
  High   = 2,
}

/**
 * A user goal that Circle AI tracks and proactively helps with.
 */
export interface Goal {
  id: string;
  userId: string;
  title: string;
  description: string;
  status: GoalStatus;
  priority: GoalPriority;
  createdAt: number;       // unix ms
  dueAt?: number;          // unix ms, optional
  completedAt?: number;    // unix ms, optional
  notes?: string;
  progress: number;        // 0.0..1.0
}

/**
 * Returns a new Goal with progress advanced by delta, clamped to [0, 1].
 * Formula: new_progress = clamp(progress + delta, 0.0, 1.0)
 */
export function advanceProgress(goal: Goal, delta: number): Goal {
  const newProgress: number = Math.min(1, Math.max(0, goal.progress + delta));
  return {
    id:          goal.id,
    userId:      goal.userId,
    title:       goal.title,
    description: goal.description,
    status:      goal.status,
    priority:    goal.priority,
    createdAt:   goal.createdAt,
    dueAt:       goal.dueAt,
    completedAt: goal.completedAt,
    notes:       goal.notes,
    progress:    newProgress,
  };
}

// ---------------------------------------------------------------------------
// EpisodicMemoryEntry
// ---------------------------------------------------------------------------

/**
 * A single recorded episode stored in the episodic memory store.
 */
export interface EpisodicMemoryEntry {
  id: string;
  userId: string;
  content: string;
  embedding: number[];
  createdAt: number;   // unix ms
  tags: string[];
  importance: number;  // 0.0..1.0
}

// ---------------------------------------------------------------------------
// FeedbackSignal
// ---------------------------------------------------------------------------

/** Polarity of the feedback signal. */
export enum FeedbackPolarity {
  Positive = 0,
  Negative = 1,
  Neutral  = 2,
}

/**
 * A single user-feedback event tied to a specific response.
 */
export interface FeedbackSignal {
  id: string;
  userId: string;
  turnId: string;
  polarity: FeedbackPolarity;
  note?: string;
  recordedAt: number;  // unix ms
}

// ---------------------------------------------------------------------------
// PersonaState
// ---------------------------------------------------------------------------

/**
 * Circle AI's dynamic persona state for a specific user.
 */
export interface PersonaState {
  id: string;
  userId: string;
  name: string;
  traitSummary: string;
  satisfactionScore: number;   // 0.0..1.0
  lastUpdatedAt: number;       // unix ms
}

// ---------------------------------------------------------------------------
// Store interfaces
// ---------------------------------------------------------------------------

/** Loads and persists episodic memories. */
export interface EpisodicMemoryStore {
  save(entry: EpisodicMemoryEntry): Promise<void>;
  getRecent(userId: string, limit: number): Promise<EpisodicMemoryEntry[]>;
  delete(id: string): Promise<void>;
}

/** Loads and persists AffectState for a specific user. */
export interface AffectStore {
  load(userId: string): Promise<AffectState | null>;
  save(state: AffectState): Promise<void>;
}

/** Loads and persists Goal records for a user. */
export interface GoalStore {
  save(goal: Goal): Promise<void>;
  getActive(userId: string): Promise<Goal[]>;
  delete(id: string): Promise<void>;
}
