/**
 * voice/energy
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// voice/energy.ts
//
// Pure-managed voice components, ported one-to-one:
//   EnergyVadDetector.cs     — RMS-energy VAD with speech buffering.
//   EnergyWakeWordDetector.cs — VAD + ASR wake-word detection with a background
//                               listen loop.
//
// The C# `Task.Run(() => ListenLoopAsync(...))` background loop is modelled as an
// un-awaited async method driven off the capture stream, cancelled via an
// internal AbortController (the analogue of the C# CancellationTokenSource).
// `stopAsync` aborts it and awaits the stored loop promise, faithful to the C#
// "cancel then await the listen task" teardown.

import {
  throwIfAborted,
  vadSegment,
  VoiceOperationCancelledError,
  type IAudioCapture,
  type IVoiceActivityDetector,
  type IVoiceTranscriber,
  type IWakeWordDetector,
  type VadSegment,
  type WakeWordDetectedEventArgs,
  type WakeWordDetectedHandler,
} from "./contracts";
import { computeRmsEnergy } from "./dsp";

/**
 * Energy-based {@link IVoiceActivityDetector} that uses RMS energy to
 * distinguish speech from silence. Pure code with no external dependencies.
 * Expected input: PCM 16-bit, 16 kHz, mono (little-endian signed shorts).
 */
export class EnergyVadDetector implements IVoiceActivityDetector {
  /** RMS energy threshold in [0, 1]; frames above this are classified as speech. */
  readonly energyThreshold: number;
  /** Consecutive below-threshold frames required to declare end-of-speech. */
  readonly silenceFrameCount: number;
  /** Size of each analysis frame in bytes (640 = 20 ms @ 16 kHz mono 16-bit). */
  readonly frameSizeBytes: number;

  constructor(energyThreshold = 0.02, silenceFrames = 15, frameSizeBytes = 640) {
    if (silenceFrames <= 0) throw new RangeError("silenceFrames must be positive");
    if (frameSizeBytes <= 0) throw new RangeError("frameSizeBytes must be positive");
    if (energyThreshold < 0) throw new RangeError("energyThreshold must be non-negative");

    this.energyThreshold = energyThreshold;
    this.silenceFrameCount = silenceFrames;
    this.frameSizeBytes = frameSizeBytes;
  }

  async *detectAsync(audioStream: AsyncIterable<Uint8Array>, signal?: AbortSignal): AsyncIterable<VadSegment> {
    if (audioStream == null) throw new Error("audioStream is required");

    // Carry-over buffer for bytes that don't fill a complete frame. Annotated
    // as bare `Uint8Array` (i.e. `Uint8Array<ArrayBufferLike>`) so it stays
    // assignable from both `new Uint8Array(0)` and the `concat` result under
    // TypeScript's strict typed-array typing.
    let residual: Uint8Array = new Uint8Array(0);
    // Accumulator for the current speech segment.
    let speech: Uint8Array = new Uint8Array(0);

    let inSpeech = false;
    let consecutiveSilenceFrames = 0;

    for await (const chunk of audioStream) {
      throwIfAborted(signal);
      if (chunk.length === 0) continue;

      residual = concat(residual, chunk);

      let offset = 0;
      while (residual.length - offset >= this.frameSizeBytes) {
        const frame = residual.subarray(offset, offset + this.frameSizeBytes);
        const rms = computeRmsEnergy(frame);
        const isSpeechFrame = rms >= this.energyThreshold;

        if (isSpeechFrame) {
          if (!inSpeech) {
            inSpeech = true;
            consecutiveSilenceFrames = 0;
            speech = new Uint8Array(0);
          } else {
            consecutiveSilenceFrames = 0;
          }
          speech = concat(speech, frame);
        } else if (inSpeech) {
          // Still in the speech region; buffer silence frames in case speech
          // resumes (avoids cutting off mid-word).
          speech = concat(speech, frame);
          consecutiveSilenceFrames++;

          if (consecutiveSilenceFrames >= this.silenceFrameCount) {
            // End of speech — emit the buffered segment.
            inSpeech = false;
            consecutiveSilenceFrames = 0;
            const audio = speech.slice();
            speech = new Uint8Array(0);
            yield vadSegment(audio, true);
          }
        }
        // else: silence while not in speech — discard.

        offset += this.frameSizeBytes;
      }

      // Keep only the unconsumed residual bytes.
      residual = offset > 0 ? residual.slice(offset) : residual;
    }

    // Stream ended — if we were mid-speech, emit what we have.
    if (inSpeech && speech.length > 0) {
      yield vadSegment(speech.slice(), true);
    }
  }
}

