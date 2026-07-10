# speech/in_memory_implementations.py
#
# Deterministic in-memory implementations of the CircleAI.Speech contracts, for
# hosts / tests that need working ASR / TTS / wake-word behaviour without a
# native, ONNX, or cloud engine wired.
#
# These are the Python analogue of the "keyword recognizer / template
# synthesizer" fakes the C# side gets from its cloud + null packs: every method
# is fully implemented and deterministic. No stubs.
#
#   KeywordSpeechRecognizer  — decodes the PCM buffer via an injected decoder
#                              (default: UTF-8 text carried in the byte buffer),
#                              then keyword-maps it to a canonical transcript.
#   TemplateSpeechSynthesizer— renders text to a reproducible PCM-16 tone buffer
#                              (sample count == characters * samples_per_char),
#                              so audio length is a pure function of the text.
#   KeywordWakeWordDetector  — fires the wake word for any fed transcript that
#                              contains the keyword. Exercises the Subscribe /
#                              StartAsync / StopAsync pub-sub with the wave-1
#                              concurrency rules: subscribers are registered
#                              synchronously, the queue is unbounded, and
#                              continuations are snapshotted OUTSIDE the lock
#                              before they are invoked.

from __future__ import annotations

import asyncio
import struct
import threading
from datetime import datetime, timedelta, timezone
from typing import Callable, Dict, List, Mapping, Optional

from .contracts import (
    IDisposable,
    ISpeechRecognizer,
    ISpeechSynthesizer,
    IWakeWordDetector,
    SynthesisResult,
    TranscribedSegment,
    TranscriptionResult,
    WakeWordEvent,
    WakeWordHandler,
)

_SHORT_MAX = 32767


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _decode_utf8(audio: bytes) -> str:
    """Default decoder for :class:`KeywordSpeechRecognizer`.

    Treats the buffer as UTF-8 text (the standard in-memory test double for an
    ASR engine). Bytes that are not valid UTF-8 are ignored so any PCM buffer
    still yields a deterministic — if empty — transcript.
    """
    return audio.decode("utf-8", errors="ignore").strip()


class KeywordSpeechRecognizer(ISpeechRecognizer):
    """Deterministic in-memory :class:`ISpeechRecognizer`.

    The audio buffer is decoded to text via an injected ``decoder`` (default:
    UTF-8). The decoded text is then normalised against an optional keyword map:
    if any key is found (case-insensitive substring) the mapped canonical
    transcript is returned; otherwise the decoded text passes through verbatim.

    Duration is derived from the byte length as if the buffer were real PCM-16
    (``samples / sample_rate``), so callers get a plausible, reproducible
    :class:`TranscriptionResult`.
    """

    def __init__(
        self,
        keyword_map: Optional[Mapping[str, str]] = None,
        decoder: Callable[[bytes], str] = _decode_utf8,
        language: Optional[str] = "en",
        confidence: float = 1.0,
    ) -> None:
        # Preserve insertion order; first matching key wins.
        self._keyword_map: Dict[str, str] = dict(keyword_map) if keyword_map else {}
        self._decoder = decoder
        self._language = language
        self._confidence = confidence

    @property
    def backend_id(self) -> str:
        return "keyword"

    async def transcribe_async(
        self,
        audio_pcm16_mono: bytes,
        sample_rate_hz: int,
        language_hint: Optional[str] = None,
        ct: object = None,
    ) -> TranscriptionResult:
        decoded = self._decoder(audio_pcm16_mono)
        text = decoded
        lowered = decoded.lower()
        for key, canonical in self._keyword_map.items():
            if key.lower() in lowered:
                text = canonical
                break

        samples = len(audio_pcm16_mono) // 2
        seconds = (samples / sample_rate_hz) if sample_rate_hz > 0 else 0.0
        duration = timedelta(seconds=seconds)
        language = language_hint or self._language

        if len(text) == 0:
            return TranscriptionResult(text="", language=language, segments=(), total_duration=duration)

        segment = TranscribedSegment(
            text=text,
            offset=timedelta(0),
            duration=duration,
            language=language,
            confidence=self._confidence,
        )
        return TranscriptionResult(
            text=text,
            language=language,
            segments=(segment,),
            total_duration=duration,
        )


