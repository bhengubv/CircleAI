// voice/index.ts
//
// Barrel for the CircleAI.Voice port — the on-device voice stack:
// wake-word detection, voice-activity detection, speech-to-text, text-to-speech,
// speaker identification, speech-emotion recognition, and the composed
// VoicePipeline. Faithful port of the CircleAI.Voice C# project (C# is the exact
// spec).
//
// PLATFORM SEAMS. Two native dependencies are injected behind interfaces so the
// port is deterministic and needs no native library — matching the existing
// "backend-injected" convention (see embeddings/index.ts):
//   • ONNX Runtime (Microsoft.ML.OnnxRuntime) → IOnnxSession / OnnxSessionFactory
//     (onnx_backend.ts), used by the TTS engine, KWS detector, speaker identity
//     and emotion detector.
//   • whisper.cpp (WhisperInterop P/Invoke)   → IWhisperContext / WhisperContextFactory
//     (whisper.ts), used by WhisperTranscriber; a NullWhisperContext is the default.
// Enrollment persistence for the speaker identity (a JSON file in C#) is injected
// behind IEnrollmentStore with a NullEnrollmentStore default.

// Core contracts + records (AudioFormat, TTS, VAD, transcriber, wake-word,
// audio capture) + the AbortSignal cancellation helper.
export type {
  AudioFormat,
  TtsSynthesisResult,
  ITtsEngine,
  VadSegment,
  IVoiceActivityDetector,
  TranscriptionResult,
  PartialTranscription,
  IVoiceTranscriber,
  WakeWordDetectedEventArgs,
  WakeWordDetectedHandler,
  IWakeWordDetector,
  IAudioCapture,
} from "./contracts.js";
export {
  audioFormat,
  PCM16_MONO_16K,
  ttsSynthesisResult,
  vadSegment,
  transcriptionResult,
  partialTranscription,
  VoiceOperationCancelledError,
  throwIfAborted,
} from "./contracts.js";

// Fail-safe Null* defaults.
export {
  NullTtsEngine,
  NullVoiceActivityDetector,
  NullVoiceTranscriber,
  NullWakeWordDetector,
  NullAudioCapture,
} from "./null_impls.js";

// Energy-based (pure) VAD + wake-word detector.
export { EnergyVadDetector, EnergyWakeWordDetector } from "./energy.js";

// ONNX-backend injection seam.
export type { DenseTensor, IOnnxSession, OnnxSessionFactory } from "./onnx_backend.js";
export { floatTensor, int64Tensor } from "./onnx_backend.js";

// ONNX-backed components.
export { OnnxTtsEngine, tokeniseText } from "./onnx_tts.js";
export { KwsWakeWordDetector, KwsInputKind, kwsConfig } from "./onnx_kws.js";
export type { KwsConfig } from "./onnx_kws.js";
export {
  OnnxSpeakerIdentity,
  SpeakerEmbedderInputKind,
  speakerIdentityConfig,
  NullEnrollmentStore,
} from "./onnx_speaker.js";
export type { EnrolledSpeaker, IEnrollmentStore, SpeakerIdentityConfig } from "./onnx_speaker.js";
export { OnnxSpeechEmotionDetector, speechEmotionConfig } from "./onnx_emotion.js";
export type { SpeechEmotionConfig } from "./onnx_emotion.js";

// Speaker-identity / emotion contracts (declared inside the C# ONNX files).
export type { ISpeakerIdentity, ISpeechEmotionDetector, SpeechEmotionFrame } from "./identity_contracts.js";
export { speechEmotionFrame } from "./identity_contracts.js";

// Whisper transcriber + its native-context seam.
export { WhisperTranscriber, NullWhisperContext } from "./whisper.js";
export type { IWhisperContext, WhisperContextFactory } from "./whisper.js";

// The composed pipeline.
export { VoicePipeline } from "./pipeline.js";
export type {
  TranscribedEventArgs,
  PipelineTranscribedHandler,
  ActivationFailedHandler,
  VoicePipelineOptions,
} from "./pipeline.js";
