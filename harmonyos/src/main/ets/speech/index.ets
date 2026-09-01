/**
 * speech/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// speech/index.ts
//
// Barrel for the CircleAI.Speech port — the on-device speech DSP + ASR/TTS/OCR
// contract surface (C# is the exact spec). DISTINCT from CircleAI.Voice: the
// Speech VAD/wake-word/transcription contracts are frame-at-a-time and collide
// by NAME with the stream-based CircleAI.Voice ones. They live here in their own
// module; the package root (src/index.ts) disambiguates the colliding names.
//
// Contents:
//   • AudioFormatConverter — μ-law / a-law / PCM-16 codec transcoding + linear
//     resample (AudioCodec enum + convertAudio + the codec/resample primitives).
//   • Voice-activity detection — Null / Energy (RMS + ZCR + hangover) / Silero
//     (host IVadModelRunner, energy fallback).
//   • Echo cancellation — Null / NLMS adaptive filter / WebRTC AEC3 shell.
//   • End-of-turn detection — Null / rule-based (punctuation + hanging words) /
//     SmartTurn (host ITurnModelRunner, rule fallback).
//   • Noise reduction — Null / spectral-subtraction gate / Krisp / DeepFilterNet
//     (host INoiseReducerModelRunner shells).
//   • Fail-closed Null ASR/TTS/wake-word/OCR defaults.

// Contracts + records.
export type {
  TranscribedSegment,
  SpeechTranscriptionResult,
  SynthesisResult,
  OcrTextBlock,
  OcrResult,
  WakeWordEvent,
  EndOfTurnResult,
  VadFrameResult,
  ISpeechRecognizer,
  ISpeechSynthesizer,
  ISpeechWakeWordDetector,
  WakeWordHandler,
  IEchoCanceller,
  INoiseReducer,
  IEndOfTurnDetector,
  ISpeechVoiceActivityDetector,
  IOpticalCharacterRecognizer,
} from "./contracts";
export {
  transcribedSegment,
  speechTranscriptionResult,
  synthesisResult,
  ocrTextBlock,
  ocrResult,
  wakeWordEvent,
  endOfTurnResult,
  vadFrameResult,
} from "./contracts";

// PCM byte helpers (μ-law/a-law/resample share these).
export { readInt16LE, writeInt16LE, clampShort, SHORT_MAX, SHORT_MIN } from "./pcm_io";

// Audio format conversion.
export {
  AudioCodec,
  convertAudio,
  decodeMuLawToPcm16,
  encodePcm16ToMuLaw,
  decodeALawToPcm16,
  encodePcm16ToALaw,
  resamplePcm16Linear,
} from "./audio_format_converter";

// Voice-activity detection.
export {
  NullSpeechVoiceActivityDetector,
  EnergySpeechVoiceActivityDetector,
  SileroSpeechVoiceActivityDetector,
  type IVadModelRunner,
} from "./voice_activity_detectors";

// Echo cancellation.
export {
  NullEchoCanceller,
  NlmsEchoCanceller,
  WebRtcEchoCanceller,
  type IEchoCancellerModelRunner,
} from "./echo_cancellers";

// End-of-turn detection.
export {
  NullEndOfTurnDetector,
  RuleBasedEndOfTurnDetector,
  SmartTurnDetector,
  type ITurnModelRunner,
} from "./end_of_turn_detectors";

// Noise reduction.
export {
  NullNoiseReducer,
  SpectralSubtractionNoiseReducer,
  KrispNoiseReducer,
  DeepFilterNetNoiseReducer,
  type INoiseReducerModelRunner,
} from "./noise_reducers";

// Fail-closed ASR / TTS / wake-word / OCR defaults.
export {
  NullSpeechRecognizer,
  NullSpeechSynthesizer,
  NullSpeechWakeWordDetector,
  NullOpticalCharacterRecognizer,
} from "./null_impls";
