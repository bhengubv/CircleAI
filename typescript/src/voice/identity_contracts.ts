// voice/identity_contracts.ts
//
// The public contracts declared inside OnnxSpeakerIdentity.cs and
// OnnxSpeechEmotionDetector.cs (ISpeakerIdentity, ISpeechEmotionDetector) plus
// the SpeechEmotionFrame record. Split into their own file so both the ONNX
// implementations and callers can depend on the contract without pulling in the
// ONNX-backend seam. `IAsyncDisposable` → `disposeAsync`.

/**
 * Identify-or-enroll speaker surface (wrapped by an IVoiceIdentity adapter in
 * CircleAI.Companion). Mirrors `ISpeakerIdentity`.
 */
export interface ISpeakerIdentity {
  /**
   * Return the best-matching enrolled user id for `audioPcm16`, or `null` when
   * no enrolled speaker passes the match threshold.
   */
  identifyAsync(audioPcm16: Uint8Array, sampleRateHz: number, signal?: AbortSignal): Promise<string | null>;

  /** Enroll (or update the centroid for) `userId` from an utterance. */
  enrollAsync(userId: string, audioPcm16: Uint8Array, sampleRateHz: number, signal?: AbortSignal): Promise<void>;

  /** Release resources (C# `IAsyncDisposable`). */
  disposeAsync(): Promise<void>;
}

/** Output emotion frame from a speech-emotion model. Mirrors `SpeechEmotionFrame` record. */
export interface SpeechEmotionFrame {
  /** Top-1 emotion label (lowercase, e.g. "happy", "angry"). */
  readonly label: string;
  /** Russell-circumplex arousal coordinate in [-1, 1]. */
  readonly arousal: number;
  /** Russell-circumplex valence coordinate in [-1, 1]. */
  readonly valence: number;
  /** Softmax probability of the winning class. */
  readonly probability: number;
}

/** Constructs a {@link SpeechEmotionFrame}. */
export function speechEmotionFrame(
  label: string,
  arousal: number,
  valence: number,
  probability: number,
): SpeechEmotionFrame {
  return { label, arousal, valence, probability };
}

/** Speech-emotion recognition over PCM frames. Mirrors `ISpeechEmotionDetector`. */
export interface ISpeechEmotionDetector {
  /** Classify the emotion of `audioPcm16`, or `null` on empty/invalid input. */
  senseAsync(
    audioPcm16: Uint8Array,
    sampleRateHz: number,
    signal?: AbortSignal,
  ): Promise<SpeechEmotionFrame | null>;

  /** Release resources (C# `IAsyncDisposable`). */
  disposeAsync(): Promise<void>;
}
