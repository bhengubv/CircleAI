// voice/contracts.ts
//
// Contracts + records for CircleAI.Voice (C# is the exact spec):
//   AudioFormat.cs, ITtsEngine.cs, IVoiceActivityDetector.cs,
//   IVoiceTranscriber.cs, IWakeWordDetector.cs, and IAudioCapture (declared in
//   VoicePipeline.cs).
//
// Type mappings (C# → TS):
//   record                     → readonly interface (+ positional factory)
//   ReadOnlyMemory<byte>       → Uint8Array
//   IAsyncEnumerable<T>        → AsyncIterable<T> (produced via async generators)
//   CancellationToken          → AbortSignal (optional)
//   IAsyncDisposable           → { disposeAsync(): Promise<void> }
//   event EventHandler<T>      → on<Event>(h) / off<Event>(h) + a handler type
//   float                      → number
//   DateTimeOffset             → Date (UTC instant)

// ─────────────────────────────────────────────────────────────────────────────
// AudioFormat — CircleAI.Voice.AudioFormat (record)
// ─────────────────────────────────────────────────────────────────────────────

/** Describes a PCM audio format expected or produced by voice components. */
export interface AudioFormat {
  /** Samples per second (e.g. 16000 for 16 kHz). */
  readonly sampleRate: number;
  /** Number of interleaved channels (1 = mono, 2 = stereo). */
  readonly channels: number;
  /** Bit depth of each sample (e.g. 16 for signed 16-bit PCM). */
  readonly bitsPerSample: number;
}

/** Constructs an {@link AudioFormat}. */
export function audioFormat(sampleRate: number, channels: number, bitsPerSample: number): AudioFormat {
  return { sampleRate, channels, bitsPerSample };
}

/**
 * Canonical input format expected by Butler / B! voice components:
 * PCM signed 16-bit, mono, 16 kHz.
 */
export const PCM16_MONO_16K: AudioFormat = audioFormat(16_000, 1, 16);

// ─────────────────────────────────────────────────────────────────────────────
// ITtsEngine — CircleAI.Voice.ITtsEngine + TtsSynthesisResult (record)
// ─────────────────────────────────────────────────────────────────────────────

/** Result of a single-shot TTS synthesis operation. Mirrors `TtsSynthesisResult`. */
export interface TtsSynthesisResult {
  /** The complete PCM audio buffer. Empty when the engine produced no audio. */
  readonly audioData: Uint8Array;
  /** Samples per second (e.g. 24000 for 24 kHz). */
  readonly sampleRate: number;
  /** Number of interleaved audio channels (1 = mono, 2 = stereo). */
  readonly channels: number;
  /** Bit depth of each sample (e.g. 16 for signed 16-bit PCM). */
  readonly bitsPerSample: number;
}

/** Constructs a {@link TtsSynthesisResult}. */
export function ttsSynthesisResult(
  audioData: Uint8Array,
  sampleRate: number,
  channels: number,
  bitsPerSample: number,
): TtsSynthesisResult {
  return { audioData, sampleRate, channels, bitsPerSample };
}

/**
 * Text-to-speech engine that converts generated text into PCM audio.
 * Implementations synthesise audio using an on-device or cloud TTS backend.
 */
export interface ITtsEngine {
  /** Synthesise `text` to a single PCM audio buffer. */
  synthesiseAsync(text: string, signal?: AbortSignal): Promise<TtsSynthesisResult>;

  /**
   * Stream PCM audio chunks as they are synthesised, enabling low-latency
   * playback that begins before the full sentence is complete. Each chunk
   * shares the engine's sample rate, channel count, and bit depth.
   */
  streamSynthesiseAsync(text: string, signal?: AbortSignal): AsyncIterable<Uint8Array>;
}

// ─────────────────────────────────────────────────────────────────────────────
// IVoiceActivityDetector — CircleAI.Voice.IVoiceActivityDetector + VadSegment
// ─────────────────────────────────────────────────────────────────────────────

/** A single segment identified by an {@link IVoiceActivityDetector}. Mirrors `VadSegment`. */
export interface VadSegment {
  /** The raw PCM audio bytes for this segment. */
  readonly audio: Uint8Array;
  /** `true` when this segment contains detected speech; `false` for silence/noise markers. */
  readonly isSpeech: boolean;
}

/** Constructs a {@link VadSegment}. */
export function vadSegment(audio: Uint8Array, isSpeech: boolean): VadSegment {
  return { audio, isSpeech };
}

/**
 * Detects speech vs silence in a raw PCM audio stream (Voice Activity Detection).
 * Implementations process 16-bit, 16 kHz mono PCM input as defined by
 * {@link PCM16_MONO_16K}.
 */
export interface IVoiceActivityDetector {
  /**
   * Processes an incoming audio stream and yields only the segments that
   * contain speech. Each yielded {@link VadSegment} with `isSpeech === true`
   * represents a complete utterance from speech onset to end-of-speech silence.
   */
  detectAsync(audioStream: AsyncIterable<Uint8Array>, signal?: AbortSignal): AsyncIterable<VadSegment>;
}

// ─────────────────────────────────────────────────────────────────────────────
// IVoiceTranscriber — CircleAI.Voice.IVoiceTranscriber + records
// ─────────────────────────────────────────────────────────────────────────────

/** Final transcription result. Mirrors `TranscriptionResult` record. */
export interface TranscriptionResult {
  /** The recognised text. Empty string if nothing was recognised. */
  readonly text: string;
  /** Engine-reported confidence in the range [0, 1] (C# `float`). */
  readonly confidence: number;
  /** Detected language as a BCP-47 / ISO 639 code (e.g. "en", "zu", "und"). */
  readonly languageCode: string;
}

