/**
 * speech/contracts
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// speech/contracts.ts
//
// The CircleAI.Speech contract surface (C# is the exact spec — Contracts.cs):
// ASR / TTS / wake-word / OCR + the on-device DSP contracts (VAD, echo
// cancellation, end-of-turn, noise reduction).
//
// IMPORTANT: CircleAI.Speech is a DISTINCT project from CircleAI.Voice. Several
// names here (ISpeechRecognizer's `TranscriptionResult`, `IVoiceActivityDetector`,
// `IWakeWordDetector`, `NullVoiceActivityDetector`, `NullWakeWordDetector`)
// collide by identifier with CircleAI.Voice but describe DIFFERENT shapes — the
// Speech VAD is frame-at-a-time (`classify` → VadFrameResult); the Voice VAD is
// stream-based (`detectAsync` → VadSegment). They coexist in separate modules;
// the package root disambiguates (see src/index.ts).
//
// Type mappings (C# → TS):
//   sealed record              → readonly interface (+ positional factory)
//   ReadOnlyMemory<byte>       → Uint8Array
//   ReadOnlySpan<byte>         → Uint8Array (input) ; Span<byte> → Uint8Array (output)
//   TimeSpan                   → number (milliseconds) — see timeSpanMs helpers
//   float                      → number
//   DateTimeOffset             → Date (UTC instant)
//   IAsyncDisposable           → { disposeAsync(): Promise<void> }
//   CancellationToken          → AbortSignal (optional)
//   ValueTask<T>               → Promise<T>

// ─────────────────────────────────────────────────────────────────────────────
// ASR / TTS / OCR records
// ─────────────────────────────────────────────────────────────────────────────

/** One transcribed segment. Mirrors `TranscribedSegment`. */
export interface TranscribedSegment {
  readonly text: string;
  /** Start offset from the stream start, in milliseconds (C# `TimeSpan`). */
  readonly offsetMs: number;
  /** Segment duration, in milliseconds (C# `TimeSpan`). */
  readonly durationMs: number;
  readonly language: string | null;
  /** Confidence in [0, 1] (C# `float`). */
  readonly confidence: number;
}

/** Constructs a {@link TranscribedSegment}. */
export function transcribedSegment(
  text: string,
  offsetMs: number,
  durationMs: number,
  language: string | null = null,
  confidence = 0,
): TranscribedSegment {
  return { text, offsetMs, durationMs, language, confidence };
}

/** Outcome of one ASR call. Mirrors `CircleAI.Speech.TranscriptionResult`. */
export interface SpeechTranscriptionResult {
  readonly text: string;
  readonly language: string | null;
  readonly segments: readonly TranscribedSegment[];
  /** Total duration in milliseconds (C# `TimeSpan`). */
  readonly totalDurationMs: number;
}

/** Constructs a {@link SpeechTranscriptionResult}. */
export function speechTranscriptionResult(
  text: string,
  language: string | null,
  segments: readonly TranscribedSegment[],
  totalDurationMs: number,
): SpeechTranscriptionResult {
  return { text, language, segments, totalDurationMs };
}

/** Outcome of one TTS call. Mirrors `SynthesisResult`. */
export interface SynthesisResult {
  readonly audioPcm16Mono: Uint8Array;
  readonly sampleRateHz: number;
  /** Duration in milliseconds (C# `TimeSpan`). */
  readonly durationMs: number;
}

/** Constructs a {@link SynthesisResult}. */
export function synthesisResult(
  audioPcm16Mono: Uint8Array,
  sampleRateHz: number,
  durationMs: number,
): SynthesisResult {
  return { audioPcm16Mono, sampleRateHz, durationMs };
}

/** One detected text block in an OCR result. Mirrors `OcrTextBlock`. */
export interface OcrTextBlock {
  readonly text: string;
  readonly x: number;
  readonly y: number;
  readonly width: number;
  readonly height: number;
  readonly confidence: number;
  readonly language: string | null;
}

