// voice/pipeline.ts
//
// VoicePipeline.cs — the convenience composition of IWakeWordDetector +
// IAudioCapture + IVoiceTranscriber (+ optional IVoiceActivityDetector + ITtsEngine).
// On wake-word detection the pipeline starts capturing audio, optionally filters
// it through VAD, feeds speech chunks to the transcriber, and raises the
// `transcribed` event with the final result.
//
// The C# `event EventHandler<T>` pattern → on/off + a Set of handlers (matching
// companion/herjarvis/voice_listener.ts). Each activation runs off the wake
// callback via an un-awaited async call (the C# `_ = Task.Run(...)`), cancelled
// by a per-activation AbortController (the C# CancellationTokenSource). The
// private `ToFinalAsync` drain helper is ported as `streamToFinal`.

import {
  transcriptionResult,
  VoiceOperationCancelledError,
  type IAudioCapture,
  type ITtsEngine,
  type IVoiceActivityDetector,
  type IVoiceTranscriber,
  type IWakeWordDetector,
  type PartialTranscription,
  type TranscriptionResult,
  type WakeWordDetectedEventArgs,
  type WakeWordDetectedHandler,
} from "./contracts.js";
import { NullAudioCapture } from "./null_impls.js";

/** Payload for a completed pipeline transcription. Mirrors `TranscribedEventArgs`. */
export interface TranscribedEventArgs {
  /** The final transcription result for the activation. */
  readonly result: TranscriptionResult;
  /** UTC timestamp when the transcription completed. */
  readonly completedAt: Date;
}

/** Handler for {@link VoicePipeline} transcription events. */
export type PipelineTranscribedHandler = (args: TranscribedEventArgs) => void;
/** Handler for {@link VoicePipeline} activation-failure events (C# `EventHandler<Exception>`). */
export type ActivationFailedHandler = (error: unknown) => void;

/**
 * Options bag for {@link VoicePipeline}. `capture` defaults to a
 * {@link NullAudioCapture}; `vad` and `tts` are optional (mirrors the C#
 * constructor's optional parameters).
 */
export interface VoicePipelineOptions {
  readonly capture?: IAudioCapture;
  readonly vad?: IVoiceActivityDetector;
  readonly tts?: ITtsEngine;
}

/**
 * Composes a wake-word detector, transcriber, audio capture, and optional VAD /
 * TTS. The pipeline does not own the wake-word lifecycle: call {@link startAsync}
 * to begin listening and {@link stopAsync} to shut down. {@link disposeAsync}
 * disposes all collaborators.
 */
export class VoicePipeline {
  private readonly wake: IWakeWordDetector;
  private readonly transcriber: IVoiceTranscriber;
  private readonly capture: IAudioCapture;
  private readonly vad: IVoiceActivityDetector | null;

  /** The optional TTS engine supplied at construction (`null` when none). */
  readonly ttsEngine: ITtsEngine | null;

  private readonly transcribedHandlers = new Set<PipelineTranscribedHandler>();
  private readonly failedHandlers = new Set<ActivationFailedHandler>();
  private readonly onWake: WakeWordDetectedHandler;

  private activationController: AbortController | null = null;
  private disposed = false;

  constructor(wake: IWakeWordDetector, transcriber: IVoiceTranscriber, options: VoicePipelineOptions = {}) {
    if (wake == null) throw new Error("wake is required");
    if (transcriber == null) throw new Error("transcriber is required");

    this.wake = wake;
    this.transcriber = transcriber;
    this.capture = options.capture ?? new NullAudioCapture();
    this.vad = options.vad ?? null;
    this.ttsEngine = options.tts ?? null;

    this.onWake = (e) => this.handleWakeWordDetected(e);
    this.wake.onWakeWordDetected(this.onWake);
  }

  /** The wake-word detector this pipeline observes. */
  get wakeDetector(): IWakeWordDetector {
    return this.wake;
  }
  /** The transcriber this pipeline drives. */
  get voiceTranscriber(): IVoiceTranscriber {
    return this.transcriber;
  }
  /** The audio capture source this pipeline reads from. */
  get audioCapture(): IAudioCapture {
    return this.capture;
  }
  /** The optional VAD supplied at construction (`null` when all audio is forwarded). */
  get voiceActivityDetector(): IVoiceActivityDetector | null {
    return this.vad;
  }

