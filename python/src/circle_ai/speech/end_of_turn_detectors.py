# speech/end_of_turn_detectors.py
#
# Port of CircleAI.Speech/EndOfTurnDetectors.cs (C# — the EXACT spec).
#
# (3.3.0) End-of-turn detectors: null default, a rule-based detector using
# punctuation + trailing-silence heuristics, and a smart-turn wrapper that
# delegates to a host-supplied model runner.

from __future__ import annotations

import math
from abc import ABC, abstractmethod
from datetime import timedelta
from typing import Optional

from .contracts import EndOfTurnResult, IEndOfTurnDetector

_ZERO = timedelta(0)


def _clamp(value: float, lo: float, hi: float) -> float:
    return lo if value < lo else hi if value > hi else value


class NullEndOfTurnDetector(IEndOfTurnDetector):
    """(3.3.0) Always says "they finished" — DI default.

    Mirrors ``CircleAI.Speech.NullEndOfTurnDetector``."""

    _instance: "NullEndOfTurnDetector | None" = None

    @classmethod
    def instance(cls) -> "NullEndOfTurnDetector":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    def predict(self, partial_transcript: str, trailing_silence: timedelta) -> EndOfTurnResult:
        return EndOfTurnResult(is_complete=True, confidence=1.0, wait_more_ms=0)

    def reset(self) -> None:
        pass


class RuleBasedEndOfTurnDetector(IEndOfTurnDetector):
    """(3.3.0) Rule-based detector. Considers a turn complete when the transcript
    ends with terminal punctuation AND the user has been silent for at least the
    minimum hangover, OR when silence exceeds the maximum-wait ceiling regardless
    of text. Recognises common "thinking" connectors (and, but, so, um, like...)
    to extend the wait when present at the tail.

    Mirrors ``CircleAI.Speech.RuleBasedEndOfTurnDetector``."""

    _TERMINAL_PUNCTUATION = (".", "!", "?", "。", "！", "？")
    _HANGING_WORDS = frozenset(
        {
            "and", "but", "so", "or", "because", "if", "when", "while",
            "though", "however", "um", "uh", "like", "you", "the", "a", "an",
        }
    )

    __slots__ = ("_min_silence", "_hanging_silence", "_max_silence")

    def __init__(
        self,
        min_silence: Optional[timedelta] = None,
        hanging_silence: Optional[timedelta] = None,
        max_silence: Optional[timedelta] = None,
    ) -> None:
        self._min_silence = min_silence if min_silence is not None else timedelta(milliseconds=400)
        self._hanging_silence = hanging_silence if hanging_silence is not None else timedelta(milliseconds=900)
        self._max_silence = max_silence if max_silence is not None else timedelta(milliseconds=2500)

    @property
    def backend_id(self) -> str:
        return "rules"

    def predict(self, partial_transcript: str, trailing_silence: timedelta) -> EndOfTurnResult:
        text = (partial_transcript or "").strip()
        if trailing_silence >= self._max_silence:
            return EndOfTurnResult(is_complete=True, confidence=0.7, wait_more_ms=0)

        if len(text) == 0:
            wait = max(150.0, (self._min_silence - trailing_silence) / timedelta(milliseconds=1))
            return EndOfTurnResult(is_complete=False, confidence=0.2, wait_more_ms=int(wait))

        ends_terminal = any(text.endswith(p) for p in self._TERMINAL_PUNCTUATION)
        parts = text.replace("\t", " ").replace("\n", " ").split(" ")
        parts = [p for p in parts if p]
        last_word = parts[-1] if parts else ""
        ends_hanging = last_word.rstrip(".,!?").lower() in self._HANGING_WORDS

        if ends_hanging:
            remaining = self._hanging_silence - trailing_silence
            if remaining <= _ZERO:
                return EndOfTurnResult(is_complete=True, confidence=0.6, wait_more_ms=0)
            ms = math.ceil(remaining / timedelta(milliseconds=1))
            return EndOfTurnResult(is_complete=False, confidence=0.4, wait_more_ms=int(ms))

        if ends_terminal and trailing_silence >= self._min_silence:
            return EndOfTurnResult(is_complete=True, confidence=0.9, wait_more_ms=0)

        if trailing_silence >= self._min_silence:
            return EndOfTurnResult(is_complete=True, confidence=0.75, wait_more_ms=0)

        ms = max(50.0, (self._min_silence - trailing_silence) / timedelta(milliseconds=1))
        return EndOfTurnResult(is_complete=False, confidence=0.6, wait_more_ms=int(ms))

    def reset(self) -> None:
        pass


class ITurnModelRunner(ABC):
    """(3.3.0) Host-supplied semantic turn model.

    Mirrors ``CircleAI.Speech.ITurnModelRunner``."""

    @abstractmethod
    def score_completion(self, partial_transcript: str, trailing_silence: timedelta) -> float:
        """Score the current state; 0..1 = probability the turn is complete."""
        ...


class SmartTurnDetector(IEndOfTurnDetector):
    """(3.3.0) Smart-turn wrapper. Uses the supplied semantic model when present;
    otherwise falls back to ``RuleBasedEndOfTurnDetector``.

    Mirrors ``CircleAI.Speech.SmartTurnDetector``."""

    __slots__ = ("_runner", "_fallback", "_threshold")

    def __init__(self, runner: Optional[ITurnModelRunner] = None, threshold: float = 0.5) -> None:
        self._runner = runner
        self._fallback = RuleBasedEndOfTurnDetector()
        self._threshold = threshold

    @property
    def backend_id(self) -> str:
        return "smart-turn (fallback)" if self._runner is None else "smart-turn-v2"

    def predict(self, partial_transcript: str, trailing_silence: timedelta) -> EndOfTurnResult:
        if self._runner is None:
            return self._fallback.predict(partial_transcript, trailing_silence)

        prob = _clamp(self._runner.score_completion(partial_transcript, trailing_silence), 0.0, 1.0)
        if prob >= self._threshold:
            return EndOfTurnResult(is_complete=True, confidence=prob, wait_more_ms=0)
        # C# does (int)Math.Round((1f - prob) * 1000f) — banker's rounding in C#.
        wait_ms = int(_round_half_even((1.0 - prob) * 1000.0))
        return EndOfTurnResult(is_complete=False, confidence=prob, wait_more_ms=wait_ms)

    def reset(self) -> None:
        self._fallback.reset()


def _round_half_even(x: float) -> float:
    # Match C# Math.Round default (MidpointRounding.ToEven). Python's built-in
    # round() is also banker's rounding on floats.
    return float(round(x))


__all__ = [
    "NullEndOfTurnDetector",
    "RuleBasedEndOfTurnDetector",
    "ITurnModelRunner",
    "SmartTurnDetector",
]
