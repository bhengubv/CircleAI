# speech/cloud/options.py
#
# Port of CircleAI.Speech.Cloud/Options.cs (C# — the EXACT spec).
#
# (3.2.0 / 3.3.0) Provider-specific options for the cloud ASR/TTS adapters.
# Every default is preserved verbatim from the C# so the request shapes are
# byte-identical. The C# ``Uri BaseAddress`` maps to the base-URL string
# ``base_address``; ``Uri? BaseAddress`` (Azure) maps to ``Optional[str]``.

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional


@dataclass(frozen=True, slots=True)
class OpenAiVoiceOptions:
    """(3.2.0) OpenAI Whisper + TTS options. Mirrors ``OpenAiVoiceOptions``."""

    base_address: str = "https://api.openai.com"
    api_key: Optional[str] = None
    #: Whisper model. Default ``whisper-1``.
    transcription_model: str = "whisper-1"
    #: TTS model. Default ``tts-1``.
    speech_model: str = "tts-1"
    #: Default voice id (alloy / echo / fable / onyx / nova / shimmer).
    default_voice: str = "alloy"
    #: PCM sample rate the TTS endpoint returns for ``response_format=pcm``
    #: (OpenAI documents 24 kHz mono 16-bit).
    pcm_sample_rate_hz: int = 24_000


@dataclass(frozen=True, slots=True)
class DeepgramOptions:
    """(3.3.0) Deepgram STT options. Bearer-equivalent auth via "Token <key>"."""

    base_address: str = "https://api.deepgram.com"
    api_key: Optional[str] = None
    #: Model id — defaults to ``nova-2-general``.
    model: str = "nova-2-general"


@dataclass(frozen=True, slots=True)
class AssemblyAiOptions:
    """(3.3.0) AssemblyAI STT options. Mirrors ``AssemblyAiOptions``."""

    base_address: str = "https://api.assemblyai.com"
    api_key: Optional[str] = None
    #: Speech model — defaults to ``universal``.
    speech_model: str = "universal"


@dataclass(frozen=True, slots=True)
class GoogleSpeechOptions:
    """(3.3.0) Google Cloud Speech-to-Text options (REST v1 + API-key auth)."""

    base_address: str = "https://speech.googleapis.com"
    api_key: Optional[str] = None
    language_code: str = "en-US"


@dataclass(frozen=True, slots=True)
class AzureSpeechOptions:
    """(3.3.0) Microsoft Azure Speech-to-Text options.

    ``base_address`` is a region-specific endpoint, e.g.
    ``https://eastus.stt.speech.microsoft.com`` (``None`` -> not configured).
    """

    base_address: Optional[str] = None
    api_key: Optional[str] = None
    language_code: str = "en-US"


@dataclass(frozen=True, slots=True)
class ElevenLabsOptions:
    """(3.3.0) ElevenLabs TTS options. Mirrors ``ElevenLabsOptions``."""

    base_address: str = "https://api.elevenlabs.io"
    api_key: Optional[str] = None
    #: Default voice id (ElevenLabs UUID — varies per account). Default "Rachel".
    default_voice_id: str = "21m00Tcm4TlvDq8ikWAM"
    #: Model id. Defaults to flash for low latency.
    model: str = "eleven_flash_v2_5"
    #: Output format. Returns PCM at 16/22/24/44 kHz.
    output_format: str = "pcm_24000"
    pcm_sample_rate_hz: int = 24_000


@dataclass(frozen=True, slots=True)
class CartesiaTtsOptions:
    """(3.3.0) Cartesia Sonic TTS options. Mirrors ``CartesiaTtsOptions``."""

    base_address: str = "https://api.cartesia.ai"
    api_key: Optional[str] = None
    model: str = "sonic-2"
    default_voice_id: str = "a0e99841-438c-4a64-b679-ae501e7d6091"
    output_container: str = "raw"
    output_encoding: str = "pcm_s16le"
    pcm_sample_rate_hz: int = 24_000
    cartesia_version: str = "2025-04-16"


@dataclass(frozen=True, slots=True)
class DeepgramTtsOptions:
    """(3.3.0) Deepgram Aura TTS options. Mirrors ``DeepgramTtsOptions``."""

    base_address: str = "https://api.deepgram.com"
    api_key: Optional[str] = None
    #: Aura voice model — defaults to ``aura-asteria-en``.
    voice: str = "aura-asteria-en"
    pcm_sample_rate_hz: int = 24_000


@dataclass(frozen=True, slots=True)
class AzureTtsOptions:
    """(3.3.0) Microsoft Azure Speech TTS options.

    ``base_address`` is a region-specific endpoint, e.g.
    ``https://eastus.tts.speech.microsoft.com`` (``None`` -> not configured).
    """

    base_address: Optional[str] = None
    api_key: Optional[str] = None
    language_code: str = "en-US"
    default_voice_name: str = "en-US-AvaMultilingualNeural"
    pcm_sample_rate_hz: int = 24_000


@dataclass(frozen=True, slots=True)
class GoogleTtsOptions:
    """(3.3.0) Google Cloud Text-to-Speech options. Mirrors ``GoogleTtsOptions``."""

    base_address: str = "https://texttospeech.googleapis.com"
    api_key: Optional[str] = None
    language_code: str = "en-US"
    default_voice_name: str = "en-US-Studio-O"
    pcm_sample_rate_hz: int = 24_000


@dataclass(frozen=True, slots=True)
class PlayHtOptions:
    """(3.3.0) PlayHT TTS options. Mirrors ``PlayHtOptions``."""

    base_address: str = "https://api.play.ht"
    api_key: Optional[str] = None
    user_id: Optional[str] = None
    default_voice: str = (
        "s3://voice-cloning-zero-shot/d9ff78ba-d016-47f6-b0ef-dd630f59414e/female-cs/manifest.json"
    )
    model: str = "PlayDialog"
    pcm_sample_rate_hz: int = 24_000


@dataclass(frozen=True, slots=True)
class CartesiaSttOptions:
    """(3.3.0) Cartesia STT options (Bearer auth). Mirrors ``CartesiaSttOptions``."""

    base_address: str = "https://api.cartesia.ai"
    api_key: Optional[str] = None
    #: Model id — defaults to Cartesia's default English STT model.
    model: str = "ink-whisper"
    #: API version header value. Defaults to current stable.
    cartesia_version: str = "2025-04-16"


__all__ = [
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
]