/** Constructs an {@link OcrTextBlock}. */
export function ocrTextBlock(
  text: string,
  x: number,
  y: number,
  width: number,
  height: number,
  confidence: number,
  language: string | null = null,
): OcrTextBlock {
  return { text, x, y, width, height, confidence, language };
}

/** One OCR result. Mirrors `OcrResult`. */
export interface OcrResult {
  readonly text: string;
  readonly blocks: readonly OcrTextBlock[];
}

/** Constructs an {@link OcrResult}. */
export function ocrResult(text: string, blocks: readonly OcrTextBlock[]): OcrResult {
  return { text, blocks };
}

/** One wake-word fire. Mirrors `WakeWordEvent`. */
export interface WakeWordEvent {
  readonly keyword: string;
  readonly confidence: number;
  readonly detectedAtUtc: Date;
}

/** Constructs a {@link WakeWordEvent}. */
export function wakeWordEvent(
  keyword: string,
  confidence: number,
  detectedAtUtc: Date,
): WakeWordEvent {
  return { keyword, confidence, detectedAtUtc };
}

// ─────────────────────────────────────────────────────────────────────────────
// DSP result records
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Verdict on whether a partial transcript represents a finished thought.
 * Mirrors `EndOfTurnResult`.
 */
export interface EndOfTurnResult {
  /** True if the speaker likely finished their turn. */
  readonly isComplete: boolean;
  /** 0..1 confidence. */
  readonly confidence: number;
  /** If `isComplete=false`, how many extra ms to wait before re-asking. */
  readonly waitMoreMs: number;
}

/** Constructs an {@link EndOfTurnResult}. */
export function endOfTurnResult(
  isComplete: boolean,
  confidence: number,
  waitMoreMs: number,
): EndOfTurnResult {
  return { isComplete, confidence, waitMoreMs };
}

/** One verdict from a voice-activity detector. Mirrors `VadFrameResult`. */
export interface VadFrameResult {
  /** True if this frame contains speech. */
  readonly isSpeech: boolean;
  /** 0..1 confidence the frame is speech. */
  readonly speechProbability: number;
  /** Frame start offset relative to the stream start, in milliseconds. */
  readonly offsetMs: number;
}

/** Constructs a {@link VadFrameResult}. */
export function vadFrameResult(
  isSpeech: boolean,
  speechProbability: number,
  offsetMs: number,
): VadFrameResult {
  return { isSpeech, speechProbability, offsetMs };
}

// ─────────────────────────────────────────────────────────────────────────────
// Contracts
// ─────────────────────────────────────────────────────────────────────────────

/** Convert audio to text. Mirrors `ISpeechRecognizer`. */
export interface ISpeechRecognizer {
  /** Backend self-identification — "funasr-1.x" / "yapsnap" / "null". */
  readonly backendId: string;

  /** Recognise one buffer of PCM-16 mono audio. */
  transcribeAsync(
    audioPcm16Mono: Uint8Array,
    sampleRateHz: number,
    languageHint?: string | null,
    signal?: AbortSignal,
  ): Promise<SpeechTranscriptionResult>;
}

/** Convert text to spoken audio. Mirrors `ISpeechSynthesizer`. */
export interface ISpeechSynthesizer {
  /** Backend self-identification — "chattts" / "null". */
  readonly backendId: string;

  /** Synthesise one utterance. Returns PCM-16 mono. */
  synthesizeAsync(
    text: string,
    voiceId?: string | null,
    languageHint?: string | null,
    signal?: AbortSignal,
  ): Promise<SynthesisResult>;
}

/** Handler for a wake-word fire (C# `Func<WakeWordEvent, ValueTask>`). */
export type WakeWordHandler = (evt: WakeWordEvent) => Promise<void> | void;

/**
 * Spot a wake word ("Hey B") in a continuous audio stream. Implementations are
 * long-running (`startAsync`/`stopAsync`). Mirrors `CircleAI.Speech.IWakeWordDetector`.
 * `IAsyncDisposable` in C# → `disposeAsync`; the `Subscribe` returning an
 * `IDisposable` → a `{ dispose(): void }` handle.
 */
