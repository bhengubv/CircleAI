# speech/contracts.py
#
# Port of CircleAI.Speech/Contracts.cs (C# — the EXACT spec).
#
# (2.3.0) The CircleAI.Speech contract surface. ASR / TTS / wake-word / OCR —
# every primitive needed for B! Butler's voice loop. Deterministic in-memory
# implementations live alongside; native/ONNX/cloud engines are injected
# dependencies (see the *ModelRunner seams).
#
# C# -> Python type mapping used throughout this module:
#   ReadOnlyMemory<byte> / ReadOnlySpan<byte> -> bytes
#   Span<byte>                                 -> bytearray
#   TimeSpan                                   -> datetime.timedelta
#   DateTimeOffset                             -> datetime (tz-aware, UTC)
#   ValueTask<T>                               -> async def -> T
#   IReadOnlyList<T>                           -> tuple[T, ...]
#   float (C# System.Single)                   -> float

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from typing import Awaitable, Callable, Optional, Tuple

# ── records ───────────────────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class TranscribedSegment:
    """One transcribed segment. Mirrors ``CircleAI.Speech.TranscribedSegment``."""

    text: str
    offset: timedelta
    duration: timedelta
    language: Optional[str] = None
    confidence: float = 0.0


@dataclass(frozen=True, slots=True)
class TranscriptionResult:
    """Outcome of one ASR call. Mirrors ``CircleAI.Speech.TranscriptionResult``."""

    text: str
    language: Optional[str]
    segments: Tuple[TranscribedSegment, ...]
    total_duration: timedelta


@dataclass(frozen=True, slots=True)
class SynthesisResult:
    """Outcome of one TTS call. Mirrors ``CircleAI.Speech.SynthesisResult``."""

    audio_pcm16_mono: bytes
    sample_rate_hz: int
    duration: timedelta


@dataclass(frozen=True, slots=True)
class OcrTextBlock:
    """One detected text block in an OCR result. Mirrors ``CircleAI.Speech.OcrTextBlock``."""

    text: str
    x: int
    y: int
    width: int
    height: int
    confidence: float
    language: Optional[str] = None


@dataclass(frozen=True, slots=True)
class OcrResult:
    """One OCR result. Mirrors ``CircleAI.Speech.OcrResult``."""

    text: str
    blocks: Tuple[OcrTextBlock, ...]


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass(frozen=True, slots=True)
class WakeWordEvent:
    """One wake-word fire. Mirrors ``CircleAI.Speech.WakeWordEvent``."""

    keyword: str
    confidence: float
    detected_at_utc: datetime = field(default_factory=_utc_now)


@dataclass(frozen=True, slots=True)
class EndOfTurnResult:
    """(3.3.0) Verdict on whether a partial transcript represents a finished thought.

    Mirrors ``CircleAI.Speech.EndOfTurnResult``.

    :param is_complete: True if the speaker likely finished their turn.
    :param confidence: 0..1 confidence.
    :param wait_more_ms: If ``is_complete`` is False, how many extra ms to wait
        before re-asking.
    """

    is_complete: bool
    confidence: float
    wait_more_ms: int


@dataclass(frozen=True, slots=True)
class VadFrameResult:
    """(3.3.0) One verdict from a voice-activity detector.

    Mirrors ``CircleAI.Speech.VadFrameResult``.

    :param is_speech: True if this frame contains speech.
    :param speech_probability: 0..1 confidence the frame is speech.
    :param offset: Frame start offset relative to the stream start.
    """

    is_speech: bool
    speech_probability: float
    offset: timedelta


# ── contracts ─────────────────────────────────────────────────────────────────


class ISpeechRecognizer(ABC):
    """(2.3.0) Convert audio to text. Mirrors ``CircleAI.Speech.ISpeechRecognizer``."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "funasr-1.x" / "yapsnap" / "null"."""
        ...

    @abstractmethod
    async def transcribe_async(
        self,
        audio_pcm16_mono: bytes,
        sample_rate_hz: int,
        language_hint: Optional[str] = None,
        ct: object = None,
    ) -> TranscriptionResult:
        """Recognise one buffer of PCM-16 mono audio."""
        ...


class ISpeechSynthesizer(ABC):
    """(2.3.0) Convert text to spoken audio. Mirrors ``CircleAI.Speech.ISpeechSynthesizer``."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "chattts" / "null"."""
        ...

    @abstractmethod
    async def synthesize_async(
        self,
        text: str,
        voice_id: Optional[str] = None,
        language_hint: Optional[str] = None,
        ct: object = None,
    ) -> SynthesisResult:
        """Synthesise one utterance. Returns PCM-16 mono."""
        ...


# Handler for wake-word subscriptions: ``Func<WakeWordEvent, ValueTask>``.
WakeWordHandler = Callable[[WakeWordEvent], Awaitable[None]]


