# voice/audio_capture.py
#
# Port of the IAudioCapture surface + NullAudioCapture + TranscribedEventArgs
# from CircleAI.Voice/VoicePipeline.cs (C# — the EXACT spec).

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import AsyncIterator

from .contracts import AudioFormat, PCM16_MONO_16K, TranscriptionResult


class IAudioCapture(ABC):
    """Captures raw audio from a platform input (microphone) and exposes it as an
    asynchronous stream of PCM byte chunks. Implementations produce data in the
    format reported by :attr:`format`.

    Mirrors ``CircleAI.Voice.IAudioCapture`` (``IAsyncDisposable``)."""

    @property
    @abstractmethod
    def format(self) -> AudioFormat:
        """The PCM format produced by :meth:`capture_async`."""
        ...

    @abstractmethod
    def capture_async(self, ct: object = None) -> AsyncIterator[bytes]:
        """Begin capturing audio. The returned sequence yields PCM chunks until
        the cancellation token is signalled or the underlying capture stops."""
        ...

    @abstractmethod
    async def dispose_async(self) -> None:
        ...

    async def __aenter__(self) -> "IAudioCapture":
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self.dispose_async()


class NullAudioCapture(IAudioCapture):
    """No-op :class:`IAudioCapture` that yields no audio. Safe default when no
    platform microphone backend is available.

    Mirrors ``CircleAI.Voice.NullAudioCapture``."""

    @property
    def format(self) -> AudioFormat:
        return PCM16_MONO_16K

    async def capture_async(self, ct: object = None) -> AsyncIterator[bytes]:
        # Yields nothing; completes immediately. (async generator w/ no yields)
        return
        yield  # pragma: no cover — makes this an async generator

    async def dispose_async(self) -> None:
        return None


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass(frozen=True, slots=True)
class TranscribedEventArgs:
    """Payload describing a completed transcription produced by
    :class:`~circle_ai.voice.voice_pipeline.VoicePipeline` after a wake-word
    activation. Mirrors ``CircleAI.Voice.TranscribedEventArgs``.

    :param result: The final transcription result for the activation.
    :param completed_at: UTC timestamp when the transcription completed.
    """

    result: TranscriptionResult
    completed_at: datetime = field(default_factory=_utc_now)


__all__ = [
    "IAudioCapture",
    "NullAudioCapture",
    "TranscribedEventArgs",
]
