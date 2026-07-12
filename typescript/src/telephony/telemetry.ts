// telephony/telemetry.ts
//
// Trace spans for the voice loop — faithful port of Telemetry.cs. The C# uses
// .NET's `System.Diagnostics.ActivitySource` and a host-wired OTel exporter.
// TypeScript has no ambient ActivitySource, so the native tracer is injected
// behind {@link IActivitySource} (matching the "inject native behind interfaces"
// convention); a {@link NullActivitySource} is the default no-op. The stable
// source name, span names, tag keys, and outcome semantics are preserved so
// dashboards can pin to them exactly as with the C#.

/** ActivityKind subset used by the voice loop (mirrors `System.Diagnostics.ActivityKind`). */
export const ActivityKind = {
  Internal: "Internal",
  Client: "Client",
} as const;
export type ActivityKind = (typeof ActivityKind)[keyof typeof ActivityKind];

/** Status a span can be tagged with (mirrors `ActivityStatusCode`). */
export const ActivityStatusCode = {
  Unset: "Unset",
  Ok: "Ok",
  Error: "Error",
} as const;
export type ActivityStatusCode = (typeof ActivityStatusCode)[keyof typeof ActivityStatusCode];

/** A started span. Mirrors the slice of `System.Diagnostics.Activity` the voice loop uses. */
export interface IActivity {
  setTag(key: string, value: string | undefined): void;
  setStatus(code: ActivityStatusCode, description?: string): void;
  /** End the span (mirrors `Activity.Dispose`). */
  dispose(): void;
}

/** Starts spans. Mirrors the slice of `ActivitySource` the voice loop uses. */
export interface IActivitySource {
  startActivity(
    name: string,
    kind: ActivityKind,
    tags?: ReadonlyArray<readonly [key: string, value: string | undefined]>,
  ): IActivity | null;
}

/** No-op activity source — returns null spans (matches OTel "no listener" behaviour). */
export class NullActivitySource implements IActivitySource {
  static readonly instance = new NullActivitySource();
  startActivity(): IActivity | null {
    return null;
  }
}

/** Public telemetry for the voice loop. Mirrors static `VoiceLoopTelemetry`. */
export const VoiceLoopTelemetry = {
  /** ActivitySource name CircleAI uses for voice-loop spans. */
  sourceName: "CircleAI.Telephony.VoiceLoop",

  /** Start a span for one voice loop turn. */
  startTurn(source: IActivitySource, callId: string): IActivity | null {
    return source.startActivity("voice_loop.turn", ActivityKind.Internal, [["call.id", callId]]);
  },

  /** Start a span around the STT stage. */
  startAsr(source: IActivitySource, backend: string): IActivity | null {
    return source.startActivity("voice_loop.asr", ActivityKind.Client, [["backend", backend]]);
  },

  /** Start a span around the LLM stage. */
  startLlm(source: IActivitySource, provider: string, model: string): IActivity | null {
    return source.startActivity("voice_loop.llm", ActivityKind.Client, [
      ["provider", provider],
      ["model", model],
    ]);
  },

  /** Start a span around the TTS stage. */
  startTts(source: IActivitySource, backend: string, voiceId?: string): IActivity | null {
    return source.startActivity("voice_loop.tts", ActivityKind.Client, [
      ["backend", backend],
      ["voice", voiceId],
    ]);
  },

  /** Tag a turn span with its outcome. */
  recordOutcome(activity: IActivity | null, success: boolean, errorReason?: string): void {
    if (activity === null) return;
    activity.setTag("outcome", success ? "success" : "failure");
    if (!success && errorReason !== undefined) {
      activity.setTag("error.message", errorReason);
      activity.setStatus(ActivityStatusCode.Error, errorReason);
    } else if (success) {
      activity.setStatus(ActivityStatusCode.Ok);
    }
  },
} as const;
