# voice/contracts.py
#
# Port of the CircleAI.Voice contract surface (C# — the EXACT spec):
#   AudioFormat.cs             -> AudioFormat
#   IAudioCapture (VoicePipeline.cs) -> IAudioCapture (+ TranscribedEventArgs)
#   IVoiceTranscriber.cs       -> IVoiceTranscriber, TranscriptionResult, PartialTranscription
#   IWakeWordDetector.cs       -> IWakeWordDetector, WakeWordDetectedEventArgs
#   IVoiceActivityDetector.cs  -> IVoiceActivityDetector, VadSegment
#   ITtsEngine.cs              -> ITtsEngine, TtsSynthesisResult
#
# C# -> Python mapping:
#   ReadOnlyMemory<byte>                              -> bytes
#   IAsyncEnumerable<T>                               -> AsyncIterator[T]
#   Task<T>                                           -> async def -> T
#   IAsyncDisposable                                  -> dispose_async() + async context mgr
#   event EventHandler<TArgs>                         -> add_*_handler / remove_*_handler
#                                                        (handler list of (sender, args) -> None)
#   DateTimeOffset                                    -> datetime (tz-aware, UTC)

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import AsyncIterator, Callable, Optional

# ── AudioFormat ────────────────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class AudioFormat:
    """Describes a PCM audio format expected or produced by voice components.

    Mirrors ``CircleAI.Voice.AudioFormat``.

    :param sample_rate: Samples per second (e.g. 16000 for 16 kHz).
    :param channels: Number of interleaved channels (1 = mono, 2 = stereo).
    :param bits_per_sample: Bit depth of each sample (e.g. 16 for signed 16-bit PCM).
    """

    sample_rate: int
    channels: int
    bits_per_sample: int


#: Canonical input format expected by Butler / B! voice components: PCM signed
#: 16-bit, mono, 16 kHz. Mirrors ``AudioFormat.Pcm16Mono16k``.
PCM16_MONO_16K = AudioFormat(16_000, 1, 16)
# Attach as a class attribute so callers can use AudioFormat.Pcm16Mono16k too.
AudioFormat.Pcm16Mono16k = PCM16_MONO_16K  # type: ignore[attr-defined]


# ── Transcription results ──────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class TranscriptionResult:
    """Final transcription result. Mirrors ``CircleAI.Voice.TranscriptionResult``.

    :param text: The recognised text. Empty string if nothing was recognised.
    :param confidence: Engine-reported confidence in the range [0, 1].
    :param language_code: Detected language as a BCP-47 / ISO 639 code
        (e.g. "en", "zu", "und" for unknown).
    """

    text: str
    confidence: float
    language_code: str


@dataclass(frozen=True, slots=True)
class PartialTranscription:
    """Partial or final transcription produced during streaming recognition.

    Mirrors ``CircleAI.Voice.PartialTranscription``.

    :param text: The recognised text so far.
    :param is_final: True when this is the final transcription for the current
        utterance; False for in-progress hypotheses that may still change.
    :param confidence: Engine-reported confidence in the range [0, 1].
    """

    text: str
    is_final: bool
    confidence: float


class IVoiceTranscriber(ABC):
    """Converts captured audio into text. Implementations consume PCM 16-bit,
    16 kHz mono input (:data:`AudioFormat.Pcm16Mono16k`) unless documented
    otherwise. Mirrors ``CircleAI.Voice.IVoiceTranscriber`` (``IAsyncDisposable``)."""

    @abstractmethod
    async def transcribe_async(self, pcm_audio: bytes, ct: object = None) -> TranscriptionResult:
        """Transcribe a complete audio buffer (PCM 16-bit, 16 kHz mono)."""
        ...

    @abstractmethod
    def stream_transcribe_async(
        self, audio_chunks: AsyncIterator[bytes], ct: object = None
    ) -> AsyncIterator[PartialTranscription]:
        """Stream audio chunks and receive partial transcriptions as the engine
        produces them. The final element has ``is_final`` set to True."""
        ...

    @abstractmethod
    async def dispose_async(self) -> None:
        ...

    async def __aenter__(self) -> "IVoiceTranscriber":
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self.dispose_async()


# ── Wake-word detection ────────────────────────────────────────────────────────


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass(frozen=True, slots=True)
class WakeWordDetectedEventArgs:
    """Payload describing a single wake-word detection event.

    Mirrors ``CircleAI.Voice.WakeWordDetectedEventArgs``.

    :param wake_word: The wake word phrase that was detected.
    :param detected_at: UTC timestamp at which the detection fired.
    :param confidence: Detector-reported confidence in [0, 1]. Implementations
        that do not produce a score report 1.0.
    """

    wake_word: str
    confidence: float = 0.0
    detected_at: datetime = field(default_factory=_utc_now)