class TemplateSpeechSynthesizer(ISpeechSynthesizer):
    """Deterministic in-memory :class:`ISpeechSynthesizer`.

    Renders ``text`` to a reproducible PCM-16 mono buffer. Every character emits
    ``samples_per_char`` samples of a fixed-amplitude square-ish tone whose value
    is derived from the character code, so identical text always yields identical
    bytes and the audio length is a pure function of the input.

    Empty text yields an empty buffer (matching the null synthesizer's shape but
    with this backend id).
    """

    def __init__(
        self,
        sample_rate_hz: int = 16_000,
        samples_per_char: int = 1_600,  # 100 ms per character @ 16 kHz
        amplitude: int = 8_192,
    ) -> None:
        if sample_rate_hz <= 0:
            raise ValueError("sample_rate_hz must be positive")
        if samples_per_char <= 0:
            raise ValueError("samples_per_char must be positive")
        self._sample_rate_hz = sample_rate_hz
        self._samples_per_char = samples_per_char
        self._amplitude = max(0, min(amplitude, _SHORT_MAX))

    @property
    def backend_id(self) -> str:
        return "template"

    async def synthesize_async(
        self,
        text: str,
        voice_id: Optional[str] = None,
        language_hint: Optional[str] = None,
        ct: object = None,
    ) -> SynthesisResult:
        text = text or ""
        if len(text) == 0:
            return SynthesisResult(
                audio_pcm16_mono=b"",
                sample_rate_hz=self._sample_rate_hz,
                duration=timedelta(0),
            )

        total_samples = len(text) * self._samples_per_char
        out = bytearray(total_samples * 2)
        pos = 0
        for ch in text:
            # Alternating +/- amplitude scaled by the character; deterministic.
            magnitude = self._amplitude if (ord(ch) % 2 == 0) else -self._amplitude
            for _ in range(self._samples_per_char):
                struct.pack_into("<h", out, pos, magnitude)
                pos += 2

        seconds = total_samples / self._sample_rate_hz
        return SynthesisResult(
            audio_pcm16_mono=bytes(out),
            sample_rate_hz=self._sample_rate_hz,
            duration=timedelta(seconds=seconds),
        )


class _HandlerSubscription(IDisposable):
    """Disposable that removes one handler from the detector's subscriber list."""

    __slots__ = ("_detector", "_handler", "_disposed")

    def __init__(self, detector: "KeywordWakeWordDetector", handler: WakeWordHandler) -> None:
        self._detector = detector
        self._handler = handler
        self._disposed = False

    def dispose(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        self._detector._unsubscribe(self._handler)


class KeywordWakeWordDetector(IWakeWordDetector):
    """Deterministic in-memory :class:`IWakeWordDetector`.

    A host feeds transcripts via :meth:`feed_async`; whenever a fed transcript
    contains the configured ``keyword`` (case-insensitive) while the detector is
    listening, every subscriber is invoked with a :class:`WakeWordEvent`.

    Concurrency (wave-1 rules):
      * Subscription is synchronous — :meth:`subscribe` mutates the subscriber
        list under the lock and returns before any event can be dispatched, so a
        wake word fired immediately after :meth:`start_async` cannot race the
        subscription.
      * Handlers are snapshotted OUTSIDE the lock before being awaited, so a
        handler that (dis)subscribes or disposes the detector cannot self-deadlock
        on the non-reentrant lock.
      * The dispatch path never blocks a producer: handler invocation is awaited
        sequentially but the lock is already released.
    """

    def __init__(self, keyword: str = "hey b", confidence: float = 1.0) -> None:
        keyword = (keyword or "").strip()
        if not keyword:
            raise ValueError("keyword must be a non-empty string")
        self._keyword = keyword
        self._confidence = confidence
        self._lock = threading.Lock()
        self._handlers: List[WakeWordHandler] = []
        self._listening = False
        self._disposed = False

    @property
    def backend_id(self) -> str:
        return "keyword"

    @property
    def keyword(self) -> str:
        return self._keyword

    @property
    def is_listening(self) -> bool:
        with self._lock:
            return self._listening

    def subscribe(self, handler: WakeWordHandler) -> IDisposable:
        if handler is None:
            raise ValueError("handler required")
        # Register synchronously, before any dispatch can observe the list.
        with self._lock:
            self._handlers.append(handler)
        return _HandlerSubscription(self, handler)

    def _unsubscribe(self, handler: WakeWordHandler) -> None:
        with self._lock:
            try:
                self._handlers.remove(handler)
            except ValueError:
                pass

    async def start_async(self, ct: object = None) -> None:
        if self._disposed:
            raise RuntimeError("KeywordWakeWordDetector is disposed")
        with self._lock:
            self._listening = True

    async def stop_async(self, ct: object = None) -> None:
        if self._disposed:
            raise RuntimeError("KeywordWakeWordDetector is disposed")
        with self._lock:
            self._listening = False

    async def feed_async(self, transcript: str) -> bool:
        """Feed a transcript. Fires the wake word (and returns True) when the
        detector is listening and the transcript contains the keyword."""
        # Snapshot state + subscribers under the lock, then release it BEFORE
        # invoking handlers (a handler may dispose/unsubscribe the detector).
        with self._lock:
            if self._disposed or not self._listening:
                return False
            text = transcript or ""
            if self._keyword.lower() not in text.lower():
                return False
            handlers = list(self._handlers)

        event = WakeWordEvent(
            keyword=self._keyword,
            confidence=self._confidence,
            detected_at_utc=_utc_now(),
        )
        for handler in handlers:
            await handler(event)
        return True

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        with self._lock:
            self._disposed = True
            self._listening = False
            self._handlers.clear()


__all__ = [
    "KeywordSpeechRecognizer",
    "TemplateSpeechSynthesizer",
    "KeywordWakeWordDetector",
]