class IDisposable(ABC):
    """Subscription handle mirroring C# ``IDisposable``."""

    @abstractmethod
    def dispose(self) -> None:
        ...

    def __enter__(self) -> "IDisposable":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.dispose()


class IWakeWordDetector(ABC):
    """(2.3.0) Spot a wake word ("Hey B") in a continuous audio stream.

    Mirrors ``CircleAI.Speech.IWakeWordDetector`` (``IAsyncDisposable``).
    Implementations are long-running (``start_async`` / ``stop_async``).
    """

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "hey-snips" / "null"."""
        ...

    @abstractmethod
    def subscribe(self, handler: WakeWordHandler) -> IDisposable:
        """Subscribe to wake-word fire events."""
        ...

    @abstractmethod
    async def start_async(self, ct: object = None) -> None:
        """Begin listening on the system mic. Idempotent."""
        ...

    @abstractmethod
    async def stop_async(self, ct: object = None) -> None:
        """Stop listening. Idempotent."""
        ...

    @abstractmethod
    async def dispose_async(self) -> None:
        ...

    async def __aenter__(self) -> "IWakeWordDetector":
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self.dispose_async()


class IEchoCanceller(ABC):
    """(3.3.0) Acoustic echo canceller — subtracts the far-end reference from the
    near-end mic input. Mirrors ``CircleAI.Speech.IEchoCanceller``."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "nlms" / "webrtc-aec3" / "null"."""
        ...

    @abstractmethod
    def cancel(
        self,
        near_end_microphone: bytes,
        far_end_reference: bytes,
        sample_rate_hz: int,
        destination: bytearray,
    ) -> int:
        """Cancel echo of ``far_end_reference`` out of ``near_end_microphone``.

        Writes the result into ``destination``. Both inputs must be the same
        sample rate and length (PCM-16 mono). Returns the number of bytes written.
        """
        ...

    @abstractmethod
    def reset(self) -> None:
        """Reset adaptive-filter state at the start of a new call."""
        ...


class INoiseReducer(ABC):
    """(3.3.0) Audio noise reducer — cleans a frame of PCM-16 mono audio.

    Mirrors ``CircleAI.Speech.INoiseReducer``."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "krisp" / "deepfilternet" / "passthrough" / "null"."""
        ...

    @property
    @abstractmethod
    def is_available(self) -> bool:
        """True when the underlying model / runtime is available."""
        ...

    @abstractmethod
    def reduce(self, audio_pcm16_mono: bytes, sample_rate_hz: int, destination: bytearray) -> int:
        """Reduce noise in ``audio_pcm16_mono`` and write into ``destination``.

        The destination buffer must be at least as long as the input. Returns the
        number of bytes written.
        """
        ...


class IEndOfTurnDetector(ABC):
    """(3.3.0) Decide whether the caller has finished their turn given the latest
    partial transcript + the trailing-silence duration. VAD says "they're silent
    now"; this says "they're DONE." Mirrors ``CircleAI.Speech.IEndOfTurnDetector``."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "rules" / "smart-turn-v2" / "null"."""
        ...

    @abstractmethod
    def predict(self, partial_transcript: str, trailing_silence: timedelta) -> EndOfTurnResult:
        """Classify the current state."""
        ...

    @abstractmethod
    def reset(self) -> None:
        """Reset internal state at the start of a fresh turn."""
        ...


class IVoiceActivityDetector(ABC):
    """(3.3.0) Voice-activity detector. Implementations classify each 10-30 ms
    audio frame as speech or silence so a voice loop knows when the caller has
    started/stopped talking. Mirrors ``CircleAI.Speech.IVoiceActivityDetector``."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "energy" / "silero" / "null"."""
        ...

    @property
    @abstractmethod
    def speech_threshold(self) -> float:
        """Speech probability threshold for ``VadFrameResult.is_speech``."""
        ...

    @abstractmethod
    def classify(self, audio_pcm16_mono: bytes, sample_rate_hz: int, offset: timedelta) -> VadFrameResult:
        """Classify one frame of PCM-16 mono audio."""
        ...

    @abstractmethod
    def reset(self) -> None:
        """Reset any internal hangover state at the start of a fresh utterance."""
        ...


class IOpticalCharacterRecognizer(ABC):
    """(2.3.0) Read text out of an image. Mirrors ``CircleAI.Speech.IOpticalCharacterRecognizer``."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "paddleocr-2.x" / "null"."""
        ...

    @abstractmethod
    async def recognize_async(
        self,
        image_bytes: bytes,
        language_hint: Optional[str] = "auto",
        ct: object = None,
    ) -> OcrResult:
        """Recognise text in an image. ``language_hint`` e.g. "eng" / "chi" / "auto"."""
        ...


__all__ = [
    # records
    "TranscribedSegment",
    "TranscriptionResult",
    "SynthesisResult",
    "OcrTextBlock",
    "OcrResult",
    "WakeWordEvent",
    "EndOfTurnResult",
    "VadFrameResult",
    # contracts
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
]
