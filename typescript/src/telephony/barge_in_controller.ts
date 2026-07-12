// telephony/barge_in_controller.ts
//
// Barge-in: when the caller interrupts the AI mid-response, pause the TTS
// playback, decide if the interruption was real (versus a cough / ambient
// noise), and either resume or cancel the turn. Faithful port of
// BargeInController.cs. `TimeSpan` → milliseconds; the injected clock returns a
// `Date`.

import { utcNow } from "./internal.js";

/** State of the AI's current turn. Mirrors `BargeInState`. */
export const BargeInState = {
  /** AI is speaking. */
  Speaking: "Speaking",
  /** Caller interrupted; playback paused while we decide. */
  Paused: "Paused",
  /** Confirmed real interruption — turn cancelled. */
  Cancelled: "Cancelled",
  /** Decided false alarm — resumed speaking. */
  Resumed: "Resumed",
} as const;
export type BargeInState = (typeof BargeInState)[keyof typeof BargeInState];

/** One state transition. Mirrors `BargeInTransition`. */
export interface BargeInTransition {
  readonly from: BargeInState;
  readonly to: BargeInState;
  readonly at: Date;
  readonly reason: string;
}

/** Configuration for barge-in detection. Mirrors `BargeInOptions`. */
export interface BargeInOptions {
  /** How long the caller must be talking before we pause, in ms. Default 100 ms. */
  readonly pauseAfterMs?: number;
  /** Continued speech that confirms a real interruption, in ms. Default 600 ms. */
  readonly cancelAfterMs?: number;
}

function pauseAfterOrDefault(o: BargeInOptions): number {
  return o.pauseAfterMs ?? 100;
}
function cancelAfterOrDefault(o: BargeInOptions): number {
  return o.cancelAfterMs ?? 600;
}

/** Drives barge-in pause/resume/cancel decisions. Mirrors `BargeInController`. */
export class BargeInController {
  private readonly options: BargeInOptions;
  private readonly clock: () => Date;
  private state: BargeInState = BargeInState.Speaking;
  private callerSpeechStartedAt: Date | null = null;

  constructor(options?: BargeInOptions, clock?: () => Date) {
    this.options = options ?? {};
    this.clock = clock ?? utcNow;
  }

  /** The current state of the AI turn. */
  getState(): BargeInState {
    return this.state;
  }

  /** Call when AI playback begins. */
  onPlaybackStart(): void {
    this.state = BargeInState.Speaking;
    this.callerSpeechStartedAt = null;
  }

  /** Call on each frame where the VAD reports caller speech. */
  onCallerSpeech(): BargeInTransition | null {
    const now = this.clock();
    if (this.state === BargeInState.Cancelled) return null;

    if (this.callerSpeechStartedAt === null) {
      this.callerSpeechStartedAt = now;
      return null;
    }

    const elapsedMs = now.getTime() - this.callerSpeechStartedAt.getTime();
    if (this.state === BargeInState.Speaking && elapsedMs >= pauseAfterOrDefault(this.options)) {
      const t: BargeInTransition = {
        from: this.state,
        to: BargeInState.Paused,
        at: now,
        reason: `Caller speech ${elapsedMs.toFixed(0)} ms`,
      };
      this.state = BargeInState.Paused;
      return t;
    }
    if (this.state === BargeInState.Paused && elapsedMs >= cancelAfterOrDefault(this.options)) {
      const t: BargeInTransition = {
        from: this.state,
        to: BargeInState.Cancelled,
        at: now,
        reason: `Confirmed barge-in after ${elapsedMs.toFixed(0)} ms`,
      };
      this.state = BargeInState.Cancelled;
      return t;
    }
    return null;
  }

  /** Call on each frame where VAD reports silence. */
  onCallerSilence(): BargeInTransition | null {
    const now = this.clock();
    this.callerSpeechStartedAt = null;

    if (this.state === BargeInState.Paused) {
      const t: BargeInTransition = {
        from: this.state,
        to: BargeInState.Resumed,
        at: now,
        reason: "Caller fell silent after pause",
      };
      this.state = BargeInState.Speaking; // resume
      return t;
    }
    return null;
  }

  /** Whether the AI should keep emitting audio frames right now. */
  get shouldEmitAudio(): boolean {
    return this.state === BargeInState.Speaking;
  }

  /** Whether the turn was confirmed barge-in (caller wins, AI should drop). */
  get wasBargedIn(): boolean {
    return this.state === BargeInState.Cancelled;
  }
}
