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
} from "./contracts.js";
export {
  transcribedSegment,
  speechTranscriptionResult,
  synthesisResult,
  ocrTextBlock,
  ocrResult,
  wakeWordEvent,
  endOfTurnResult,
  vadFrameResult,
} from "./contracts.js";

// PCM byte helpers (μ-law/a-law/resample share these).
export { readInt16LE, writeInt16LE, clampShort, SHORT_MAX, SHORT_MIN } from "./pcm_io.js";

// Audio format conversion.
export {
  AudioCodec,
  convertAudio,
  decodeMuLawToPcm16,
  encodePcm16ToMuLaw,
  decodeALawToPcm16,
  encodePcm16ToALaw,
  resamplePcm16Linear,
} from "./audio_format_converter.js";

// Voice-activity detection.
export {
  NullSpeechVoiceActivityDetector,
  EnergySpeechVoiceActivityDetector,
  SileroSpeechVoiceActivityDetector,
  type IVadModelRunner,
} from "./voice_activity_detectors.js";

// Echo cancellation.
export {
  NullEchoCanceller,
  NlmsEchoCanceller,
  WebRtcEchoCanceller,
  type IEchoCancellerModelRunner,
} from "./echo_cancellers.js";

// End-of-turn detection.
export {
  NullEndOfTurnDetector,
  RuleBasedEndOfTurnDetector,
  SmartTurnDetector,
  type ITurnModelRunner,
} from "./end_of_turn_detectors.js";

// Noise reduction.
export {
  NullNoiseReducer,
  SpectralSubtractionNoiseReducer,
  KrispNoiseReducer,
  DeepFilterNetNoiseReducer,
  type INoiseReducerModelRunner,
} from "./noise_reducers.js";

// Fail-closed ASR / TTS / wake-word / OCR defaults.
export {
  NullSpeechRecognizer,
  NullSpeechSynthesizer,
  NullSpeechWakeWordDetector,
  NullOpticalCharacterRecognizer,
} from "./null_impls.js";
