# barge_in_controller.py
#
# Port of CircleAI.Telephony BargeInController.cs (C# — the EXACT spec).
#
# (3.3.0) Barge-in: when the caller interrupts the AI mid-response, pause the TTS
# playback, decide if the interruption was real (versus a cough/ambient noise),
# and either resume or cancel the turn.
#
# C# Func<DateTimeOffset> clock -> Callable[[], datetime] (default
# datetime.now(timezone.utc)). All state is guarded by a lock (C# monitor). The
# reason strings interpolate elapsed-ms with no decimals to match C# ``:F0``.

from __future__ import annotations

import threading
from dataclasses import dataclass
from datetime import datetime, timezone
from enum import IntEnum
from typing import Callable, Optional


class BargeInState(IntEnum):
    """(3.3.0) State of the AI's current turn."""

    #: AI is speaking.
    SPEAKING = 0
    #: Caller interrupted; playback paused while we decide.
    PAUSED = 1
    #: Confirmed real interruption — turn cancelled.
    CANCELLED = 2
    #: Decided false alarm — resumed speaking.
    RESUMED = 3


@dataclass(frozen=True, slots=True)
class BargeInTransition:
    """(3.3.0) One state transition.

    Mirrors ``record(BargeInState From, BargeInState To, DateTimeOffset At, string Reason)``.
    """

    from_state: BargeInState
    to_state: BargeInState
    at: datetime
    reason: str


@dataclass(frozen=True, slots=True)
class BargeInOptions:
    """(3.3.0) Configuration for barge-in detection.

    ``pause_after``: how long the caller must be talking before we pause (default 100 ms).
    ``cancel_after``: continued speech that confirms a real interruption (default 600 ms).
    """

    pause_after: Optional["object"] = None  # timedelta
    cancel_after: Optional["object"] = None  # timedelta

    @property
    def pause_after_or_default(self):
        from datetime import timedelta

        return self.pause_after if self.pause_after is not None else timedelta(milliseconds=100)

    @property
    def cancel_after_or_default(self):
        from datetime import timedelta

        return self.cancel_after if self.cancel_after is not None else timedelta(milliseconds=600)


class BargeInController:
    """(3.3.0) Drives barge-in pause/resume/cancel decisions."""

    def __init__(
        self,
        options: Optional[BargeInOptions] = None,
        clock: Optional[Callable[[], datetime]] = None,
    ) -> None:
        self._options = options if options is not None else BargeInOptions()
        self._clock = clock if clock is not None else (lambda: datetime.now(timezone.utc))
        self._gate = threading.Lock()
        self._state = BargeInState.SPEAKING
        self._caller_speech_started_at: Optional[datetime] = None

    @property
    def state(self) -> BargeInState:
        """The current state of the AI turn."""
        with self._gate:
            return self._state

    def on_playback_start(self) -> None:
        """Call when AI playback begins."""
        with self._gate:
            self._state = BargeInState.SPEAKING
            self._caller_speech_started_at = None

    def on_caller_speech(self) -> Optional[BargeInTransition]:
        """Call on each frame where the VAD reports caller speech."""
        now = self._clock()
        with self._gate:
            if self._state == BargeInState.CANCELLED:
                return None

            if self._caller_speech_started_at is None:
                self._caller_speech_started_at = now
                return None

            elapsed = now - self._caller_speech_started_at
            elapsed_ms = elapsed.total_seconds() * 1000
            if self._state == BargeInState.SPEAKING and elapsed >= self._options.pause_after_or_default:
                t = BargeInTransition(
                    self._state, BargeInState.PAUSED, now, f"Caller speech {elapsed_ms:.0f} ms"
                )
                self._state = BargeInState.PAUSED
                return t
            if self._state == BargeInState.PAUSED and elapsed >= self._options.cancel_after_or_default:
                t = BargeInTransition(
                    self._state,
                    BargeInState.CANCELLED,
                    now,
                    f"Confirmed barge-in after {elapsed_ms:.0f} ms",
                )
                self._state = BargeInState.CANCELLED
                return t
            return None

    def on_caller_silence(self) -> Optional[BargeInTransition]:
        """Call on each frame where VAD reports silence."""
        now = self._clock()
        with self._gate:
            self._caller_speech_started_at = None

            if self._state == BargeInState.PAUSED:
                t = BargeInTransition(
                    self._state, BargeInState.RESUMED, now, "Caller fell silent after pause"
                )
                self._state = BargeInState.SPEAKING  # resume
                return t
            return None

    @property
    def should_emit_audio(self) -> bool:
        """Whether the AI should keep emitting audio frames right now."""
        with self._gate:
            return self._state == BargeInState.SPEAKING

    @property
    def was_barged_in(self) -> bool:
        """Whether the turn was confirmed barge-in (caller wins, AI should drop)."""
        with self._gate:
            return self._state == BargeInState.CANCELLED