/**
 * {@link IWakeWordDetector} that combines energy-based VAD with speech-to-text
 * transcription to detect a configurable wake word. Audio is captured
 * continuously, short speech segments are transcribed, and when a transcription
 * contains the wake word the wake-word event is fired.
 *
 * A dependency-light approach that reuses the {@link IVoiceTranscriber}
 * infrastructure. For very low latency, prefer a dedicated KWS model
 * ({@link ../onnx/index.js KwsWakeWordDetector}).
 */
export class EnergyWakeWordDetector implements IWakeWordDetector {
  readonly wakeWord: string;

  private readonly capture: IAudioCapture;
  private readonly transcriber: IVoiceTranscriber;
  private readonly vad: EnergyVadDetector;
  private readonly handlers = new Set<WakeWordDetectedHandler>();

  private listening = false;
  private disposed = false;
  private controller: AbortController | null = null;
  private loop: Promise<void> | null = null;

  constructor(
    capture: IAudioCapture,
    transcriber: IVoiceTranscriber,
    wakeWord = "hey b",
    energyThreshold = 0.02,
  ) {
    if (capture == null) throw new Error("capture is required");
    if (transcriber == null) throw new Error("transcriber is required");
    if (!wakeWord || wakeWord.trim().length === 0) throw new Error("wakeWord is required");

    this.capture = capture;
    this.transcriber = transcriber;
    this.wakeWord = wakeWord.trim();
    this.vad = new EnergyVadDetector(energyThreshold, 10, 640);
  }

  get isListening(): boolean {
    return this.listening;
  }

  onWakeWordDetected(handler: WakeWordDetectedHandler): void {
    this.handlers.add(handler);
  }
  offWakeWordDetected(handler: WakeWordDetectedHandler): void {
    this.handlers.delete(handler);
  }

  async startAsync(signal?: AbortSignal): Promise<void> {
    if (this.disposed) throw new Error("EnergyWakeWordDetector is disposed");
    throwIfAborted(signal);
    if (this.listening) return;

    this.controller = new AbortController();
    this.listening = true;
    // Fire-and-forget background loop (the C# `Task.Run(...)`).
    this.loop = this.listenLoop(this.controller.signal);
  }

  async stopAsync(_signal?: AbortSignal): Promise<void> {
    if (this.disposed) throw new Error("EnergyWakeWordDetector is disposed");
    if (!this.listening) return;

    this.controller?.abort();
    this.listening = false;
    const toAwait = this.loop;

    if (toAwait != null) {
      try {
        await toAwait;
      } catch (ex) {
        if (!(ex instanceof VoiceOperationCancelledError)) throw ex;
      }
    }

    this.controller = null;
    this.loop = null;
  }

  async disposeAsync(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    try {
      await this.stopAsync();
    } catch (ex) {
      if (!(ex instanceof VoiceOperationCancelledError)) throw ex;
    }
  }

  private raise(args: WakeWordDetectedEventArgs): void {
    for (const h of this.handlers) h(args);
  }

  /** Background loop: capture → VAD → transcribe → match wake word. */
  private async listenLoop(signal: AbortSignal): Promise<void> {
    try {
      const audioStream = this.capture.captureAsync(signal);

      for await (const segment of this.vad.detectAsync(audioStream, signal)) {
        throwIfAborted(signal);

        if (!segment.isSpeech || segment.audio.length === 0) continue;

        let result;
        try {
          result = await this.transcriber.transcribeAsync(segment.audio, signal);
        } catch (ex) {
          if (ex instanceof VoiceOperationCancelledError) throw ex;
          // Transcription failed for this segment — skip and keep listening.
          continue;
        }

        if (!result.text || result.text.trim().length === 0) continue;

        // Case-insensitive substring match (C# StringComparison.OrdinalIgnoreCase).
        if (result.text.toLowerCase().includes(this.wakeWord.toLowerCase())) {
          this.raise({
            wakeWord: this.wakeWord,
            detectedAt: new Date(),
            confidence: result.confidence,
          });
        }
      }
    } catch (ex) {
      if (ex instanceof VoiceOperationCancelledError || signal.aborted) {
        // Normal shutdown — swallow.
      } else {
        throw ex;
      }
    } finally {
      this.listening = false;
    }
  }
}

/**
 * Concatenate two byte arrays into a fresh, standalone-backed buffer. Always
 * allocates so the result is a `Uint8Array<ArrayBuffer>` (not a view over some
 * `ArrayBufferLike`), which keeps the mutable `residual` / `speech` accumulators
 * assignable under TypeScript's strict typed-array typing.
 */
function concat(a: Uint8Array, b: Uint8Array): Uint8Array {
  const out = new Uint8Array(a.length + b.length);
  out.set(a, 0);
  out.set(b, a.length);
  return out;
}
