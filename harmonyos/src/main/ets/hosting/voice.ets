/**
 * hosting/voice
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/voice.ts
//
// Port of CircleAI.Hosting.VoiceOptions — configuration for the B! voice
// pipeline, composed via AIOptions.voice. Safe defaults produce a
// voice-disabled, silent-TTS pipeline.

/** Configuration for the B! voice pipeline. Mirrors CircleAI.Hosting.VoiceOptions. */
export interface VoiceOptions {
  /** Wake word (phrase) that triggers the voice pipeline. Default "hey b". */
  readonly wakeWord?: string;
  /** Microphone capture sample rate in Hz. Default 16000. */
  readonly sampleRateHz?: number;
  /** Auto-start the pipeline alongside the butler service. Default false. */
  readonly autoStart?: boolean;
  /** TTS engine backend: "null" (default) | "kokoro" | "piper". */
  readonly ttsBackend?: string;
  /** Trailing silence (ms) marking end-of-utterance for VAD. Default 800. */
  readonly endOfSpeechSilenceMs?: number;
}

/** Default VoiceOptions — mirrors the C# `set`-value defaults. */
export const DEFAULT_VOICE_OPTIONS: Required<VoiceOptions> = {
  wakeWord: "hey b",
  sampleRateHz: 16_000,
  autoStart: false,
  ttsBackend: "null",
  endOfSpeechSilenceMs: 800,
};
