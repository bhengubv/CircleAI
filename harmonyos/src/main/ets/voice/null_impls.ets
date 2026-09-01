/**
 * voice/null_impls
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// voice/null_impls.ts
//
// Fail-safe no-op defaults, ported one-to-one:
//   NullTtsEngine.cs, NullVoiceActivityDetector.cs, NullVoiceTranscriber.cs,
//   NullWakeWordDetector.cs, and NullAudioCapture (declared in VoicePipeline.cs).
//
// C# `ObjectDisposedException.ThrowIf` → an explicit disposed guard throwing an
// Error; `CancellationToken.ThrowIfCancellationRequested()` → throwIfAborted.

import {
  PCM16_MONO_16K,
  throwIfAborted,
  transcriptionResult,
  ttsSynthesisResult,
  vadSegment,
  type AudioFormat,
  type IAudioCapture,
  type ITtsEngine,
  type IVoiceActivityDetector,
  type IVoiceTranscriber,
  type IWakeWordDetector,
  type PartialTranscription,
  type TranscriptionResult,
  type TtsSynthesisResult,
  type VadSegment,
  type WakeWordDetectedHandler,
} from "./contracts";

/**
 * No-op {@link ITtsEngine}: returns empty audio and yields nothing. The PCM
 * format that a real engine would use is 24 kHz, mono, 16-bit.
 */
export class NullTtsEngine implements ITtsEngine {
  /** The empty result a real engine would report metadata-compatibly (24 kHz, mono, 16-bit). */
  static readonly emptyResult: TtsSynthesisResult = ttsSynthesisResult(new Uint8Array(0), 24_000, 1, 16);

  async synthesiseAsync(_text: string, signal?: AbortSignal): Promise<TtsSynthesisResult> {
    throwIfAborted(signal);
    return NullTtsEngine.emptyResult;
  }

  // eslint-disable-next-line require-yield
  async *streamSynthesiseAsync(_text: string, signal?: AbortSignal): AsyncIterable<Uint8Array> {
    throwIfAborted(signal);
    return;
  }
}

/**
 * No-op {@link IVoiceActivityDetector} that passes every chunk through as a
 * speech segment (`isSpeech === true`) without any analysis.
 */
export class NullVoiceActivityDetector implements IVoiceActivityDetector {
  async *detectAsync(audioStream: AsyncIterable<Uint8Array>, signal?: AbortSignal): AsyncIterable<VadSegment> {
    if (audioStream == null) throw new Error("audioStream is required");
    for await (const chunk of audioStream) {
      throwIfAborted(signal);
      yield vadSegment(chunk, true);
    }
  }
}

/**
 * No-op {@link IVoiceTranscriber}: drains its input so producers aren't blocked
 * but emits nothing. Single-shot returns an empty "und" result.
 */
export class NullVoiceTranscriber implements IVoiceTranscriber {
  private disposed = false;

  async transcribeAsync(_pcmAudio: Uint8Array, signal?: AbortSignal): Promise<TranscriptionResult> {
    if (this.disposed) throw new Error("NullVoiceTranscriber is disposed");
    throwIfAborted(signal);
    return transcriptionResult("", 0, "und");
  }

  // eslint-disable-next-line require-yield
  async *streamTranscribeAsync(
    audioChunks: AsyncIterable<Uint8Array>,
    signal?: AbortSignal,
  ): AsyncIterable<PartialTranscription> {
    if (this.disposed) throw new Error("NullVoiceTranscriber is disposed");
    if (audioChunks == null) throw new Error("audioChunks is required");
    // Drain the input so callers' producers are not blocked, but emit nothing.
    for await (const _ of audioChunks) {
      throwIfAborted(signal);
    }
    return;
  }

  async disposeAsync(): Promise<void> {
    this.disposed = true;
  }
}

/**
 * No-op {@link IWakeWordDetector}. Tracks listening state but never raises the
 * wake-word event. Defaults to the Butler / B! wake word "Hey B".
 */
export class NullWakeWordDetector implements IWakeWordDetector {
  readonly wakeWord: string;
  private listening = false;
  private disposed = false;

  constructor(wakeWord = "Hey B") {
    if (!wakeWord || wakeWord.trim().length === 0) throw new Error("wakeWord is required");
    this.wakeWord = wakeWord;
  }

  get isListening(): boolean {
    return this.listening;
  }

  // The event is declared to satisfy the contract but is never raised.
  onWakeWordDetected(_handler: WakeWordDetectedHandler): void {
    /* never raised */
  }
  offWakeWordDetected(_handler: WakeWordDetectedHandler): void {
    /* never raised */
  }

  async startAsync(signal?: AbortSignal): Promise<void> {
    if (this.disposed) throw new Error("NullWakeWordDetector is disposed");
    throwIfAborted(signal);
    this.listening = true;
  }

  async stopAsync(signal?: AbortSignal): Promise<void> {
    if (this.disposed) throw new Error("NullWakeWordDetector is disposed");
    throwIfAborted(signal);
    this.listening = false;
  }

  async disposeAsync(): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    this.listening = false;
  }
}

/**
 * No-op {@link IAudioCapture} that yields no audio. Used as a safe default when
 * no platform microphone backend is available.
 */
export class NullAudioCapture implements IAudioCapture {
  readonly format: AudioFormat = PCM16_MONO_16K;

  // eslint-disable-next-line require-yield
  async *captureAsync(signal?: AbortSignal): AsyncIterable<Uint8Array> {
    throwIfAborted(signal);
    return;
  }

  async disposeAsync(): Promise<void> {
    /* nothing to release */
  }
}
