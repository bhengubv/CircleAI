// companion.ts
//
// Circle AI Companion layer — ArkTS port.
// The Companion is the HER + JARVIS persona — available on every surface,
// with memory and identity that travels with the person.

// ---------------------------------------------------------------------------
// Enumerations
// ---------------------------------------------------------------------------

/**
 * The surface on which the Companion session is running.
 */
export enum InterfaceKind {
  Mobile   = 'Mobile',
  Wearable = 'Wearable',
  Desktop  = 'Desktop',
  Web      = 'Web',
  IoT      = 'IoT',
  Ambient  = 'Ambient',
  Headless = 'Headless',
}

// ---------------------------------------------------------------------------
// Data types
// ---------------------------------------------------------------------------

/**
 * Snapshot of all context injected into the Companion's system prompt.
 */
export interface CompanionContext {
  readonly identityId:           string;
  readonly displayName:          string;
  readonly preferredLanguage:    string | null;
  readonly interface:            InterfaceKind;
  readonly personaHints:         string;
  readonly affectSummary:        string;
  readonly recentMemorySnippets: readonly string[];
  readonly activeGoals:          readonly string[];
  readonly contextBuiltAt:       Date;
}

/**
 * A single turn in the Companion conversation log.
 * role is "user" or "assistant".
 */
export interface CompanionTurn {
  readonly role:      string;
  readonly content:   string;
  readonly timestamp: Date;
}

/**
 * Metadata emitted when the Companion proactively initiates contact.
 */
export interface CompanionProactiveEvent {
  readonly sessionId:   string;
  readonly identityId:  string;
  readonly interface:   InterfaceKind;
  readonly message:     string;
  readonly triggerName: string;
  readonly generatedAt: Date;
}

// ---------------------------------------------------------------------------
// ICompanionSession
// ---------------------------------------------------------------------------

/**
 * A Companion conversation session.
 */
export abstract class ICompanionSession {
  abstract readonly sessionId: string;
  abstract readonly identityId: string;
  abstract readonly interface: InterfaceKind;

  abstract send(message: string): Promise<string>;
  abstract stream(message: string): AsyncGenerator<string, void, unknown>;
  abstract agent(instruction: string): Promise<string>;
  abstract getContext(): CompanionContext;
  abstract refreshContext(): Promise<void>;
  abstract readonly history: readonly CompanionTurn[];
  abstract signalFeedback(positive: boolean, note?: string | null): Promise<void>;
  abstract onProactiveMessage(
    handler: (event: CompanionProactiveEvent) => void,
  ): () => void;
  abstract dispose(): Promise<void>;
}
