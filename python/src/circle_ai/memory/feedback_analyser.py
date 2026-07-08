# memory/feedback_analyser.py
#
# Analyses a window of FeedbackSignal records and produces PersonaAdaptation
# deltas. Ported from CircleAI.Memory.FeedbackAnalyser (C#) and mirrors the
# verified TypeScript reference (memory/feedback_analyser.ts).
#
# Rules (applied to the most-recent N signals, default N=20):
#   - >70% negative signals -> verbosity_delta = -0.1
#   - >70% positive signals -> verbosity_delta = +0.05
#   - formality_delta is always 0 (reserved for future heuristics)
#   - preferred_topics is always empty — FeedbackSignal carries no topic tags
#
# The C# PersonaAdaptation holds `float` deltas. Python floats are IEEE-754
# doubles, so — exactly as the TS port narrows the constants through Math.fround
# — we narrow the delta literals through float32 (struct pack/unpack) to
# reproduce the FP32 values the C# record would carry (-0.1f, +0.05f). This
# keeps the cross-language fixture contract byte-identical.

from __future__ import annotations

import struct
from dataclasses import dataclass, field
from typing import Iterable

from .feedback_signal import FeedbackPolarity, FeedbackSignal


def _f32(x: float) -> float:
    """Narrow a Python double to float32 precision (mirrors C# `(float)` / TS Math.fround)."""
    return struct.unpack("<f", struct.pack("<f", x))[0]


# FP32-narrowed delta constants, matching the C# `float` literals.
_VERBOSITY_DOWN = _f32(-0.1)
_VERBOSITY_UP = _f32(0.05)


@dataclass
class PersonaAdaptation:
    """Deltas to apply to :class:`PersonaState` after analysing feedback signals."""

    verbosity_delta: float
    formality_delta: float
    preferred_topics: list[str] = field(default_factory=list)


class FeedbackAnalyser:
    """Analyses recent :class:`FeedbackSignal` records and produces
    :class:`PersonaAdaptation` adjustments.
    """

    def __init__(self, window_size: int = 20) -> None:
        """
        :param window_size: Number of most-recent signals to consider. Must be
            at least 1. Default 20.
        """
        if window_size < 1:
            raise ValueError("Window size must be at least 1.")
        self._window_size = window_size

    def analyse(self, signals: Iterable[FeedbackSignal]) -> PersonaAdaptation:
        """Compute persona adaptation from the provided signals.

        ``verbosity_delta`` is:

        * ``-0.1``  when more than 70% of the window is negative
        * ``+0.05`` when more than 70% of the window is positive
        * ``0``     otherwise

        ``formality_delta`` is always 0 and ``preferred_topics`` is always empty
        because :class:`FeedbackSignal` carries no topic metadata.
        """
        if signals is None:
            raise ValueError("signals required")

        window = sorted(
            signals, key=lambda s: s.recorded_at_utc, reverse=True
        )[: self._window_size]

        if len(window) == 0:
            return PersonaAdaptation(0.0, 0.0, [])

        positive_count = sum(
            1 for s in window if s.polarity == FeedbackPolarity.POSITIVE
        )
        negative_count = sum(
            1 for s in window if s.polarity == FeedbackPolarity.NEGATIVE
        )
        total = len(window)

        verbosity_delta = 0.0
        negative_ratio = negative_count / total
        positive_ratio = positive_count / total

        if negative_ratio > 0.70:
            verbosity_delta = _VERBOSITY_DOWN
        elif positive_ratio > 0.70:
            verbosity_delta = _VERBOSITY_UP

        # FeedbackSignal has no tags — topic extraction is deferred.
        return PersonaAdaptation(verbosity_delta, 0.0, [])
