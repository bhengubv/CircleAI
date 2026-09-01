/**
 * companion/herjarvis/voice_listener
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/herjarvis/voice_listener.ts
//
// IVoiceListener + VoiceCompanionListener — IVoiceListener.cs +
// VoiceCompanionListener.cs. Bridges a voice pipeline to an ICompanionSession:
// when a transcription arrives, forward the text to the session and surface the
// Companion's reply via the ResponseReady event.
//
// C# uses `event EventHandler<T>` and a concrete `CircleAI.Voice.VoicePipeline`.
// The pipeline is not part of this port's work unit, so it is modelled as an
// INJECTED `IVoicePipeline` interface (start/stop/dispose + a `transcribed`
// event). Events are modelled as add/remove listener methods; the "fire the
// session on the thread pool" behaviour becomes an un-awaited async call so the
// transcription callback never blocks — faithful to the C# `_ = Task.Run(...)`.

import type { ICompanionSession } from "../index";

/** Raised when a user utterance has been transcribed and forwarded. */
export interface UtteranceDetectedEventArgs {
  /** Transcribed text of the user's utterance. */
  readonly text: string;
  /** Transcription confidence in [0.0, 1.0] (a C# `float`). */
  readonly confidence: number;
  /** UTC timestamp when the transcription completed. */
  readonly detectedAt: Date;
}

/** Raised when the Companion has produced a reply to a voice utterance. */
export interface ResponseReadyEventArgs {
  /** The Companion's reply text. */
  readonly text: string;
  /** The utterance that triggered this response. */
  readonly originalUtterance: string;
  /** UTC timestamp when the Companion completed the reply. */
  readonly completedAt: Date;
}

export type UtteranceDetectedHandler = (args: UtteranceDetectedEventArgs) => void;
export type ResponseReadyHandler = (args: ResponseReadyEventArgs) => void;

/**
 * Bridges a voice pipeline with an ICompanionSession: listens for transcribed
 * utterances, forwards them to the session, and raises ResponseReady when the
 * reply is available. Mirrors CircleAI.Companion.IVoiceListener (IAsyncDisposable).
 */
export interface IVoiceListener {
  onUtteranceDetected(handler: UtteranceDetectedHandler): void;
  offUtteranceDetected(handler: UtteranceDetectedHandler): void;
  onResponseReady(handler: ResponseReadyHandler): void;
  offResponseReady(handler: ResponseReadyHandler): void;

  /** Begin listening for the wake word. Starts the underlying pipeline. */
  startAsync(signal?: AbortSignal): Promise<void>;
  /** Stop listening and cancel any in-flight activation. Stops the pipeline. */
  stopAsync(signal?: AbortSignal): Promise<void>;
  /** IAsyncDisposable — dispose the pipeline and the session. */
  disposeAsync(): Promise<void>;
}

/** One transcription result (subset of the C# TranscribedEventArgs.Result). */
export interface TranscriptionResult {
  readonly text: string;
  readonly confidence: number;
}

/** The transcription event payload (C# TranscribedEventArgs). */
export interface TranscribedEventArgs {
  readonly result: TranscriptionResult;
  readonly completedAt: Date;
}

export type TranscribedHandler = (args: TranscribedEventArgs) => void;

/**
 * The injected voice pipeline. Models the surface of CircleAI.Voice.VoicePipeline
 * that VoiceCompanionListener uses: a `transcribed` event + start / stop /
 * dispose. A host wires the real ONNX wake-word + STT pipeline.
 */
export interface IVoicePipeline {
  onTranscribed(handler: TranscribedHandler): void;
  offTranscribed(handler: TranscribedHandler): void;
  startAsync(signal?: AbortSignal): Promise<void>;
  stopAsync(signal?: AbortSignal): Promise<void>;
  disposeAsync(): Promise<void>;
}

/**
 * Concrete IVoiceListener wiring a VoicePipeline to an ICompanionSession.
 * Transcriptions are forwarded to the session (off the callback thread, so the
 * wake-word detector never blocks) and the reply is surfaced via ResponseReady.
 * Owns both the pipeline and the session — `disposeAsync` disposes them.
 */
export class VoiceCompanionListener implements IVoiceListener {
  private readonly pipeline: IVoicePipeline;
  private readonly session: ICompanionSession & { disposeAsync?: () => Promise<void> };
  private disposed = false;
  private readonly utteranceHandlers = new Set<UtteranceDetectedHandler>();
  private readonly responseHandlers = new Set<ResponseReadyHandler>();
  private readonly onTranscribed: TranscribedHandler;

  constructor(
    pipeline: IVoicePipeline,
    session: ICompanionSession & { disposeAsync?: () => Promise<void> },
  ) {
    if (pipeline == null) throw new Error("pipeline required");
    if (session == null) throw new Error("session required");
    this.pipeline = pipeline;
    this.session = session;
    this.onTranscribed = (e) => this.handleTranscribed(e);
    this.pipeline.onTranscribed(this.onTranscribed);
  }

  onUtteranceDetected(handler: UtteranceDetectedHandler): void {
    this.utteranceHandlers.add(handler);
  }
  offUtteranceDetected(handler: UtteranceDetectedHandler): void {
    this.utteranceHandlers.delete(handler);
  }
  onResponseReady(handler: ResponseReadyHandler): void {
    this.responseHandlers.add(handler);
  }
  offResponseReady(handler: ResponseReadyHandler): void {
    this.responseHandlers.delete(handler);
  }

  async startAsync(signal?: AbortSignal): Promise<void> {
    if (this.disposed) throw new Error("VoiceCompanionListener disposed");
    return this.pipeline.startAsync(signal);
  }

  async stopAsync(signal?: AbortSignal): Promise<void> {
    if (this.disposed) throw new Error("VoiceCompanionListener disposed");
    return this.pipeline.stopAsync(signal);
  }

  private handleTranscribed(e: TranscribedEventArgs): void {
    if (this.disposed) return;
    const text = e.result.text;
    const confidence = e.result.confidence;
    const detectedAt = e.completedAt;

    // Notify subscribers that we received an utterance.
    const detected: UtteranceDetectedEventArgs = { text, confidence, detectedAt };
    for (const h of this.utteranceHandlers) h(detected);

    // Forward to the Companion asynchronously — never block the pipeline thread.
    void (async () => {
      try {
        const reply = await this.session.sendAsync(text);
        if (!this.disposed) {
          const ready: ResponseReadyEventArgs = {
            text: reply,
            originalUtterance: text,
            completedAt: new Date(),
          };
          for (const h of this.responseHandlers) h(ready);
        }
      } catch (ex) {
        // C# traces the error; do not crash the host.
        if (typeof console !== "undefined" && console.error) {
          console.error(
            `VoiceCompanionListener: session failed for utterance '${text}': ${
              ex instanceof Error ? ex.message : String(ex)
            }`,
          );
        }
      }
    })();
  }

  async disposeAsync(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    this.pipeline.offTranscribed(this.onTranscribed);
    await this.pipeline.disposeAsync();
    if (typeof this.session.disposeAsync === "function") {
      await this.session.disposeAsync();
    }
  }
}
