# voice/energy_wake_word_detector.py
#
# Port of CircleAI.Voice/EnergyWakeWordDetector.cs (C# — the EXACT spec).
#
# IWakeWordDetector that combines energy-based VAD with speech-to-text
# transcription to detect a configurable wake-word phrase. Audio is captured
# continuously via IAudioCapture, short speech segments are transcribed, and when
# the transcription contains the wake word the WakeWordDetected event is fired.
#
# The C# background loop runs on the thread pool (Task.Run) guarded by a lock over
# _cts / IsListening / _listenTask. Here it is an asyncio task guarded by an
# asyncio.Lock. Concurrency (wave-1 rules): event handlers are snapshotted OUTSIDE
# the lock before invocation so a handler that mutates subscriptions or stops the
# detector cannot deadlock; the transcriber/VAD are subscribed to (iterated)
# synchronously within the loop before any await that could drop a segment.

from __future__ import annotations

import asyncio
import logging
from typing import List, Optional

from .audio_capture import IAudioCapture
from .contracts import (
    IVoiceTranscriber,
    IWakeWordDetector,
    TranscriptionResult,
    WakeWordDetectedEventArgs,
    WakeWordHandler,
)
from .energy_vad_detector import EnergyVadDetector

_LOG = logging.getLogger("circle_ai.voice.energy_wake_word_detector")


class EnergyWakeWordDetector(IWakeWordDetector):
    """Energy-based wake-word detector reusing the transcriber infrastructure.

    Mirrors ``CircleAI.Voice.EnergyWakeWordDetector``.

    :param capture: Audio capture source providing PCM 16-bit, 16 kHz mono audio.
    :param transcriber: Voice transcriber used to convert speech segments to text.
    :param wake_word: Phrase to listen for. Matching is case-insensitive substring
        so surrounding words do not prevent detection. Default "hey b".
    :param energy_threshold: RMS energy threshold for VAD.
    """

    __slots__ = (
        "_capture",
        "_transcriber",
        "_vad",
        "_wake_word",
        "_lock",
        "_task",
        "_stop_event",
        "_is_listening",
        "_disposed",
        "_handlers",
    )

    def __init__(
        self,
        capture: IAudioCapture,
        transcriber: IVoiceTranscriber,
        wake_word: str = "hey b",
        energy_threshold: float = 0.02,
    ) -> None:
        if capture is None:
            raise ValueError("capture")
        if transcriber is None:
            raise ValueError("transcriber")
        if wake_word is None or not wake_word.strip():
            raise ValueError("wake_word")

        self._capture = capture
        self._transcriber = transcriber
        self._wake_word = wake_word.strip()
        self._vad = EnergyVadDetector(energy_threshold, silence_frames=10, frame_size_bytes=640)
        self._lock = asyncio.Lock()
        self._task: Optional[asyncio.Task] = None
        self._stop_event: Optional[asyncio.Event] = None
        self._is_listening = False
        self._disposed = False
        self._handlers: List[WakeWordHandler] = []

    @property
    def wake_word(self) -> str:
        return self._wake_word

    @property
    def is_listening(self) -> bool:
        return self._is_listening

    def add_wake_word_detected_handler(self, handler: WakeWordHandler) -> None:
        # Synchronous registration — a wake fired right after start cannot race it.
        self._handlers.append(handler)

    def remove_wake_word_detected_handler(self, handler: WakeWordHandler) -> None:
        try:
            self._handlers.remove(handler)
        except ValueError:
            pass

    async def start_async(self, ct: object = None) -> None:
        if self._disposed:
            raise RuntimeError("EnergyWakeWordDetector is disposed")
        async with self._lock:
            if self._is_listening:
                return
            self._is_listening = True
            self._stop_event = asyncio.Event()
            self._task = asyncio.create_task(self._listen_loop(self._stop_event))

    async def stop_async(self, ct: object = None) -> None:
        if self._disposed:
            raise RuntimeError("EnergyWakeWordDetector is disposed")
        async with self._lock:
            if not self._is_listening:
                return
            self._is_listening = False
            stop_event = self._stop_event
            task = self._task

        if stop_event is not None:
            stop_event.set()
        if task is not None:
            try:
                await task
            except asyncio.CancelledError:
                pass

        async with self._lock:
            self._task = None
            self._stop_event = None

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        try:
            await self.stop_async()
        except RuntimeError:
            # Disposed race — ignore during tear-down.
            pass

    # ── Private helpers ─────────────────────────────────────────────────────────

    async def _listen_loop(self, stop_event: asyncio.Event) -> None:
        try:
            audio_stream = self._capture.capture_async()
            async for segment in self._vad.detect_async(audio_stream):
                if stop_event.is_set():
                    break
                if not segment.is_speech or len(segment.audio) == 0:
                    continue

                try:
                    result: TranscriptionResult = await self._transcriber.transcribe_async(segment.audio)
                except Exception:  # noqa: BLE001 — matching C# catch-all skip
                    # Transcription failed for this segment — skip and keep listening.
                    continue

                if not result.text or not result.text.strip():
                    continue

                if self._wake_word.lower() in result.text.lower():
                    self._fire(result.confidence)
        except asyncio.CancelledError:
            raise
        except Exception as ex:  # noqa: BLE001
            _LOG.debug("EnergyWakeWordDetector loop error: %s", ex)
        finally:
            self._is_listening = False

    def _fire(self, confidence: float) -> None:
        args = WakeWordDetectedEventArgs(wake_word=self._wake_word, confidence=confidence)
        # Snapshot subscribers before invoking; a handler may mutate the list.
        for handler in list(self._handlers):
            handler(self, args)


__all__ = ["EnergyWakeWordDetector"]
