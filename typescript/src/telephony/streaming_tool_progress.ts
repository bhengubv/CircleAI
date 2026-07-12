// telephony/streaming_tool_progress.ts
//
// Long-running tools push progress updates (% complete + status text) while they
// run, so the AI can keep the caller informed. Faithful port of
// StreamingToolProgress.cs — includes the spoken + recording sinks and the
// StreamingToolRunner.
//
// `float PercentComplete` uses Math.fround at construction. The throttle clock
// is injected (defaults to UTC now).

import type { BriefingSynthesiser, ICallSession } from "./contracts.js";
import type { ToolInvocation, ToolResult } from "./tool_calling.js";
import { toolResult } from "./tool_calling.js";
import { audioFrame, CallMediaFormat } from "./primitives.js";
import { utcNow } from "./internal.js";

/** One progress update from a streaming tool. Mirrors `ToolProgressUpdate`. */
export interface ToolProgressUpdate {
  /** The tool-call id this update belongs to. */
  readonly callId: string;
  /** 0..100 progress fraction. */
  readonly percentComplete: number;
  /** Optional status to speak to the caller. */
  readonly statusText?: string;
  /** Server time the update was created. */
  readonly emittedAt: Date;
}

/** Constructs a {@link ToolProgressUpdate} (narrows `percentComplete` to float32, as C# `float`). */
export function toolProgressUpdate(
  callId: string,
  percentComplete: number,
  statusText: string | undefined,
  emittedAt: Date,
): ToolProgressUpdate {
  return { callId, percentComplete: Math.fround(percentComplete), statusText, emittedAt };
}

/** The sink a tool pushes progress updates into. Mirrors `IToolProgressSink`. */
export interface IToolProgressSink {
  /** Emit one update. Implementations decide whether to forward to the caller. */
  emitAsync(update: ToolProgressUpdate, signal?: AbortSignal): Promise<void>;
}

/** Streaming tool handler — accepts a progress sink it can push updates into. Mirrors the `StreamingToolHandler` delegate. */
export type StreamingToolHandler = (
  argumentsJson: string,
  progressSink: IToolProgressSink,
  signal?: AbortSignal,
) => Promise<string>;

/**
 * Default sink that throttles updates (≥ `minIntervalMs` apart) and speaks each
 * via TTS to the active call session. Mirrors `SpokenToolProgressSink`.
 */
export class SpokenToolProgressSink implements IToolProgressSink {
  private readonly session: ICallSession;
  private readonly tts: BriefingSynthesiser;
  private readonly minIntervalMs: number;
  private lastSpoken: Date = new Date(0);
  private readonly clock: () => Date;

  constructor(
    session: ICallSession,
    tts: BriefingSynthesiser,
    minIntervalMs?: number,
    clock?: () => Date,
  ) {
    if (session === null || session === undefined) throw new Error("session is required");
    if (tts === null || tts === undefined) throw new Error("tts is required");
    this.session = session;
    this.tts = tts;
    this.minIntervalMs = minIntervalMs ?? 2000;
    this.clock = clock ?? utcNow;
  }

  async emitAsync(update: ToolProgressUpdate, signal?: AbortSignal): Promise<void> {
    if (update === null || update === undefined) throw new Error("update is required");
    if (!update.statusText || update.statusText.trim().length === 0) return;

    const now = this.clock();
    const shouldSpeak = now.getTime() - this.lastSpoken.getTime() >= this.minIntervalMs;
    if (shouldSpeak) this.lastSpoken = now;
    if (!shouldSpeak) return;

    const audio = await this.tts(update.statusText, signal);
    if (audio.length > 0) {
      await this.session.sendAudioAsync(audioFrame(audio, CallMediaFormat.Pcm24000, 0), signal);
    }
  }
}

/** Sink that records updates for observability without speaking them. Mirrors `RecordingToolProgressSink`. */
export class RecordingToolProgressSink implements IToolProgressSink {
  private readonly updatesList: ToolProgressUpdate[] = [];

  get updates(): readonly ToolProgressUpdate[] {
    return [...this.updatesList];
  }

  emitAsync(update: ToolProgressUpdate, _signal?: AbortSignal): Promise<void> {
    if (update === null || update === undefined) throw new Error("update is required");
    this.updatesList.push(update);
    return Promise.resolve();
  }
}

/** Run a streaming tool handler against a progress sink. Mirrors static `StreamingToolRunner`. */
export const StreamingToolRunner = {
  async runAsync(
    invocation: ToolInvocation,
    handler: StreamingToolHandler,
    sink: IToolProgressSink,
    signal?: AbortSignal,
  ): Promise<ToolResult> {
    if (invocation === null || invocation === undefined) throw new Error("invocation is required");
    if (handler === null || handler === undefined) throw new Error("handler is required");
    if (sink === null || sink === undefined) throw new Error("sink is required");

    try {
      const resultJson = await handler(invocation.argumentsJson, sink, signal);
      return toolResult(invocation.callId, true, resultJson ?? "{}");
    } catch (ex) {
      return toolResult(invocation.callId, false, "{}", ex instanceof Error ? ex.message : String(ex));
    }
  },
} as const;
