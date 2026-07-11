# speculative_generator.py
#
# Port of CircleAI.Telephony SpeculativeGenerator.cs (C# — the EXACT spec).
#
# (3.3.0) Speculative generation: while the user is still speaking, start
# generating a draft response from the partial transcript. If the user keeps
# talking we discard and restart with the new partial; when they finish we use
# whichever speculative branch is closest. Cuts time-to-first-token by ~300-600 ms.
#
# C# Task<string> ResponseTask + CancellationTokenSource per branch -> the
# generator is kicked off as an asyncio.Task (so it can be awaited later and
# cancelled when superseded). Cancelling that task is the Python analogue of
# cancelling the branch's CTS. A cancelled/awaited-superseded branch raises
# CancelledError, which CommitAsync swallows exactly like the C# catches
# OperationCanceledException. All branch state is lock-guarded (C# monitor).
# StartsWith/Equals(OrdinalIgnoreCase) -> casefold() comparisons.

from __future__ import annotations

import asyncio
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Awaitable, Callable, Optional

# (3.3.0) Function that drives a response generation given a partial transcript.
# C# ``delegate Task<string> ResponseGenerator(string transcript,
# CancellationToken ct)``.
ResponseGenerator = Callable[[str, Optional[object]], Awaitable[str]]


@dataclass(frozen=True, slots=True)
class SpeculativeBranch:
    """(3.3.0) One in-flight speculative branch.

    ``response_task`` is the started generation (C# ``Task<string>``).
    """

    partial_transcript: str
    response_task: "asyncio.Task[str]"
    started_at: datetime


class ISpeculativeGenerator(ABC):
    """(3.3.0) Manages speculative-generation branches."""

    @property
    @abstractmethod
    def active_branch(self) -> Optional[SpeculativeBranch]:
        """The branch currently considered most likely to commit."""

    @abstractmethod
    def speculate(self, partial_transcript: str, generator: ResponseGenerator) -> None:
        """Start (or restart) the speculative branch using ``partial_transcript``."""

    @abstractmethod
    async def commit_async(
        self, final_transcript: str, generator: ResponseGenerator, *, ct: Optional[object] = None
    ) -> str:
        """Commit to a final transcript and return the matching response."""

    @abstractmethod
    def abort(self) -> None:
        """Abort any active speculation."""


def _starts_with(text: str, prefix: str) -> bool:
    return text.casefold().startswith(prefix.casefold())


class DefaultSpeculativeGenerator(ISpeculativeGenerator):
    """(3.3.0) Default driver. Cancels older branches when the partial diverges."""

    def __init__(
        self,
        clock: Optional[Callable[[], datetime]] = None,
        min_partial_length: int = 8,
    ) -> None:
        self._gate = threading.Lock()
        self._active: Optional[SpeculativeBranch] = None
        self._clock = clock if clock is not None else (lambda: datetime.now(timezone.utc))
        self._min_partial_length = min_partial_length

    @property
    def active_branch(self) -> Optional[SpeculativeBranch]:
        with self._gate:
            return self._active

    def speculate(self, partial_transcript: str, generator: ResponseGenerator) -> None:
        if generator is None:
            raise ValueError("generator must not be None")
        if not partial_transcript or partial_transcript.isspace():
            return
        if len(partial_transcript) < self._min_partial_length:
            return

        to_cancel: Optional["asyncio.Task[str]"] = None
        with self._gate:
            # If the new partial is just an extension of the active one, keep it.
            if self._active is not None and _starts_with(partial_transcript, self._active.partial_transcript):
                return
            to_cancel = self._active.response_task if self._active is not None else None
            task = asyncio.ensure_future(generator(partial_transcript, None))
            self._active = SpeculativeBranch(partial_transcript, task, self._clock())
        if to_cancel is not None:
            to_cancel.cancel()

    async def commit_async(
        self, final_transcript: str, generator: ResponseGenerator, *, ct: Optional[object] = None
    ) -> str:
        if generator is None:
            raise ValueError("generator must not be None")
        if not final_transcript or final_transcript.isspace():
            return ""

        with self._gate:
            active = self._active

        if active is not None and _starts_with(final_transcript, active.partial_transcript):
            try:
                draft = await active.response_task
                if final_transcript.casefold() == active.partial_transcript.casefold():
                    return draft
                # Final extended the partial — finalize via a fresh generation.
                # For our contract: re-run with full transcript.
            except asyncio.CancelledError:
                pass  # superseded — fall through
            except Exception:
                pass  # swallow draft errors

        # No usable speculative draft — generate fresh.
        to_cancel: Optional["asyncio.Task[str]"] = None
        with self._gate:
            to_cancel = self._active.response_task if self._active is not None else None
            self._active = None
        if to_cancel is not None:
            to_cancel.cancel()

        return await generator(final_transcript, ct)

    def abort(self) -> None:
        to_cancel: Optional["asyncio.Task[str]"] = None
        with self._gate:
            to_cancel = self._active.response_task if self._active is not None else None
            self._active = None
        if to_cancel is not None:
            to_cancel.cancel()
