# voice/null_implementations.py
#
# Ports of the CircleAI.Voice null implementations (C# — the EXACT spec):
#   NullVoiceTranscriber.cs        -> NullVoiceTranscriber
#   NullWakeWordDetector.cs        -> NullWakeWordDetector
#   NullVoiceActivityDetector.cs   -> NullVoiceActivityDetector
#   NullTtsEngine.cs               -> NullTtsEngine
#
# Each is a fully-implemented, safe default used when no real backend is wired.

from __future__ import annotations

from typing import AsyncIterator, List

from .contracts import (
    ITtsEngine,
    IVoiceActivityDetector,
    IVoiceTranscriber,
    IWakeWordDetector,
    PartialTranscription,
    TranscriptionResult,
    TtsSynthesisResult,
    VadSegment,
    WakeWordHandler,
)


class NullVoiceTranscriber(IVoiceTranscriber):
    """No-op :class:`IVoiceTranscriber`. Returns empty results without consuming
    audio. Mirrors ``CircleAI.Voice.NullVoiceTranscriber``."""

    __slots__ = ("_disposed",)

    def __init__(self) -> None:
        self._disposed = False

    async def transcribe_async(self, pcm_audio: bytes, ct: object = None) -> TranscriptionResult:
        if self._disposed:
            raise RuntimeError("NullVoiceTranscriber is disposed")
        return TranscriptionResult("", 0.0, "und")

    async def stream_transcribe_async(
        self, audio_chunks: AsyncIterator[bytes], ct: object = None
    ) -> AsyncIterator[PartialTranscription]:
        if self._disposed:
            raise RuntimeError("NullVoiceTranscriber is disposed")
        if audio_chunks is None:
            raise ValueError("audio_chunks")
        # Drain the input so callers' producers are not blocked, but emit nothing.
        async for _ in audio_chunks:
            pass
        return
        yield  # pragma: no cover — makes this an async generator

    async def dispose_async(self) -> None:
        self._disposed = True


class NullWakeWordDetector(IWakeWordDetector):
    """No-op :class:`IWakeWordDetector`. Tracks listening state but never raises
    the wake-word event. Mirrors ``CircleAI.Voice.NullWakeWordDetector``."""

    __slots__ = ("_wake_word", "_is_listening", "_disposed", "_handlers")

    def __init__(self, wake_word: str = "Hey B") -> None:
        if wake_word is None or not wake_word.strip():
            raise ValueError("wake_word")
        self._wake_word = wake_word
        self._is_listening = False
        self._disposed = False
        # Declared to satisfy the contract; never invoked (intentional).
        self._handlers: List[WakeWordHandler] = []

    @property
    def wake_word(self) -> str:
        return self._wake_word

    @property
    def is_listening(self) -> bool:
        return self._is_listening

    def add_wake_word_detected_handler(self, handler: WakeWordHandler) -> None:
        self._handlers.append(handler)

    def remove_wake_word_detected_handler(self, handler: WakeWordHandler) -> None:
        try:
            self._handlers.remove(handler)
        except ValueError:
            pass

    async def start_async(self, ct: object = None) -> None:
        if self._disposed:
            raise RuntimeError("NullWakeWordDetector is disposed")
        self._is_listening = True

    async def stop_async(self, ct: object = None) -> None:
        if self._disposed:
            raise RuntimeError("NullWakeWordDetector is disposed")
        self._is_listening = False

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        self._is_listening = False


class NullVoiceActivityDetector(IVoiceActivityDetector):
    """No-op :class:`IVoiceActivityDetector` that passes all audio chunks through
    as speech segments without any analysis.

    Mirrors ``CircleAI.Voice.NullVoiceActivityDetector``."""

    async def detect_async(
        self, audio_stream: AsyncIterator[bytes], cancellation_token: object = None
    ) -> AsyncIterator[VadSegment]:
        if audio_stream is None:
            raise ValueError("audio_stream")
        async for chunk in audio_stream:
            yield VadSegment(chunk, True)


class NullTtsEngine(ITtsEngine):
    """No-op :class:`ITtsEngine`. Returns empty audio and yields nothing.

    Mirrors ``CircleAI.Voice.NullTtsEngine``."""

    #: The PCM format a real engine would use: 24 kHz, mono, 16-bit. Mirrors
    #: ``NullTtsEngine.EmptyResult``.
    EMPTY_RESULT = TtsSynthesisResult(b"", 24_000, 1, 16)

    async def synthesise_async(self, text: str, cancellation_token: object = None) -> TtsSynthesisResult:
        return NullTtsEngine.EMPTY_RESULT

    async def stream_synthesise_async(
        self, text: str, cancellation_token: object = None
    ) -> AsyncIterator[bytes]:
        return
        yield  # pragma: no cover — makes this an async generator


__all__ = [
    "NullVoiceTranscriber",
    "NullWakeWordDetector",
    "NullVoiceActivityDetector",
    "NullTtsEngine",
]
