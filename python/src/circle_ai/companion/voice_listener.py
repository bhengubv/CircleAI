# companion/voice_listener.py
#
# Bridges a voice pipeline with a Companion session. Ported from
# CircleAI.Companion (IVoiceListener.cs + VoiceCompanionListener.cs) — the C#
# reference.
#
# When the wake word fires and the user speaks, the voice pipeline raises a
# transcription; VoiceCompanionListener forwards the text to the session and
# raises ``response_ready`` with the reply. Platform hosts subscribe to the two
# events and drive TTS playback / UI updates.
#
# The underlying ``VoicePipeline`` lives in CircleAI.Voice (native audio) — out of
# this module's scope — so it is modelled here as an injected ``IVoicePipeline``
# seam: an object exposing ``start_async`` / ``stop_async`` / ``dispose_async``
# and a ``transcribed`` event that invokes subscribers with a
# :class:`TranscribedEventArgs`. The Companion call is dispatched off the pipeline
# thread (as a fire-and-forget task) so wake-word detection is never blocked;
# failures are logged, not raised — consistent with the C# semantics.

from __future__ import annotations

import asyncio
import logging
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Callable, List, Optional, Protocol, runtime_checkable

_LOG = logging.getLogger("circle_ai.companion.voice_listener")


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# ── event argument records ────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class UtteranceDetectedEventArgs:
    """Raised when a user utterance has been transcribed and forwarded.

    Mirrors ``CircleAI.Companion.UtteranceDetectedEventArgs``.
    """

    text: str
    confidence: float = 0.0
    detected_at: datetime = field(default_factory=_utc_now)


@dataclass(frozen=True, slots=True)
class ResponseReadyEventArgs:
    """Raised when the Companion has produced a reply to a voice utterance.

    Mirrors ``CircleAI.Companion.ResponseReadyEventArgs``.
    """

    text: str
    original_utterance: str
    completed_at: datetime = field(default_factory=_utc_now)


# ── the injected voice-pipeline seam ──────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class TranscriptionResult:
    """A transcription result — mirrors ``CircleAI.Voice`` transcription output."""

    text: str
    confidence: float = 0.0


@dataclass(frozen=True, slots=True)
class TranscribedEventArgs:
    """Pipeline transcription event — mirrors ``CircleAI.Voice.TranscribedEventArgs``."""

    result: TranscriptionResult
    completed_at: datetime = field(default_factory=_utc_now)


TranscribedHandler = Callable[[object, TranscribedEventArgs], None]


@runtime_checkable
class IVoicePipeline(Protocol):
    """The native voice-pipeline seam consumed by :class:`VoiceCompanionListener`.

    Stands in for ``CircleAI.Voice.VoicePipeline``. The listener subscribes to
    ``transcribed`` (via :meth:`add_transcribed_handler`) and owns the pipeline's
    lifetime.
    """

    def add_transcribed_handler(self, handler: TranscribedHandler) -> None: ...

    def remove_transcribed_handler(self, handler: TranscribedHandler) -> None: ...

    async def start_async(self, *, ct: Optional[object] = None) -> None: ...

    async def stop_async(self, *, ct: Optional[object] = None) -> None: ...

    async def dispose_async(self) -> None: ...


# ── the Companion session seam ────────────────────────────────────────────────


@runtime_checkable
class _SessionLike(Protocol):
    async def send_async(self, message: str, *, ct: Optional[object] = None) -> str: ...


# ── the listener contract ─────────────────────────────────────────────────────


class IVoiceListener(ABC):
    """Bridges a voice pipeline with a Companion session.

    Mirrors ``CircleAI.Companion.IVoiceListener`` (``IAsyncDisposable``). Events
    are modelled as handler lists: subscribe via ``on_utterance_detected`` /
    ``on_response_ready`` (each a list of callbacks ``(sender, args) -> None``).
    """

    @abstractmethod
    async def start_async(self, *, ct: Optional[object] = None) -> None:
        """Begin listening for the wake word (starts the underlying pipeline)."""
        ...

    @abstractmethod
    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        """Stop listening and cancel any in-flight activation."""
        ...

    @abstractmethod
    async def dispose_async(self) -> None:
        """Dispose the listener, its pipeline, and its session."""
        ...


class VoiceCompanionListener(IVoiceListener):
    """Concrete :class:`IVoiceListener` wiring a pipeline to a Companion session.

    Mirrors ``CircleAI.Companion.VoiceCompanionListener``. Owns the lifetime of
    both the pipeline and the session — :meth:`dispose_async` disposes both.
    """

    __slots__ = (
        "_pipeline",
        "_session",
        "_disposed",
        "on_utterance_detected",
        "on_response_ready",
        "_pending",
    )

    def __init__(self, pipeline: IVoicePipeline, session: _SessionLike) -> None:
        if pipeline is None:
            raise ValueError("pipeline required")
        if session is None:
            raise ValueError("session required")
        self._pipeline = pipeline
        self._session = session
        self._disposed = False
        # Event subscriber lists (the analogue of C# multicast delegates).
        self.on_utterance_detected: List[Callable[[object, UtteranceDetectedEventArgs], None]] = []
        self.on_response_ready: List[Callable[[object, ResponseReadyEventArgs], None]] = []
        self._pending: List[asyncio.Task] = []
        self._pipeline.add_transcribed_handler(self._on_transcribed)

    async def start_async(self, *, ct: Optional[object] = None) -> None:
        if self._disposed:
            raise RuntimeError("VoiceCompanionListener is disposed")
        await self._pipeline.start_async(ct=ct)

    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        if self._disposed:
            raise RuntimeError("VoiceCompanionListener is disposed")
        await self._pipeline.stop_async(ct=ct)

    def _on_transcribed(self, sender: object, e: TranscribedEventArgs) -> None:
        if self._disposed:
            return
        text = e.result.text
        confidence = e.result.confidence
        detected_at = e.completed_at

        # Notify subscribers that an utterance was received.
        args = UtteranceDetectedEventArgs(
            text=text, confidence=confidence, detected_at=detected_at
        )
        for handler in list(self.on_utterance_detected):
            handler(self, args)

        # Forward to the Companion off the pipeline thread — never block it.
        self._pending.append(asyncio.ensure_future(self._forward_async(text)))

    async def _forward_async(self, text: str) -> None:
        try:
            reply = await self._session.send_async(text)
            if not self._disposed:
                ready = ResponseReadyEventArgs(
                    text=reply, original_utterance=text, completed_at=_utc_now()
                )
                for handler in list(self.on_response_ready):
                    handler(self, ready)
        except Exception as ex:  # noqa: BLE001 — trace + swallow, matching C#
            _LOG.error(
                "VoiceCompanionListener: session failed for utterance '%s': %s", text, ex
            )

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        self._pipeline.remove_transcribed_handler(self._on_transcribed)
        # Let any in-flight forwards settle before tearing down.
        pending = [t for t in self._pending if not t.done()]
        if pending:
            await asyncio.gather(*pending, return_exceptions=True)
        await self._pipeline.dispose_async()
        dispose = getattr(self._session, "dispose_async", None)
        if callable(dispose):
            await dispose()

    async def __aenter__(self) -> "VoiceCompanionListener":
        return self

    async def __aexit__(self, *exc) -> None:
        await self.dispose_async()


__all__ = [
    "UtteranceDetectedEventArgs",
    "ResponseReadyEventArgs",
    "TranscriptionResult",
    "TranscribedEventArgs",
    "IVoicePipeline",
    "IVoiceListener",
    "VoiceCompanionListener",
]
