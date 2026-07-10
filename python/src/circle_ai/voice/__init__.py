"""circle_ai.voice — port of the CircleAI.Voice assembly.

The voice-loop composition layer for B! Butler (C# is the exact spec): audio
capture, wake-word detection, voice-activity detection, transcription, TTS, plus
neural speaker-identity and speech-emotion detection. Native audio, whisper.cpp,
and ONNX engines are injected behind seams (``ISpeakerEmbedder`` /
``IEmotionClassifier`` / the null + energy implementations), so no real audio or
model file is required.

Public surface:

  * Formats + results:
      AudioFormat, PCM16_MONO_16K, TranscriptionResult, PartialTranscription,
      TtsSynthesisResult, VadSegment, WakeWordDetectedEventArgs, TranscribedEventArgs.
  * Contracts:
      IAudioCapture, IVoiceTranscriber, IWakeWordDetector, IVoiceActivityDetector,
      ITtsEngine, ISpeakerIdentity, ISpeechEmotionDetector.
  * Null (safe-default) implementations:
      NullAudioCapture, NullVoiceTranscriber, NullWakeWordDetector,
      NullVoiceActivityDetector, NullTtsEngine.
  * Deterministic working implementations:
      EnergyVadDetector, EnergyWakeWordDetector, VoicePipeline,
      OnnxSpeakerIdentity, OnnxSpeechEmotionDetector.
  * Injected model seams + configs:
      ISpeakerEmbedder, SpeakerIdentityConfig, SpeakerEmbedderInputKind,
      EnrolledSpeaker, IEmotionClassifier, SpeechEmotionConfig, SpeechEmotionFrame.
"""
from __future__ import annotations

from .audio_capture import (
    IAudioCapture,
    NullAudioCapture,
    TranscribedEventArgs,
)
from .contracts import (
    AudioFormat,
    ITtsEngine,
    IVoiceActivityDetector,
    IVoiceTranscriber,
    IWakeWordDetector,
    PartialTranscription,
    PCM16_MONO_16K,
    TranscriptionResult,
    TtsSynthesisResult,
    VadSegment,
    WakeWordDetectedEventArgs,
    WakeWordHandler,
)
from .energy_vad_detector import EnergyVadDetector
from .energy_wake_word_detector import EnergyWakeWordDetector
from .null_implementations import (
    NullTtsEngine,
    NullVoiceActivityDetector,
    NullVoiceTranscriber,
    NullWakeWordDetector,
)
from .onnx_speaker_identity import (
    EnrolledSpeaker,
    ISpeakerEmbedder,
    ISpeakerIdentity,
    OnnxSpeakerIdentity,
    SpeakerEmbedderInputKind,
    SpeakerIdentityConfig,
)
from .onnx_speech_emotion_detector import (
    IEmotionClassifier,
    ISpeechEmotionDetector,
    OnnxSpeechEmotionDetector,
    SpeechEmotionConfig,
    SpeechEmotionFrame,
)
from .voice_pipeline import VoicePipeline

__all__ = [
    # formats + results
    "AudioFormat",
    "PCM16_MONO_16K",
    "TranscriptionResult",
    "PartialTranscription",
    "TtsSynthesisResult",
    "VadSegment",
    "WakeWordDetectedEventArgs",
    "WakeWordHandler",
    "TranscribedEventArgs",
    # contracts
    "IAudioCapture",
    "IVoiceTranscriber",
    "IWakeWordDetector",
    "IVoiceActivityDetector",
    "ITtsEngine",
    "ISpeakerIdentity",
    "ISpeechEmotionDetector",
    # null implementations
    "NullAudioCapture",
    "NullVoiceTranscriber",
    "NullWakeWordDetector",
    "NullVoiceActivityDetector",
    "NullTtsEngine",
    # deterministic implementations
    "EnergyVadDetector",
    "EnergyWakeWordDetector",
    "VoicePipeline",
    "OnnxSpeakerIdentity",
    "OnnxSpeechEmotionDetector",
    # seams + configs
    "ISpeakerEmbedder",
    "SpeakerIdentityConfig",
    "SpeakerEmbedderInputKind",
    "EnrolledSpeaker",
    "IEmotionClassifier",
    "SpeechEmotionConfig",
    "SpeechEmotionFrame",
]