/** Constructs a {@link TranscriptionResult}. */
export function transcriptionResult(text: string, confidence: number, languageCode: string): TranscriptionResult {
  return { text, confidence, languageCode };
}

/** Partial or final transcription produced during streaming recognition. Mirrors `PartialTranscription`. */
export interface PartialTranscription {
  /** The recognised text so far. */
  readonly text: string;
  /** `true` when this is the final transcription for the current utterance. */
  readonly isFinal: boolean;
  /** Engine-reported confidence in the range [0, 1] (C# `float`). */
  readonly confidence: number;
}

/** Constructs a {@link PartialTranscription}. */
export function partialTranscription(text: string, isFinal: boolean, confidence: number): PartialTranscription {
  return { text, isFinal, confidence };
}

/**
 * Converts captured audio into text. Implementations consume PCM 16-bit,
 * 16 kHz mono input as defined by {@link PCM16_MONO_16K} unless documented
 * otherwise. `IAsyncDisposable` in C# → `disposeAsync`.
 */
export interface IVoiceTranscriber {
  /** Transcribe a complete audio buffer (PCM 16-bit, 16 kHz mono, little-endian). */
  transcribeAsync(pcmAudio: Uint8Array, signal?: AbortSignal): Promise<TranscriptionResult>;

  /**
   * Stream audio chunks and receive partial transcriptions as the underlying
   * engine produces them. The final element has `isFinal === true`.
   */
  streamTranscribeAsync(
    audioChunks: AsyncIterable<Uint8Array>,
    signal?: AbortSignal,
  ): AsyncIterable<PartialTranscription>;

  /** Release engine resources (C# `IAsyncDisposable`). */
  disposeAsync(): Promise<void>;
}

// ─────────────────────────────────────────────────────────────────────────────
// IWakeWordDetector — CircleAI.Voice.IWakeWordDetector + WakeWordDetectedEventArgs
// ─────────────────────────────────────────────────────────────────────────────

/** Payload describing a single wake-word detection event. Mirrors `WakeWordDetectedEventArgs`. */
export interface WakeWordDetectedEventArgs {
  /** The wake word phrase that was detected. */
  readonly wakeWord: string;
  /** UTC timestamp at which the detection fired (C# `DateTimeOffset`). */
  readonly detectedAt: Date;
  /**
   * Detector-reported confidence in the range [0, 1]. Implementations that do
   * not produce a confidence score report 1.0.
   */
  readonly confidence: number;
}

/** Handler for {@link IWakeWordDetector} wake-word events (C# `EventHandler<WakeWordDetectedEventArgs>`). */
export type WakeWordDetectedHandler = (args: WakeWordDetectedEventArgs) => void;

/**
 * Detects a configured wake word in a continuous audio stream and raises the
 * wake-word event when the phrase is recognised. Implementations manage their
 * own audio capture (microphone open/close) between {@link startAsync} and
 * {@link stopAsync}. `IAsyncDisposable` in C# → `disposeAsync`.
 */
export interface IWakeWordDetector {
  /** The phrase the detector listens for (e.g. "Hey B"). */
  readonly wakeWord: string;

  /** True when the detector is actively listening for the wake word. */
  readonly isListening: boolean;

  /** Subscribe to wake-word detections (C# `WakeWordDetected +=`). */
  onWakeWordDetected(handler: WakeWordDetectedHandler): void;
  /** Unsubscribe from wake-word detections (C# `WakeWordDetected -=`). */
  offWakeWordDetected(handler: WakeWordDetectedHandler): void;

  /** Begin listening for the wake word. Idempotent. */
  startAsync(signal?: AbortSignal): Promise<void>;

  /** Stop listening and release audio-capture resources. Idempotent. */
  stopAsync(signal?: AbortSignal): Promise<void>;

  /** Release detector resources (C# `IAsyncDisposable`). */
  disposeAsync(): Promise<void>;
}

// ─────────────────────────────────────────────────────────────────────────────
// IAudioCapture — declared in VoicePipeline.cs
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Captures raw audio from a platform input (microphone) and exposes it as an
 * asynchronous stream of PCM byte chunks in the format reported by {@link format}.
 * `IAsyncDisposable` in C# → `disposeAsync`.
 */
export interface IAudioCapture {
  /** The PCM format produced by {@link captureAsync}. */
  readonly format: AudioFormat;

  /**
   * Begin capturing audio. The returned sequence yields PCM chunks until the
   * `signal` is aborted or the underlying capture stops.
   */
  captureAsync(signal?: AbortSignal): AsyncIterable<Uint8Array>;

  /** Release capture resources (C# `IAsyncDisposable`). */
  disposeAsync(): Promise<void>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Small shared cancellation helper — the AbortSignal analogue of
// CancellationToken.ThrowIfCancellationRequested().
// ─────────────────────────────────────────────────────────────────────────────

/** Error thrown when an aborted {@link AbortSignal} cancels a voice operation. */
export class VoiceOperationCancelledError extends Error {
  constructor(message = "The voice operation was cancelled.") {
    super(message);
    this.name = "VoiceOperationCancelledError";
  }
}

/** Throws {@link VoiceOperationCancelledError} if `signal` is already aborted. */
export function throwIfAborted(signal?: AbortSignal): void {
  if (signal?.aborted) throw new VoiceOperationCancelledError();
}
