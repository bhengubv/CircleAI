/**
 * B!'s current emotional/engagement state — the "HER affect layer".
 * Five float dimensions, all 0.0–1.0. Persisted per-user and injected
 * into the system prompt to shape response tone and initiative.
 */
export declare class AffectState {
    userId: string;
    lastUpdatedUtc: Date;
    /** 0=bored, 1=fascinated. Drives proactive questions. */
    curiosity: number;
    /** 0=disengaged, 1=fully engaged. Rises with frequent quality interactions. */
    engagement: number;
    /** 0=confident, 1=confused. High = ask clarifying questions. */
    uncertainty: number;
    /** 0=stranger, 1=deep rapport. Grows slowly over many sessions. */
    rapport: number;
    /** 0=subdued, 1=energetic. Mirrors time-of-day and interaction pace. */
    energy: number;
    /** Apply a positive interaction: nudge Engagement and Rapport up slightly. */
    applyPositiveSignal(): void;
    /** Apply a negative interaction: nudge Engagement down. */
    applyNegativeSignal(): void;
    /**
     * Apply idle time decay: Engagement and Energy drift back toward 0.5.
     * @param idleHours Number of hours the user has been idle.
     */
    applyIdleDecay(idleHours: number): void;
    /**
     * Builds a compact affect hint for injection into the system prompt.
     * Only emits lines that deviate meaningfully from neutral (0.5).
     */
    toSystemPromptHint(): string;
    private _lerp;
}
/**
 * A single recorded episode (one user↔assistant exchange) stored in
 * IEpisodicMemoryStore.
 */
export interface EpisodicMemoryEntry {
    readonly id: string;
    readonly recordedAtUtc: Date;
    readonly userText: string;
    readonly assistantText: string;
    readonly appContext?: string;
    /** L2-normalised embedding, null if embedding backend was unavailable. */
    readonly embedding?: number[];
    readonly tags?: Record<string, string>;
}
/** Polarity of a user feedback signal. */
export declare enum FeedbackPolarity {
    /** User explicitly approved / up-voted the response. */
    Positive = 1,
    /** User explicitly rejected / down-voted the response. */
    Negative = -1,
    /**
     * User provided a correction (neutral polarity, but carries the
     * preferred text in FeedbackSignal.correctedText).
     */
    Correction = 0
}
/**
 * A single user-feedback event tied to a specific B! response.
 * Stored by IFeedbackStore for later analysis and potential on-device adaptation.
 */
export interface FeedbackSignal {
    readonly id: string;
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
/**
 * B!'s dynamic persona state for a specific user. Persisted between
 * sessions and injected into the system prompt to shape tone, vocabulary,
 * and topical depth.
 */
export declare class PersonaState {
    userId: string;
    lastUpdatedUtc: Date;
    /** "brief" | "balanced" (default) | "detailed" */
    verbosity: string;
    /** "casual" | "neutral" (default) | "formal" */
    formality: string;
    /**
     * Preferred response language/locale (IETF BCP-47).
     * null means "match the device locale".
     */
    preferredLocale: string | null;
    /**
     * Weighted topic interests accumulated from positive interactions.
     * Key = normalised topic label, Value = accumulated positive-signal weight.
     */
    topicWeights: Record<string, number>;
    /** Topics the user has down-voted or explicitly rejected. */
    disfavouredTopics: Set<string>;
    totalInteractions: number;
    positiveSignals: number;
    negativeSignals: number;
    /**
     * Derived satisfaction score 0.0–1.0.
     * Returns null when insufficient data (fewer than 10 signals).
     */
    get satisfactionScore(): number | null;
    /**
     * Builds a compact persona instruction block suitable for prepending
     * to the B! system prompt. Returns an empty string when the persona
     * is in its default/unlearned state.
     */
    toSystemPromptHint(): string;
}
/** Lifecycle state of a Goal. */
export declare enum GoalStatus {
    /** Goal is currently being pursued. */
    Active = "Active",
    /** Goal has been achieved. */
    Completed = "Completed",
    /** Goal has been abandoned without completion. */
    Abandoned = "Abandoned"
}
/** Relative importance of a Goal. */
export declare enum GoalPriority {
    /** Nice-to-have; may be deferred. */
    Low = "Low",
    /** Standard importance. */
    Normal = "Normal",
    /** Urgent or critical to the user. */
    High = "High"
}
/**
 * A user goal that B! tracks and proactively helps with.
 * Inspired by the way Samantha in *Her* remembered what Theodore cared about.
 */
export declare class Goal {
    id: string;
    userId: string;
    title: string;
    description: string;
    status: GoalStatus;
    priority: GoalPriority;
    createdUtc: Date;
    dueUtc?: Date;
    completedUtc?: Date;
    notes?: string;
    /**
     * Fraction of the goal completed, in the range [0.0, 1.0].
     * 0.0 = not started; 1.0 = fully achieved.
     */
    progress: number;
    /**
     * Returns a new Goal with progress advanced by delta, clamped to [0.0, 1.0].
     * Does not mutate this instance.
     */
    advanceProgress(delta: number): Goal;
}
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
