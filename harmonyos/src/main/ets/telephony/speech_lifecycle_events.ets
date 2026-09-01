/**
 * telephony/speech_lifecycle_events
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// telephony/speech_lifecycle_events.ts
//
// Lifecycle events for every speaking moment in a call — faithful port of
// SpeechLifecycleEvents.cs. caller-speech-started, transcript-final,
// agent-thinking, agent-speaking-started, agent-speaking-finished, plus errors.
// Apps subscribe for analytics, UX (waveform animations), or audit.
//
// C# models the events as an abstract-record hierarchy and dispatches by walking
// the runtime type up to the base. TS has no record inheritance, so each event
// carries a `kind` discriminant; subscribers pick a specific `kind` OR the
// sentinel {@link ALL_EVENTS} to receive every event — the exact reachability
// the C# `Subscribe<SpeechLifecycleEvent>` base-type subscription provides.

/** Base fields on every lifecycle event. Mirrors abstract `SpeechLifecycleEvent`. */
export interface SpeechLifecycleEventBase {
  readonly callId: string;
  readonly at: Date;
}

export interface CallerSpeechStartedEvent extends SpeechLifecycleEventBase {
  readonly kind: "CallerSpeechStarted";
}
export interface CallerSpeechEndedEvent extends SpeechLifecycleEventBase {
  readonly kind: "CallerSpeechEnded";
}
export interface TranscriptInterimEvent extends SpeechLifecycleEventBase {
  readonly kind: "TranscriptInterim";
  readonly text: string;
}
/** Mirrors `TranscriptFinalEvent_v2`. */
export interface TranscriptFinalEvent extends SpeechLifecycleEventBase {
  readonly kind: "TranscriptFinal";
  readonly text: string;
}
export interface AgentThinkingEvent extends SpeechLifecycleEventBase {
  readonly kind: "AgentThinking";
}
export interface AgentSpeakingStartedEvent extends SpeechLifecycleEventBase {
  readonly kind: "AgentSpeakingStarted";
}
export interface AgentSpeakingFinishedEvent extends SpeechLifecycleEventBase {
  readonly kind: "AgentSpeakingFinished";
  /** Spoken duration in milliseconds. */
  readonly spokenDurationMs: number;
}
export interface SpeechErrorEvent extends SpeechLifecycleEventBase {
  readonly kind: "SpeechError";
  readonly stage: string;
  readonly message: string;
}

/** Discriminated union of all lifecycle events. */
export type SpeechLifecycleEvent =
  | CallerSpeechStartedEvent
  | CallerSpeechEndedEvent
  | TranscriptInterimEvent
  | TranscriptFinalEvent
  | AgentThinkingEvent
  | AgentSpeakingStartedEvent
  | AgentSpeakingFinishedEvent
  | SpeechErrorEvent;

/** The `kind` discriminants. */
export type SpeechEventKind = SpeechLifecycleEvent["kind"];

/** Subscribe with this in place of a specific kind to receive every event (mirrors the base-type subscription). */
export const ALL_EVENTS = "*" as const;

/** Subscription handle. Mirrors `ISpeechSubscription` (`IDisposable`). */
export interface ISpeechSubscription {
  dispose(): void;
}

/** Speech lifecycle pub/sub. Mirrors `ISpeechLifecycleBus`. */
export interface ISpeechLifecycleBus {
  /**
   * Subscribe to a specific event `kind`. Pass {@link ALL_EVENTS} to receive
   * every event (mirrors `Subscribe<SpeechLifecycleEvent>`).
   */
  subscribe(kind: typeof ALL_EVENTS, handler: (ev: SpeechLifecycleEvent) => void): ISpeechSubscription;
  subscribe<K extends SpeechEventKind>(
    kind: K,
    handler: (ev: Extract<SpeechLifecycleEvent, { kind: K }>) => void,
  ): ISpeechSubscription;

  /** Publish one event. All matching subscribers are invoked synchronously. */
  publish(ev: SpeechLifecycleEvent): void;
}

/** Default in-memory bus. Mirrors `InMemorySpeechLifecycleBus`. */
export class InMemorySpeechLifecycleBus implements ISpeechLifecycleBus {
  // key: kind or ALL_EVENTS → (handle id → handler)
  private readonly subscribers = new Map<string, Map<number, (ev: SpeechLifecycleEvent) => void>>();
  private nextHandle = 0;

  subscribe(
    kind: typeof ALL_EVENTS | SpeechEventKind,
    handler: (ev: never) => void,
  ): ISpeechSubscription {
    if (handler === null || handler === undefined) throw new Error("handler is required");
    let bucket = this.subscribers.get(kind);
    if (bucket === undefined) {
      bucket = new Map();
      this.subscribers.set(kind, bucket);
    }
    const id = ++this.nextHandle;
    bucket.set(id, handler as (ev: SpeechLifecycleEvent) => void);
    const b = bucket;
    return {
      dispose: () => {
        b.delete(id);
      },
    };
  }

  publish(ev: SpeechLifecycleEvent): void {
    if (ev === null || ev === undefined) throw new Error("ev is required");
    // Deliver to kind-specific subscribers, then to ALL_EVENTS subscribers —
    // mirroring the C# walk from the concrete type up to the abstract base.
    const exact = this.subscribers.get(ev.kind);
    if (exact !== undefined) {
      for (const del of [...exact.values()]) del(ev);
    }
    const all = this.subscribers.get(ALL_EVENTS);
    if (all !== undefined) {
      for (const del of [...all.values()]) del(ev);
    }
  }
}
