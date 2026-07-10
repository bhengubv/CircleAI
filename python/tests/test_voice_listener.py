"""test_voice_listener.py

Verifies VoiceCompanionListener ported from CircleAI.Companion
(VoiceCompanionListener.cs): a pipeline transcription raises utterance-detected,
is forwarded to the Companion session, and the reply is surfaced via
response-ready; session failures are swallowed; disposal tears down pipeline and
session and detaches the handler.
"""
from __future__ import annotations

import asyncio
from typing import List

import pytest

from circle_ai.companion.voice_listener import (
    IVoiceListener,
    ResponseReadyEventArgs,
    TranscribedEventArgs,
    TranscriptionResult,
    UtteranceDetectedEventArgs,
    VoiceCompanionListener,
)


class FakePipeline:
    """A voice-pipeline seam that lets tests fire transcriptions directly."""

    def __init__(self) -> None:
        self._handlers = []
        self.started = False
        self.stopped = False
        self.disposed = False

    def add_transcribed_handler(self, handler) -> None:
        self._handlers.append(handler)

    def remove_transcribed_handler(self, handler) -> None:
        if handler in self._handlers:
            self._handlers.remove(handler)

    async def start_async(self, *, ct=None) -> None:
        self.started = True

    async def stop_async(self, *, ct=None) -> None:
        self.stopped = True

    async def dispose_async(self) -> None:
        self.disposed = True

    def fire(self, text: str, confidence: float = 0.9) -> None:
        args = TranscribedEventArgs(TranscriptionResult(text, confidence))
        for h in list(self._handlers):
            h(self, args)


class FakeSession:
    def __init__(self, reply: str) -> None:
        self._reply = reply
        self.received: List[str] = []
        self.disposed = False

    async def send_async(self, message: str, *, ct=None) -> str:
        self.received.append(message)
        return self._reply

    async def dispose_async(self) -> None:
        self.disposed = True


class FailingSession:
    def __init__(self) -> None:
        self.disposed = False

    async def send_async(self, message: str, *, ct=None) -> str:
        raise RuntimeError("session down")

    async def dispose_async(self) -> None:
        self.disposed = True


def test_implements_interface() -> None:
    listener = VoiceCompanionListener(FakePipeline(), FakeSession("hi"))
    assert isinstance(listener, IVoiceListener)


async def test_start_stop_drive_pipeline() -> None:
    pipe = FakePipeline()
    listener = VoiceCompanionListener(pipe, FakeSession("hi"))
    await listener.start_async()
    assert pipe.started is True
    await listener.stop_async()
    assert pipe.stopped is True


async def test_transcription_flows_to_session_and_raises_events() -> None:
    pipe = FakePipeline()
    session = FakeSession("The answer is 42")
    listener = VoiceCompanionListener(pipe, session)

    utterances: List[UtteranceDetectedEventArgs] = []
    responses: List[ResponseReadyEventArgs] = []
    listener.on_utterance_detected.append(lambda s, a: utterances.append(a))
    listener.on_response_ready.append(lambda s, a: responses.append(a))

    pipe.fire("what is six times seven?", confidence=0.8)
    # Let the fire-and-forget forward task run.
    await asyncio.gather(*listener._pending)

    assert [u.text for u in utterances] == ["what is six times seven?"]
    assert utterances[0].confidence == pytest.approx(0.8)
    assert session.received == ["what is six times seven?"]
    assert len(responses) == 1
    assert responses[0].text == "The answer is 42"
    assert responses[0].original_utterance == "what is six times seven?"


async def test_session_failure_is_swallowed_no_response_event() -> None:
    pipe = FakePipeline()
    listener = VoiceCompanionListener(pipe, FailingSession())
    responses = []
    listener.on_response_ready.append(lambda s, a: responses.append(a))

    pipe.fire("boom")
    await asyncio.gather(*listener._pending, return_exceptions=True)
    # No response raised, and no exception propagated to the caller.
    assert responses == []


async def test_dispose_tears_down_pipeline_and_session() -> None:
    pipe = FakePipeline()
    session = FakeSession("hi")
    listener = VoiceCompanionListener(pipe, session)
    await listener.dispose_async()
    assert pipe.disposed is True
    assert session.disposed is True
    # Handler detached: firing after dispose does nothing.
    responses = []
    listener.on_response_ready.append(lambda s, a: responses.append(a))
    pipe.fire("late")
    assert responses == []


async def test_start_after_dispose_raises() -> None:
    listener = VoiceCompanionListener(FakePipeline(), FakeSession("hi"))
    await listener.dispose_async()
    with pytest.raises(RuntimeError):
        await listener.start_async()


def test_rejects_none_deps() -> None:
    with pytest.raises(ValueError):
        VoiceCompanionListener(None, FakeSession("x"))  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        VoiceCompanionListener(FakePipeline(), None)  # type: ignore[arg-type]
