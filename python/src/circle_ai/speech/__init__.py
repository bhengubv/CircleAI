"""circle_ai.speech — port of the CircleAI.Speech + CircleAI.Speech.Cloud assemblies.

The ASR / TTS / wake-word / OCR contract surface for B! Butler's voice loop
(C# is the exact spec), plus every deterministic in-memory implementation and
the pure-Python audio DSP (echo cancellation, noise reduction, VAD, end-of-turn,
G.711 codec conversion). Native / ONNX / cloud engines are injected behind the
``*ModelRunner`` seams — no real audio is required.

Public surface:

  * Contracts + records:
      TranscribedSegment, TranscriptionResult, SynthesisResult,
      OcrTextBlock, OcrResult, WakeWordEvent, EndOfTurnResult, VadFrameResult,
      ISpeechRecognizer, ISpeechSynthesizer, IWakeWordDetector, WakeWordHandler,
      IDisposable, IEchoCanceller, INoiseReducer, IEndOfTurnDetector,
      IVoiceActivityDetector, IOpticalCharacterRecognizer.
  * Null (fail-closed) implementations:
      NullSpeechRecognizer, NullSpeechSynthesizer, NullWakeWordDetector,
      NullOpticalCharacterRecognizer, NullEchoCanceller, NullNoiseReducer,
      NullVoiceActivityDetector, NullEndOfTurnDetector.
  * Deterministic working implementations:
      KeywordSpeechRecognizer, TemplateSpeechSynthesizer, KeywordWakeWordDetector,
      NlmsEchoCanceller, WebRtcEchoCanceller, SpectralSubtractionNoiseReducer,
      KrispNoiseReducer, DeepFilterNetNoiseReducer, EnergyVoiceActivityDetector,
      SileroVoiceActivityDetector, RuleBasedEndOfTurnDetector, SmartTurnDetector.
  * Injected model-runner seams:
      IEchoCancellerModelRunner, INoiseReducerModelRunner, IVadModelRunner,
      ITurnModelRunner.
  * Audio format conversion:
      AudioCodec, AudioFormatConverter.
  * Cloud pack (CircleAI.Speech.Cloud):
      VoiceIntent, VoiceIntentMatch, IVoiceIntentRouter,
      KeywordVoiceIntentRouter, NullVoiceIntentRouter.
"""
from __future__ import annotations

from .audio_format_converter import (
    AudioCodec,
    AudioFormatConverter,
    decode_a_law_to_pcm16,
    decode_mu_law_to_pcm16,
    encode_pcm16_to_a_law,
    encode_pcm16_to_mu_law,
    resample_pcm16_linear,
)
from .cloud import (
    IVoiceIntentRouter,
    KeywordVoiceIntentRouter,
    NullVoiceIntentRouter,
    VoiceIntent,
    VoiceIntentMatch,
)
from .contracts import (
    EndOfTurnResult,
    IDisposable,
    IEchoCanceller,
    IEndOfTurnDetector,
    INoiseReducer,
    IOpticalCharacterRecognizer,
    ISpeechRecognizer,
    ISpeechSynthesizer,
    IVoiceActivityDetector,
    IWakeWordDetector,
    OcrResult,
    OcrTextBlock,
    SynthesisResult,
    TranscribedSegment,
    TranscriptionResult,
    VadFrameResult,
    WakeWordEvent,
    WakeWordHandler,
)
from .echo_cancellers import (
    IEchoCancellerModelRunner,
    NlmsEchoCanceller,
    NullEchoCanceller,
    WebRtcEchoCanceller,
)
from .end_of_turn_detectors import (
    ITurnModelRunner,
    NullEndOfTurnDetector,
    RuleBasedEndOfTurnDetector,
    SmartTurnDetector,
)
from .in_memory_implementations import (
    KeywordSpeechRecognizer,
    KeywordWakeWordDetector,
    TemplateSpeechSynthesizer,
)
from .noise_reducers import (
    DeepFilterNetNoiseReducer,
    INoiseReducerModelRunner,
    KrispNoiseReducer,
    NullNoiseReducer,
    SpectralSubtractionNoiseReducer,
)
from .null_implementations import (
    NullOpticalCharacterRecognizer,
    NullSpeechRecognizer,
    NullSpeechSynthesizer,
    NullWakeWordDetector,
)
from .voice_activity_detectors import (
    EnergyVoiceActivityDetector,
    IVadModelRunner,
    NullVoiceActivityDetector,
    SileroVoiceActivityDetector,
)

__all__ = [
    # contracts + records
    "TranscribedSegment",
    "TranscriptionResult",
    "SynthesisResult",
    "OcrTextBlock",
    "OcrResult",
    "WakeWordEvent",
    "EndOfTurnResult",
    "VadFrameResult",
    "ISpeechRecognizer",
    "ISpeechSynthesizer",
    "IWakeWordDetector",
    "WakeWordHandler",
    "IDisposable",
    "IEchoCanceller",
    "INoiseReducer",
    "IEndOfTurnDetector",
    "IVoiceActivityDetector",
    "IOpticalCharacterRecognizer",
    # null implementations
    "NullSpeechRecognizer",
    "NullSpeechSynthesizer",
    "NullWakeWordDetector",
    "NullOpticalCharacterRecognizer",
    "NullEchoCanceller",
    "NullNoiseReducer",
    "NullVoiceActivityDetector",
    "NullEndOfTurnDetector",
    # deterministic implementations
    "KeywordSpeechRecognizer",
    "TemplateSpeechSynthesizer",
    "KeywordWakeWordDetector",
    "NlmsEchoCanceller",
    "WebRtcEchoCanceller",
    "SpectralSubtractionNoiseReducer",
    "KrispNoiseReducer",
    "DeepFilterNetNoiseReducer",
    "EnergyVoiceActivityDetector",
    "SileroVoiceActivityDetector",
    "RuleBasedEndOfTurnDetector",
    "SmartTurnDetector",
    # model-runner seams
    "IEchoCancellerModelRunner",
    "INoiseReducerModelRunner",
    "IVadModelRunner",
    "ITurnModelRunner",
    # audio format conversion
    "AudioCodec",
    "AudioFormatConverter",
    "decode_mu_law_to_pcm16",
    "encode_pcm16_to_mu_law",
    "decode_a_law_to_pcm16",
    "encode_pcm16_to_a_law",
    "resample_pcm16_linear",
    # cloud pack
    "VoiceIntent",
    "VoiceIntentMatch",
    "IVoiceIntentRouter",
    "KeywordVoiceIntentRouter",
    "NullVoiceIntentRouter",
]
