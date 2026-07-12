"""circle_ai.speech.cloud — port of the CircleAI.Speech.Cloud assembly.

The cloud pack's provider-specific ASR/TTS engines (OpenAI, Deepgram, Azure,
Google, AssemblyAI, Cartesia, ElevenLabs, PlayHT) plus the hermetic, rule-based
voice-intent router. C# is the exact spec.

The provider recognizers/synthesizers each drive a ``System.Net.Http.HttpClient``
in the C#; the Python ports inject the shared
``circle_ai.integration.http.IHttpFetcher`` instead (the same seam every other
``.Cloud`` port in this tree uses). The request construction and response parsing
are ported faithfully:

  * WAV envelope / raw-PCM upload / base64 audio all built with ``struct`` /
    ``base64`` in :mod:`circle_ai.speech.cloud._audio_http`.
  * ``multipart/form-data`` bodies (OpenAI + Cartesia STT) built byte-for-byte.
  * Binary audio bodies ride the fetcher's ``HttpRequest.body_bytes`` /
    ``HttpResponse.content_bytes`` seam; JSON bodies ride ``body_json`` and are
    read back off ``resp.json()`` exactly as the C# reads ``JsonDocument``.
  * Every adapter is fail-soft: an unconfigured key returns an empty
    ``TranscriptionResult`` / ``SynthesisResult`` (never raises), so a fallback
    router can move on — mirroring the C#.

The C# ``SpeechCloudServiceCollectionExtensions`` (Microsoft.Extensions.
DependencyInjection plumbing) has no Python analogue and is intentionally
omitted, matching the other ``.Cloud`` ports.

Public surface:

  * Voice-intent router: VoiceIntent, VoiceIntentMatch, IVoiceIntentRouter,
    KeywordVoiceIntentRouter, NullVoiceIntentRouter.
  * Options: OpenAiVoiceOptions, DeepgramOptions, AssemblyAiOptions,
    GoogleSpeechOptions, AzureSpeechOptions, ElevenLabsOptions, CartesiaTtsOptions,
    DeepgramTtsOptions, AzureTtsOptions, GoogleTtsOptions, PlayHtOptions,
    CartesiaSttOptions.
  * Recognizers (ISpeechRecognizer): OpenAiSpeechRecognizer,
    DeepgramSpeechRecognizer, AzureSpeechRecognizer, GoogleSpeechRecognizer,
    AssemblyAiSpeechRecognizer, CartesiaSpeechRecognizer.
  * Synthesizers (ISpeechSynthesizer): OpenAiSpeechSynthesizer,
    DeepgramSpeechSynthesizer, AzureSpeechSynthesizer, GoogleSpeechSynthesizer,
    CartesiaSpeechSynthesizer, ElevenLabsSpeechSynthesizer, PlayHtSpeechSynthesizer.
"""
from __future__ import annotations

from .assemblyai_speech_recognizer import AssemblyAiSpeechRecognizer
from .azure_speech_recognizer import AzureSpeechRecognizer
from .azure_speech_synthesizer import AzureSpeechSynthesizer
from .cartesia_speech_recognizer import CartesiaSpeechRecognizer
from .cartesia_speech_synthesizer import CartesiaSpeechSynthesizer
from .deepgram_speech_recognizer import DeepgramSpeechRecognizer
from .deepgram_speech_synthesizer import DeepgramSpeechSynthesizer
from .elevenlabs_speech_synthesizer import ElevenLabsSpeechSynthesizer
from .google_speech_recognizer import GoogleSpeechRecognizer
from .google_speech_synthesizer import GoogleSpeechSynthesizer
from .keyword_voice_intent_router import (
    IVoiceIntentRouter,
    KeywordVoiceIntentRouter,
    NullVoiceIntentRouter,
    VoiceIntent,
    VoiceIntentMatch,
)
from .openai_speech_recognizer import OpenAiSpeechRecognizer
from .openai_speech_synthesizer import OpenAiSpeechSynthesizer
from .options import (
    AssemblyAiOptions,
    AzureSpeechOptions,
    AzureTtsOptions,
    CartesiaSttOptions,
    CartesiaTtsOptions,
    DeepgramOptions,
    DeepgramTtsOptions,
    ElevenLabsOptions,
    GoogleSpeechOptions,
    GoogleTtsOptions,
    OpenAiVoiceOptions,
    PlayHtOptions,
)
from .playht_speech_synthesizer import PlayHtSpeechSynthesizer

__all__ = [
    # voice-intent router
    "VoiceIntent",
    "VoiceIntentMatch",
    "IVoiceIntentRouter",
    "KeywordVoiceIntentRouter",
    "NullVoiceIntentRouter",
    # options
    "OpenAiVoiceOptions",
    "DeepgramOptions",
    "AssemblyAiOptions",
    "GoogleSpeechOptions",
    "AzureSpeechOptions",
    "ElevenLabsOptions",
    "CartesiaTtsOptions",
    "DeepgramTtsOptions",
    "AzureTtsOptions",
    "GoogleTtsOptions",
    "PlayHtOptions",
    "CartesiaSttOptions",
    # recognizers
    "OpenAiSpeechRecognizer",
    "DeepgramSpeechRecognizer",
    "AzureSpeechRecognizer",
    "GoogleSpeechRecognizer",
    "AssemblyAiSpeechRecognizer",
    "CartesiaSpeechRecognizer",
    # synthesizers
    "OpenAiSpeechSynthesizer",
    "DeepgramSpeechSynthesizer",
    "AzureSpeechSynthesizer",
    "GoogleSpeechSynthesizer",
    "CartesiaSpeechSynthesizer",
    "ElevenLabsSpeechSynthesizer",
    "PlayHtSpeechSynthesizer",
]
