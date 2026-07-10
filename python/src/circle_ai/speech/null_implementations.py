# speech/null_implementations.py
#
# Port of CircleAI.Speech/NullImplementations.cs (C# — the EXACT spec).
#
# (2.3.0) Fail-closed defaults for each Speech contract. Lets hosting layers wire
# the Speech pack optionally; absence of a real backend degrades to deterministic
# empty answers.

from __future__ import annotations

from datetime import timedelta
from typing import Optional

from .contracts import (
    IDisposable,
    IOpticalCharacterRecognizer,
    ISpeechRecognizer,
    ISpeechSynthesizer,
    IWakeWordDetector,
    OcrResult,
    SynthesisResult,
    TranscriptionResult,
    WakeWordHandler,
)


class NullSpeechRecognizer(ISpeechRecognizer):
    """Mirrors ``CircleAI.Speech.NullSpeechRecognizer``."""

    _instance: "NullSpeechRecognizer | None" = None

    @classmethod
    def instance(cls) -> "NullSpeechRecognizer":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    async def transcribe_async(
        self,
        audio_pcm16_mono: bytes,
        sample_rate_hz: int,
        language_hint: Optional[str] = None,
        ct: object = None,
    ) -> TranscriptionResult:
        return TranscriptionResult(
            text="",
            language=language_hint,
            segments=(),
            total_duration=timedelta(0),
        )


class NullSpeechSynthesizer(ISpeechSynthesizer):
    """Mirrors ``CircleAI.Speech.NullSpeechSynthesizer``."""

    _instance: "NullSpeechSynthesizer | None" = None

    @classmethod
    def instance(cls) -> "NullSpeechSynthesizer":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    async def synthesize_async(
        self,
        text: str,
        voice_id: Optional[str] = None,
        language_hint: Optional[str] = None,
        ct: object = None,
    ) -> SynthesisResult:
        return SynthesisResult(
            audio_pcm16_mono=b"",
            sample_rate_hz=16_000,
            duration=timedelta(0),
        )


class _EmptyDisposable(IDisposable):
    _instance: "_EmptyDisposable | None" = None

    @classmethod
    def instance(cls) -> "_EmptyDisposable":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    def dispose(self) -> None:
        pass


class NullWakeWordDetector(IWakeWordDetector):
    """Mirrors ``CircleAI.Speech.NullWakeWordDetector``."""

    @property
    def backend_id(self) -> str:
        return "null"

    def subscribe(self, handler: WakeWordHandler) -> IDisposable:
        return _EmptyDisposable.instance()

    async def start_async(self, ct: object = None) -> None:
        return None

    async def stop_async(self, ct: object = None) -> None:
        return None

    async def dispose_async(self) -> None:
        return None


class NullOpticalCharacterRecognizer(IOpticalCharacterRecognizer):
    """Mirrors ``CircleAI.Speech.NullOpticalCharacterRecognizer``."""

    _instance: "NullOpticalCharacterRecognizer | None" = None

    @classmethod
    def instance(cls) -> "NullOpticalCharacterRecognizer":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    async def recognize_async(
        self,
        image_bytes: bytes,
        language_hint: Optional[str] = "auto",
        ct: object = None,
    ) -> OcrResult:
        return OcrResult(text="", blocks=())


__all__ = [
    "NullSpeechRecognizer",
    "NullSpeechSynthesizer",
    "NullWakeWordDetector",
    "NullOpticalCharacterRecognizer",
]
