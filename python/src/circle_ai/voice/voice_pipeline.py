# voice/voice_pipeline.py
#
# Port of CircleAI.Voice/VoicePipeline.cs (C# — the EXACT spec).
#
# Convenience composition of IWakeWordDetector, IAudioCapture, IVoiceTranscriber,
# and optionally IVoiceActivityDetector and ITtsEngine. On wake-word detection the
# pipeline starts capturing audio, optionally filters it through VAD, feeds the
# speech chunks to the transcriber, and raises Transcribed with the final
# TranscriptionResult.
#
# Concurrency (wave-1 rules): the wake-word handler is registered SYNCHRONOUSLY at
# construction (before StartAsync), so an activation cannot race the subscription.
# The activation task and its cancel-event are swapped under a lock, and the lock
# is released BEFORE the previous activation is cancelled/awaited so a cancelling
# path cannot self-deadlock. Event subscribers are snapshotted outside the lock.

from __future__ import annotations

import asyncio
import logging
from typing import AsyncIterator, List, Optional

from .audio_capture import IAudioCapture, NullAudioCapture, TranscribedEventArgs
from .contracts import (
    ITtsEngine,
    IVoiceActivityDetector,
    IVoiceTranscriber,
    IWakeWordDetector,
    PartialTranscription,
    TranscriptionResult,
    WakeWordDetectedEventArgs,
)

_LOG = logging.getLogger("circle_ai.voice.voice_pipeline")

# Handler signatures (the analogue of the C# multicast delegates).
TranscribedHandler = "callable"  # (sender, TranscribedEventArgs) -> None
ActivationFailedHandler = "callable"  # (sender, Exception) -> None


async def _to_final_async(
    source: AsyncIterator[PartialTranscription],
) -> Optional[TranscriptionResult]:
    """Drain the partial-transcription stream and return the final result.
    Returns None if the stream produces no items. Mirrors the C#
    ``ToFinalAsync`` extension."""
    last: Optional[PartialTranscription] = None
    async for partial in source:
        last = partial
        if partial.is_final:
            break
    if last is None:
        return None
    # We do not know the language at this layer; callers can use the single-shot
    # transcribe_async overload for richer metadata.
    return TranscriptionResult(last.text, last.confidence, "und")