  /** Subscribe to final-transcription events (C# `Transcribed +=`). */
  onTranscribed(handler: PipelineTranscribedHandler): void {
    this.transcribedHandlers.add(handler);
  }
  /** Unsubscribe from final-transcription events. */
  offTranscribed(handler: PipelineTranscribedHandler): void {
    this.transcribedHandlers.delete(handler);
  }
  /** Subscribe to activation-failure events (C# `ActivationFailed +=`). */
  onActivationFailed(handler: ActivationFailedHandler): void {
    this.failedHandlers.add(handler);
  }
  /** Unsubscribe from activation-failure events. */
  offActivationFailed(handler: ActivationFailedHandler): void {
    this.failedHandlers.delete(handler);
  }

  /** Begin listening for the wake word (delegates to the detector). */
  async startAsync(signal?: AbortSignal): Promise<void> {
    if (this.disposed) throw new Error("VoicePipeline is disposed");
    return this.wake.startAsync(signal);
  }

  /** Stop listening and cancel any in-flight activation. */
  async stopAsync(signal?: AbortSignal): Promise<void> {
    if (this.disposed) throw new Error("VoicePipeline is disposed");
    this.cancelActivation();
    await this.wake.stopAsync(signal);
  }

  async disposeAsync(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;

    this.wake.offWakeWordDetected(this.onWake);
    this.cancelActivation();

    await this.wake.disposeAsync();
    await this.transcriber.disposeAsync();
    await this.capture.disposeAsync();
  }

  private handleWakeWordDetected(_e: WakeWordDetectedEventArgs): void {
    if (this.disposed) return;

    // Cancel any previous activation still running, then start a new one.
    this.cancelActivation();
    const controller = new AbortController();
    this.activationController = controller;

    // Fire-and-forget (the C# `_ = Task.Run(...)`).
    void this.runActivation(controller.signal);
  }

  private async runActivation(signal: AbortSignal): Promise<void> {
    try {
      // With VAD, pipe raw audio through it and pass only speech segments to the
      // transcriber; without VAD, forward the raw capture stream directly.
      const audioInput =
        this.vad === null
          ? this.capture.captureAsync(signal)
          : extractSpeechSegments(this.vad, this.capture.captureAsync(signal), signal);

      const result = await streamToFinal(this.transcriber.streamTranscribeAsync(audioInput, signal));

      if (result !== null) {
        const args: TranscribedEventArgs = { result, completedAt: new Date() };
        for (const h of this.transcribedHandlers) h(args);
      }
      // else: no final result (silence/noise/premature cancel) — normal; no event.
    } catch (ex) {
      if (ex instanceof VoiceOperationCancelledError || signal.aborted) {
        // Activation cancelled (stop requested or a new wake event). Swallow.
        return;
      }
      for (const h of this.failedHandlers) h(ex);
    }
  }

  private cancelActivation(): void {
    const toCancel = this.activationController;
    this.activationController = null;
    toCancel?.abort();
  }
}

/**
 * Filter `rawAudio` through `vad` and yield only the bytes from speech segments
 * (`isSpeech === true`). Mirrors the C# `ExtractSpeechSegmentsAsync`.
 */
async function* extractSpeechSegments(
  vad: IVoiceActivityDetector,
  rawAudio: AsyncIterable<Uint8Array>,
  signal: AbortSignal,
): AsyncIterable<Uint8Array> {
  for await (const segment of vad.detectAsync(rawAudio, signal)) {
    if (segment.isSpeech) yield segment.audio;
  }
}

/**
 * Drain a partial-transcription stream and return the final result, or `null`
 * if the stream produced no items. Mirrors the C# `ToFinalAsync`: language is
 * unknown at this layer, so "und" is reported.
 */
async function streamToFinal(source: AsyncIterable<PartialTranscription>): Promise<TranscriptionResult | null> {
  let last: PartialTranscription | null = null;
  for await (const partial of source) {
    last = partial;
    if (partial.isFinal) break;
  }
  if (last === null) return null;
  return transcriptionResult(last.text, last.confidence, "und");
}
