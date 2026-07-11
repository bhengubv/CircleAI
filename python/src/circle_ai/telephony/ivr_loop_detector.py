# ivr_loop_detector.py
#
# Port of CircleAI.Telephony IvrLoopDetector.cs (C# — the EXACT spec).
#
# (3.3.0) Detect when an outbound call has landed in an IVR loop — repeating
# prompts, looping menus, the AI pressing the same digit over and over. Surfaces
# a verdict the orchestrator can act on (escalate to a human, abandon, or try a
# different path).
#
# C# List<IvrRound> + monitor lock -> a list guarded by a lock. LINQ TakeLast /
# Skip / All -> slices + all(). The Jaccard word-set similarity mirrors the C#
# HashSet<string> with an OrdinalIgnoreCase comparer (casefold word sets).

from __future__ import annotations

import threading
from dataclasses import dataclass
from datetime import datetime
from typing import List, Optional


@dataclass(frozen=True, slots=True)
class IvrRound:
    """(3.3.0) One observation in the IVR conversation.

    ``speech``: text heard from the IVR.
    ``dtmf_pressed``: digits the AI sent in response, if any.
    ``at``: when this round happened.
    """

    speech: str
    dtmf_pressed: Optional[str]
    at: datetime


@dataclass(frozen=True, slots=True)
class IvrLoopVerdict:
    """(3.3.0) Verdict on IVR navigation health.

    ``is_looping``: True if the navigator looks stuck.
    ``loop_length``: estimated length of the repeating cycle (number of rounds).
    ``reason``: human-readable reason.
    """

    is_looping: bool
    loop_length: int
    reason: str


class IvrLoopDetector:
    """(3.3.0) Records IVR rounds and surfaces a loop verdict."""

    def __init__(
        self,
        max_rounds_to_track: int = 32,
        min_rounds_for_loop: int = 2,
        similarity_threshold: float = 0.85,
    ) -> None:
        self._max_rounds_to_track = max_rounds_to_track
        self._min_rounds_for_loop = min_rounds_for_loop
        self._similarity_threshold = similarity_threshold
        self._gate = threading.Lock()
        self._rounds: List[IvrRound] = []

    def observe(self, round: IvrRound) -> IvrLoopVerdict:
        """(3.3.0) Append one round and return the current verdict."""
        if round is None:
            raise ValueError("round must not be None")
        with self._gate:
            self._rounds.append(round)
            while len(self._rounds) > self._max_rounds_to_track:
                self._rounds.pop(0)
            return self._evaluate()

    def current_verdict(self) -> IvrLoopVerdict:
        """(3.3.0) Current verdict without adding a new round."""
        with self._gate:
            return self._evaluate()

    def reset(self) -> None:
        """(3.3.0) Drop all history."""
        with self._gate:
            self._rounds.clear()

    def _evaluate(self) -> IvrLoopVerdict:
        rounds = self._rounds
        count = len(rounds)
        # Strong signal first — same DTMF + similar prompt three times in a row.
        if count >= 3:
            tail = rounds[-3:]
            if all(r.dtmf_pressed == tail[0].dtmf_pressed for r in tail) and all(
                self._similar_to(r.speech, tail[0].speech) for r in tail
            ):
                return IvrLoopVerdict(True, 1, "Same prompt-and-press triple in a row.")

        if count < self._min_rounds_for_loop * 2:
            return IvrLoopVerdict(False, 0, "Not enough rounds to evaluate.")

        # Look for a repeating cycle of length L in the last N rounds.
        for length in range(self._min_rounds_for_loop, count // 2 + 1):
            tail = rounds[count - 2 * length:]
            looped = True
            for i in range(length):
                if (
                    not self._similar_to(tail[i].speech, tail[length + i].speech)
                    or tail[i].dtmf_pressed != tail[length + i].dtmf_pressed
                ):
                    looped = False
                    break
            if looped:
                return IvrLoopVerdict(True, length, f"Detected repeating cycle of length {length}.")
        return IvrLoopVerdict(False, 0, "No loop detected.")

    def _similar_to(self, a: str, b: str) -> bool:
        if a is not None and b is not None and a.casefold() == b.casefold():
            return True
        if a is None or b is None:
            return False
        # Cheap Jaccard over word sets.
        set_a = {w.casefold() for w in a.split() if w}
        set_b = {w.casefold() for w in b.split() if w}
        if len(set_a) == 0 or len(set_b) == 0:
            return False
        inter = sum(1 for w in set_a if w in set_b)
        union = len(set_a | set_b)
        return (inter / union) >= self._similarity_threshold