export interface ISpeechWakeWordDetector {
  /** Backend self-identification — "hey-snips" / "null". */
  readonly backendId: string;

  /** Subscribe to wake-word fire events. Returns an unsubscribe handle. */
  subscribe(handler: WakeWordHandler): { dispose(): void };

  /** Begin listening on the system mic. Idempotent. */
  startAsync(signal?: AbortSignal): Promise<void>;

  /** Stop listening. Idempotent. */
  stopAsync(signal?: AbortSignal): Promise<void>;

  /** Release detector resources (C# `IAsyncDisposable`). */
  disposeAsync(): Promise<void>;
}

/**
 * Acoustic echo canceller — subtracts the far-end reference from the near-end
 * mic input. Mirrors `IEchoCanceller`.
 */
export interface IEchoCanceller {
  /** Backend self-identification — "nlms" / "webrtc-aec3" / "null". */
  readonly backendId: string;

  /**
   * Cancel echo of `farEndReference` out of `nearEndMicrophone`, writing the
   * result into `destination`. Both inputs must be the same sample rate and
   * length (PCM-16 mono). Returns the number of bytes written.
   */
  cancel(
    nearEndMicrophone: Uint8Array,
    farEndReference: Uint8Array,
    sampleRateHz: number,
    destination: Uint8Array,
  ): number;

  /** Reset adaptive-filter state at the start of a new call. */
  reset(): void;
}

/** Audio noise reducer — cleans a frame of PCM-16 mono audio. Mirrors `INoiseReducer`. */
export interface INoiseReducer {
  /** Backend self-identification — "krisp" / "deepfilternet" / "passthrough" / "null". */
  readonly backendId: string;

  /** True when the underlying model / runtime is available. */
  readonly isAvailable: boolean;

  /**
   * Reduce noise in `audioPcm16Mono` and write into `destination` (which must be
   * at least as long as the input). Returns the number of bytes written.
   */
  reduce(audioPcm16Mono: Uint8Array, sampleRateHz: number, destination: Uint8Array): number;
}

/**
 * Decide whether the caller has finished their turn given the latest partial
 * transcript + the trailing-silence duration. VAD says "they're silent now";
 * this says "they're DONE." Mirrors `IEndOfTurnDetector`.
 */
export interface IEndOfTurnDetector {
  /** Backend self-identification — "rules" / "smart-turn-v2" / "null". */
  readonly backendId: string;

  /** Classify the current state. `trailingSilenceMs` is C# `TimeSpan`. */
  predict(partialTranscript: string, trailingSilenceMs: number): EndOfTurnResult;

  /** Reset internal state at the start of a fresh turn. */
  reset(): void;
}

/**
 * Voice-activity detector. Implementations classify each 10-30 ms audio frame
 * as speech or silence. Mirrors `CircleAI.Speech.IVoiceActivityDetector`
 * (frame-at-a-time — distinct from CircleAI.Voice's stream-based VAD).
 */
export interface ISpeechVoiceActivityDetector {
  /** Backend self-identification — "energy" / "silero" / "null". */
  readonly backendId: string;

  /** Speech probability threshold for {@link VadFrameResult.isSpeech}. */
  readonly speechThreshold: number;

  /** Classify one frame of PCM-16 mono audio. `offsetMs` is C# `TimeSpan`. */
  classify(audioPcm16Mono: Uint8Array, sampleRateHz: number, offsetMs: number): VadFrameResult;

  /** Reset any internal hangover state at the start of a fresh utterance. */
  reset(): void;
}

/** Read text out of an image. Mirrors `IOpticalCharacterRecognizer`. */
export interface IOpticalCharacterRecognizer {
  /** Backend self-identification — "paddleocr-2.x" / "null". */
  readonly backendId: string;

  /** Recognise text in an image. `languageHint` e.g. "eng" / "chi" / "auto". */
  recognizeAsync(
    imageBytes: Uint8Array,
    languageHint?: string | null,
    signal?: AbortSignal,
  ): Promise<OcrResult>;
}
