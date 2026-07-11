# first_message_preamble.py
#
# Port of CircleAI.Telephony FirstMessagePreamble.cs (C# — the EXACT spec).
#
# (3.3.0) Speak a greeting the moment a call connects, before the LLM has a
# chance to "warm up" — eliminates the awkward 1-2 second silence callers hate.
# Supports variable substitution (time of day, business name, agent identity)
# and per-call overrides.
#
# C# Task modelReady + Task.Delay race via Task.WhenAny -> asyncio: modelReady is
# wrapped in an asyncio.Task (ensure_future) so we can poll .done()/exception();
# the delay is asyncio.sleep wrapped as a task. asyncio.wait(FIRST_COMPLETED) is
# the WhenAny. "model won within the window and completed successfully" mirrors
# ``winner == modelReady && modelReady.IsCompletedSuccessfully``. The race-window
# sleep task is cancelled on exit so it doesn't linger.

from __future__ import annotations

import asyncio
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import timedelta
from typing import Awaitable, Optional

from .contracts import ICallSession
from .primitives import AudioFrame, CallMediaFormat
from .prompt_variable_resolver import PromptVariableResolver
from .warm_transfer_orchestrator import BriefingSynthesiser


@dataclass(frozen=True, slots=True)
class FirstMessagePreambleOptions:
    """(3.3.0) Configuration for the first-message preamble.

    ``template``: template with ``{{var}}`` placeholders.
    ``max_latency``: if the LLM responds before this elapses, skip the preamble
    (default 250 ms).
    """

    template: str
    max_latency: Optional[timedelta] = None

    @property
    def max_latency_or_default(self) -> timedelta:
        return self.max_latency if self.max_latency is not None else timedelta(milliseconds=250)


class IFirstMessagePreamble(ABC):
    """(3.3.0) Speaks a greeting at call-start."""

    @abstractmethod
    async def speak_async(
        self,
        session: ICallSession,
        tts: BriefingSynthesiser,
        model_ready: Awaitable[object],
        *,
        ct: Optional[object] = None,
    ) -> None:
        """(3.3.0) Speak the preamble. ``model_ready`` is awaited concurrently — if
        it completes before ``max_latency`` the preamble is skipped (the model has
        its own greeting)."""


class DefaultFirstMessagePreamble(IFirstMessagePreamble):
    """(3.3.0) Default driver that resolves the template via a
    :class:`PromptVariableResolver`."""

    def __init__(
        self,
        options: FirstMessagePreambleOptions,
        resolver: Optional[PromptVariableResolver] = None,
    ) -> None:
        if options is None:
            raise ValueError("options must not be None")
        self._options = options
        self._resolver = resolver if resolver is not None else PromptVariableResolver()

    async def speak_async(
        self,
        session: ICallSession,
        tts: BriefingSynthesiser,
        model_ready: Awaitable[object],
        *,
        ct: Optional[object] = None,
    ) -> None:
        if session is None:
            raise ValueError("session must not be None")
        if tts is None:
            raise ValueError("tts must not be None")
        if model_ready is None:
            raise ValueError("model_ready must not be None")

        # Race the model. If it wins within the latency window, skip the preamble.
        model_task = asyncio.ensure_future(model_ready)
        race_window = asyncio.ensure_future(
            asyncio.sleep(self._options.max_latency_or_default.total_seconds())
        )
        try:
            done, _pending = await asyncio.wait(
                {model_task, race_window}, return_when=asyncio.FIRST_COMPLETED
            )
        finally:
            if not race_window.done():
                race_window.cancel()

        # winner == modelReady && modelReady.IsCompletedSuccessfully
        if model_task in done and model_task.exception() is None:
            return

        rendered = await self._resolver.render_async(self._options.template, ct=ct)
        if not rendered or rendered.isspace():
            return

        audio = await tts(rendered, ct)
        if not audio:
            return

        await session.send_audio_async(
            AudioFrame(audio, CallMediaFormat.PCM24000, timedelta(0)), ct=ct
        )
