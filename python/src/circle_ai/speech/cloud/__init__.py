"""circle_ai.speech.cloud — port of the CircleAI.Speech.Cloud assembly.

The cloud pack's provider-specific ASR/TTS engines (Azure, Deepgram, OpenAI,
ElevenLabs, ...) are injected external dependencies with no in-memory analogue;
what ports faithfully is the hermetic, rule-based voice-intent router (C# is the
exact spec):

    VoiceIntent, VoiceIntentMatch,
    IVoiceIntentRouter, KeywordVoiceIntentRouter, NullVoiceIntentRouter.
"""
from __future__ import annotations

from .keyword_voice_intent_router import (
    IVoiceIntentRouter,
    KeywordVoiceIntentRouter,
    NullVoiceIntentRouter,
    VoiceIntent,
    VoiceIntentMatch,
)

__all__ = [
    "VoiceIntent",
    "VoiceIntentMatch",
    "IVoiceIntentRouter",
    "KeywordVoiceIntentRouter",
    "NullVoiceIntentRouter",
]
