// audio_format.ts
//
// Port of src/CircleAI.Voice/AudioFormat.cs.
//
// DECLARED HERE, unlike in the typescript/ port. The sibling TS port already
// carries PCM16_MONO_16K in voice/contracts.ts, so the shared source
// deliberately does NOT declare it — but this HarmonyOS module had no voice
// module at all before the parity work, so there is nothing here to collide with.

/** Describes a PCM audio format expected or produced by voice components. */
export interface AudioFormat {
  /** Samples per second (e.g. 16000 for 16 kHz). */
  readonly sampleRate: number;
  /** Number of interleaved channels (1 = mono, 2 = stereo). */
  readonly channels: number;
  /** Bit depth of each sample (e.g. 16 for signed 16-bit PCM). */
  readonly bitsPerSample: number;
}

/**
 * Canonical input format expected by Butler / B! voice components: PCM signed
 * 16-bit, mono, 16 kHz. Most open-source ASR engines (sherpa-onnx, Vosk) accept
 * this directly.
 */
export const PCM16_MONO_16K: AudioFormat = Object.freeze({
  sampleRate: 16_000,
  channels: 1,
  bitsPerSample: 16,
});
