import { AffectState } from './memory';
import { FacialMetricMatrix } from './tools';
/**
 * The surface on which the Companion session is running.
 * Determines sensory capabilities, available UI affordances, and
 * how the Companion adapts its communication style.
 */
export declare enum InterfaceKind {
    /** Mobile phone or tablet (MAUI). */
    Mobile = "Mobile",
    /** Smartwatch or fitness band with a small display. */
    Wearable = "Wearable",
    /** Desktop or laptop computer (MAUI or WPF). */
    Desktop = "Desktop",
    /** Browser-based experience (Blazor). */
    Web = "Web",
    /** Embedded IoT device — voice in, voice out, minimal compute. */
    IoT = "IoT",
    /** Always-on ambient surface — smart speaker, room display, car. */
    Ambient = "Ambient",
    /** Programmatic / background / testing context (no UI). */
    Headless = "Headless"
}
/**
 * Snapshot of all context injected into the Companion's system prompt.
 * Rebuilt at the start of each session and refreshed on request.
 */
export interface CompanionContext {
    readonly identityId: string;
    readonly displayName: string;
    readonly preferredLanguage: string | null;
    readonly interface: InterfaceKind;
    readonly personaHints: string;
    readonly affectSummary: string;
    readonly recentMemorySnippets: readonly string[];
    readonly activeGoals: readonly string[];
    readonly contextBuiltAt: Date;
}
/**
 * A single turn in the Companion conversation log, held in memory for the
 * duration of the session.
 * role is "user" or "assistant".
 */
export interface CompanionTurn {
    readonly role: string;
    readonly content: string;
    readonly timestamp: Date;
}
/**
 * Metadata emitted when the Companion proactively initiates contact.
 */
export interface CompanionProactiveEvent {
    readonly sessionId: string;
    readonly identityId: string;
    readonly interface: InterfaceKind;
    readonly message: string;
    readonly triggerName: string;
    readonly generatedAt: Date;
}
/**
 * A Companion conversation session. Combines identity awareness, cross-device
 * memory, language adaptation, affect sensing, and proactive reasoning into a
 * single coherent interface.
 */
export declare abstract class ICompanionSession {
    /** Stable unique identifier for this session. */
    abstract readonly sessionId: string;
    /** The authenticated identity driving this session. */
    abstract readonly identityId: string;
    /** The surface on which this session is running. */
    abstract readonly interface: InterfaceKind;
    /**
     * Send a message to the Companion and receive a complete reply.
     * Context enrichment (identity, memory, persona, affect, language) is applied automatically.
     */
    abstract send(message: string): Promise<string>;
    /**
     * Stream the Companion's reply token-by-token for low-latency rendering.
     * Each yielded string is the next chunk to append to the output.
     */
    abstract stream(message: string): AsyncGenerator<string, void, unknown>;
    /**
     * Agentic mode: sends the instruction, detects tool calls in the reply,
     * executes them, and re-prompts until the model produces a plain-text answer.
     */
    abstract agent(instruction: string): Promise<string>;
    /**
     * Returns the most recent CompanionContext snapshot, including identity,
     * persona hints, affect summary, and recent memories.
     */
    abstract getContext(): CompanionContext;
    /**
     * Refreshes the context from backing stores (memory, persona, affect).
     * Call after significant state changes (e.g. new goal set, mood shift).
     */
    abstract refreshContext(): Promise<void>;
    /** The in-session conversation history (this session only, not persisted). */
    abstract readonly history: readonly CompanionTurn[];
    /**
     * Signal satisfaction with the last reply. Used to evolve the persona
     * and communication style over time.
     */
    abstract signalFeedback(positive: boolean, note?: string | null): Promise<void>;
    /**
     * Subscribe to proactive messages from the Companion — goal check-ins,
     * mood-triggered nudges, or scheduled reminders.
     * Returns an unsubscribe function.
     */
    abstract onProactiveMessage(handler: (event: CompanionProactiveEvent) => void): () => void;
    /** Dispose the session and release all resources. */
    abstract dispose(): Promise<void>;
}
/**
 * Minimum confidence score for a face detection to be used as an affect signal.
 * Detections below this threshold are silently discarded.
 */
export declare const FACE_AFFECT_CONFIDENCE_THRESHOLD = 0.5;
/**
 * Maps a FacialMetricMatrix expression observation to mutations of AffectState.
 * Mutates affect in place. No-op when confidence < 0.5 or expression is
 * NEUTRAL or UNKNOWN.
 *
 * Mapping table (validated against fixtures/facex_biometric_vectors.json):
 *   Happy     → engagement += 0.03, energy     += 0.02
 *   Surprised → curiosity  += 0.04
 *   Confused  → uncertainty += 0.05
 *   Stressed  → uncertainty += 0.08, energy    -= 0.05
 *   Angry     → engagement -= 0.04, rapport    -= 0.02
 *   Neutral   → no change
 *   Unknown   → no change
 */
export declare function applyFaceToAffect(matrix: FacialMetricMatrix, affect: AffectState): void;
/**
 * AffectState.uncertainty level at or above which a proactive companion message
 * is triggered, provided the observed expression is also CONFUSED or STRESSED.
 */
export declare const CONFUSION_THRESHOLD = 0.7;
/**
 * Apply a face observation to the affect state and optionally surface
 * a proactive companion event.
 * Returns null when no threshold is crossed.
 */
export declare function observeFace(matrix: FacialMetricMatrix, affect: AffectState, sessionId: string, identityId: string, surface: InterfaceKind): CompanionProactiveEvent | null;