class VoicePipeline:
    """Composes wake-word detection, audio capture, VAD, transcription, and TTS.

    Mirrors ``CircleAI.Voice.VoicePipeline`` (``IAsyncDisposable``). The pipeline
    does not own the wake-word lifecycle: callers invoke :meth:`start_async` /
    :meth:`stop_async`. Disposing disposes all collaborators.

    Events are modelled as subscriber lists:
      * ``on_transcribed``      — list of ``(sender, TranscribedEventArgs) -> None``
      * ``on_activation_failed``— list of ``(sender, Exception) -> None``
    """

    __slots__ = (
        "_wake",
        "_transcriber",
        "_capture",
        "_vad",
        "_tts",
        "_lock",
        "_activation_task",
        "_activation_cancel",
        "_disposed",
        "on_transcribed",
        "on_activation_failed",
    )

    def __init__(
        self,
        wake: IWakeWordDetector,
        transcriber: IVoiceTranscriber,
        capture: Optional[IAudioCapture] = None,
        vad: Optional[IVoiceActivityDetector] = None,
        tts: Optional[ITtsEngine] = None,
    ) -> None:
        if wake is None:
            raise ValueError("wake")
        if transcriber is None:
            raise ValueError("transcriber")

        self._wake = wake
        self._transcriber = transcriber
        self._capture = capture if capture is not None else NullAudioCapture()
        self._vad = vad
        self._tts = tts
        self._lock = asyncio.Lock()
        self._activation_task: Optional[asyncio.Task] = None
        self._activation_cancel: Optional[asyncio.Event] = None
        self._disposed = False
        self.on_transcribed: List = []
        self.on_activation_failed: List = []
        # Register the handler synchronously (before any StartAsync).
        self._wake.add_wake_word_detected_handler(self._on_wake_word_detected)

    # ── read-only collaborators (mirror the C# properties) ──────────────────────

    @property
    def wake_detector(self) -> IWakeWordDetector:
        return self._wake

    @property
    def transcriber(self) -> IVoiceTranscriber:
        return self._transcriber

    @property
    def audio_capture(self) -> IAudioCapture:
        return self._capture

    @property
    def tts_engine(self) -> Optional[ITtsEngine]:
        return self._tts

    @property
    def voice_activity_detector(self) -> Optional[IVoiceActivityDetector]:
        return self._vad

    # ── lifecycle ───────────────────────────────────────────────────────────────

    async def start_async(self, ct: object = None) -> None:
        if self._disposed:
            raise RuntimeError("VoicePipeline is disposed")
        await self._wake.start_async(ct)

    async def stop_async(self, ct: object = None) -> None:
        if self._disposed:
            raise RuntimeError("VoicePipeline is disposed")
        await self._cancel_activation()
        await self._wake.stop_async(ct)

    def _on_wake_word_detected(self, sender: object, e: WakeWordDetectedEventArgs) -> None:
        if self._disposed:
            return
        # Schedule a fresh activation on the running loop. The old one is cancelled
        # inside the coroutine (off this synchronous callback).
        try:
            loop = asyncio.get_running_loop()
        except RuntimeError:
            _LOG.debug("VoicePipeline: wake fired with no running loop; ignoring")
            return
        loop.create_task(self._restart_activation())

    async def _restart_activation(self) -> None:
        # Cancel any previous activation still running, then start a new one.
        await self._cancel_activation()
        cancel = asyncio.Event()
        async with self._lock:
            self._activation_cancel = cancel
            task = asyncio.create_task(self._run_activation(cancel))
            self._activation_task = task

    async def _run_activation(self, cancel: asyncio.Event) -> None:
        try:
            # When VAD is configured, pipe raw audio through it and only pass
            # speech segments to the transcriber. When absent, forward directly.
            if self._vad is None:
                audio_input = self._capture.capture_async()
            else:
                audio_input = self._extract_speech_segments(self._vad, self._capture.capture_async())

            audio_input = self._until_cancelled(audio_input, cancel)

            result = await _to_final_async(self._transcriber.stream_transcribe_async(audio_input))

            if cancel.is_set():
                return

            if result is not None:
                args = TranscribedEventArgs(result=result)
                for handler in list(self.on_transcribed):
                    handler(self, args)
            else:
                # No final result — silence, noise, or premature cancel. Normal.
                _LOG.info(
                    "VoicePipeline: activation produced no final transcription (silent or empty audio)."
                )
        except asyncio.CancelledError:
            # Activation was cancelled (stop requested or new wake event). Swallow.
            return
        except Exception as ex:  # noqa: BLE001
            for handler in list(self.on_activation_failed):
                handler(self, ex)

    @staticmethod
    async def _extract_speech_segments(
        vad: IVoiceActivityDetector, raw_audio: AsyncIterator[bytes]
    ) -> AsyncIterator[bytes]:
        """Yield only the audio bytes from segments where ``is_speech`` is True.
        Mirrors the C# ``ExtractSpeechSegmentsAsync``."""
        async for segment in vad.detect_async(raw_audio):
            if segment.is_speech:
                yield segment.audio

    @staticmethod
    async def _until_cancelled(
        source: AsyncIterator[bytes], cancel: asyncio.Event
    ) -> AsyncIterator[bytes]:
        """Forward chunks until the activation cancel-event is set. This is the
        Python analogue of feeding the C# activation CancellationToken into the
        capture/VAD stream."""
        async for chunk in source:
            if cancel.is_set():
                return
            yield chunk

    async def _cancel_activation(self) -> None:
        async with self._lock:
            to_cancel = self._activation_task
            cancel_event = self._activation_cancel
            self._activation_task = None
            self._activation_cancel = None

        # Signal + await OUTSIDE the lock so the activation's own paths (which may
        # need the lock) cannot self-deadlock.
        if cancel_event is not None:
            cancel_event.set()
        if to_cancel is not None and not to_cancel.done():
            to_cancel.cancel()
            try:
                await to_cancel
            except (asyncio.CancelledError, Exception):  # noqa: BLE001
                pass

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        self._disposed = True

        self._wake.remove_wake_word_detected_handler(self._on_wake_word_detected)
        await self._cancel_activation()

        await self._wake.dispose_async()
        await self._transcriber.dispose_async()
        await self._capture.dispose_async()

    async def __aenter__(self) -> "VoicePipeline":
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self.dispose_async()


__all__ = ["VoicePipeline"]
