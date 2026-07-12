// telephony/first_message_preamble.ts
//
// Speak a greeting the moment a call connects, before the LLM has a chance to
// "warm up" — eliminates the awkward 1-2 second silence callers hate. Faithful
// port of FirstMessagePreamble.cs. Supports variable substitution (time of day,
// business name, agent identity) and per-call overrides.
//
// The C# races `modelReady` against `Task.Delay(MaxLatency)` with
// `Task.WhenAny`; we mirror that with `Promise.race` over a sentinel-tagged
// delay, then only skip the preamble if the model actually completed first
// (matches `winner == modelReady && modelReady.IsCompletedSuccessfully`).

import type { BriefingSynthesiser, ICallSession } from "./contracts.js";
import { PromptVariableResolver } from "./prompt_variable_resolver.js";
import { audioFrame, CallMediaFormat } from "./primitives.js";
import { delay } from "./internal.js";

/** Configuration for the first-message preamble. Mirrors `FirstMessagePreambleOptions`. */
export interface FirstMessagePreambleOptions {
  /** Template with `{{var}}` placeholders. */
  readonly template: string;
  /** If the LLM responds before this (ms) elapses, skip the preamble. Default 250 ms. */
  readonly maxLatencyMs?: number;
}

function maxLatency(o: FirstMessagePreambleOptions): number {
  return o.maxLatencyMs ?? 250;
}

/** Speaks a greeting at call-start. Mirrors `IFirstMessagePreamble`. */
export interface IFirstMessagePreamble {
  /**
   * Speak the preamble. `modelReady` is awaited concurrently — if it completes
   * before {@link FirstMessagePreambleOptions.maxLatencyMs} the preamble is
   * skipped (the model has its own greeting).
   */
  speakAsync(
    session: ICallSession,
    tts: BriefingSynthesiser,
    modelReady: Promise<void>,
    signal?: AbortSignal,
  ): Promise<void>;
}

const RACE_TIMEOUT = Symbol("race-timeout");

/**
 * Default driver that resolves {@link FirstMessagePreambleOptions.template} via a
 * {@link PromptVariableResolver}. Mirrors `DefaultFirstMessagePreamble`.
 */
export class DefaultFirstMessagePreamble implements IFirstMessagePreamble {
  private readonly options: FirstMessagePreambleOptions;
  private readonly resolver: PromptVariableResolver;

  constructor(options: FirstMessagePreambleOptions, resolver?: PromptVariableResolver) {
    if (options === null || options === undefined) throw new Error("options is required");
    this.options = options;
    this.resolver = resolver ?? new PromptVariableResolver();
  }

  async speakAsync(
    session: ICallSession,
    tts: BriefingSynthesiser,
    modelReady: Promise<void>,
    signal?: AbortSignal,
  ): Promise<void> {
    if (session === null || session === undefined) throw new Error("session is required");
    if (tts === null || tts === undefined) throw new Error("tts is required");
    if (modelReady === null || modelReady === undefined) throw new Error("modelReady is required");

    // Race the model. If it wins within the latency window, skip the preamble.
    let modelWon = false;
    const modelTagged = modelReady.then(
      () => {
        modelWon = true;
        return "model" as const;
      },
      () => "model" as const, // a rejected modelReady still resolves the race (not "completed successfully")
    );
    const winner = await Promise.race([
      modelTagged,
      delay(maxLatency(this.options), signal).then(() => RACE_TIMEOUT),
    ]);
    if (winner === "model" && modelWon) {
      return;
    }

    const rendered = await this.resolver.renderAsync(this.options.template, signal);
    if (!rendered || rendered.trim().length === 0) return;

    const audio = await tts(rendered, signal);
    if (audio.length === 0) return;

    await session.sendAudioAsync(audioFrame(audio, CallMediaFormat.Pcm24000, 0), signal);
  }
}