#: Wake-word event handler: ``(sender, args) -> None`` — the analogue of a C#
#: ``EventHandler<WakeWordDetectedEventArgs>`` subscriber.
WakeWordHandler = Callable[[object, WakeWordDetectedEventArgs], None]


class IWakeWordDetector(ABC):
    """Detects a configured wake word in a continuous audio stream and raises the
    wake-word event when the phrase is recognised. Implementations manage their
    own audio capture pipeline between :meth:`start_async` and :meth:`stop_async`.

    Mirrors ``CircleAI.Voice.IWakeWordDetector`` (``IAsyncDisposable``). The C#
    ``event EventHandler<WakeWordDetectedEventArgs>? WakeWordDetected`` is modelled
    as a subscriber list via :meth:`add_wake_word_detected_handler` /
    :meth:`remove_wake_word_detected_handler`."""

    @property
    @abstractmethod
    def wake_word(self) -> str:
        """The phrase the detector listens for (e.g. "Hey B")."""
        ...

    @property
    @abstractmethod
    def is_listening(self) -> bool:
        """True when the detector is actively listening for the wake word."""
        ...

    @abstractmethod
    def add_wake_word_detected_handler(self, handler: WakeWordHandler) -> None:
        """Subscribe to the wake-word detection event."""
        ...

    @abstractmethod
    def remove_wake_word_detected_handler(self, handler: WakeWordHandler) -> None:
        """Unsubscribe from the wake-word detection event."""
        ...

    @abstractmethod
    async def start_async(self, ct: object = None) -> None:
        """Begin listening for the wake word. Idempotent."""
        ...

    @abstractmethod
    async def stop_async(self, ct: object = None) -> None:
        """Stop listening and release audio-capture resources. Idempotent."""
        ...

    @abstractmethod
    async def dispose_async(self) -> None:
        ...

    async def __aenter__(self) -> "IWakeWordDetector":
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self.dispose_async()


# ── Voice activity detection ───────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class VadSegment:
    """A single segment identified by an :class:`IVoiceActivityDetector`.

    Mirrors ``CircleAI.Voice.VadSegment``.

    :param audio: Raw PCM audio bytes for this segment. Non-empty for speech
        segments; may be empty for silence markers.
    :param is_speech: True when this segment contains detected speech and should
        be forwarded to the transcriber. False for silence/noise markers.
    """

    audio: bytes
    is_speech: bool


class IVoiceActivityDetector(ABC):
    """Detects speech vs silence in a raw PCM audio stream. Returns only the
    segments that contain speech, trimming leading and trailing silence.

    Mirrors ``CircleAI.Voice.IVoiceActivityDetector``."""

    @abstractmethod
    def detect_async(
        self, audio_stream: AsyncIterator[bytes], cancellation_token: object = None
    ) -> AsyncIterator[VadSegment]:
        """Process an incoming audio stream and yield speech-containing segments."""
        ...


# ── Text-to-speech ─────────────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class TtsSynthesisResult:
    """Result of a single-shot TTS synthesis operation.

    Mirrors ``CircleAI.Voice.TtsSynthesisResult``.

    :param audio_data: The complete PCM audio buffer. Empty when the engine
        produced no audio (e.g. empty input text or null implementation).
    :param sample_rate: Samples per second (e.g. 24000 for 24 kHz).
    :param channels: Number of interleaved audio channels (1 = mono, 2 = stereo).
    :param bits_per_sample: Bit depth of each sample (e.g. 16 for signed 16-bit PCM).
    """

    audio_data: bytes
    sample_rate: int
    channels: int
    bits_per_sample: int


class ITtsEngine(ABC):
    """Text-to-speech engine that converts generated text into PCM audio.

    Mirrors ``CircleAI.Voice.ITtsEngine``."""

    @abstractmethod
    async def synthesise_async(self, text: str, cancellation_token: object = None) -> TtsSynthesisResult:
        """Synthesise ``text`` to a single PCM audio buffer."""
        ...

    @abstractmethod
    def stream_synthesise_async(
        self, text: str, cancellation_token: object = None
    ) -> AsyncIterator[bytes]:
        """Stream PCM audio chunks as they are synthesised, enabling low-latency
        playback that begins before the full sentence is complete."""
        ...


__all__ = [
    "AudioFormat",
    "PCM16_MONO_16K",
    "TranscriptionResult",
    "PartialTranscription",
    "IVoiceTranscriber",
    "WakeWordDetectedEventArgs",
    "WakeWordHandler",
    "IWakeWordDetector",
    "VadSegment",
    "IVoiceActivityDetector",
    "TtsSynthesisResult",
    "ITtsEngine",
]
