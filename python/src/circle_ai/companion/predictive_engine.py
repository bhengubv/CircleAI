# companion/predictive_engine.py
#
# IPredictiveEngine implementations. Ported from CircleAI.Companion — the C#
# reference:
#
#   * SequencePredictiveEngine  (SequencePredictiveEngine.cs)      — n-gram Markov
#   * HistogramPredictiveEngine (HerJarvisRealImplementations.cs)  — time-of-day
#
# SequencePredictiveEngine is a variable-order Markov chain (default 3-gram) over
# the user's observed event timeline. It predicts the next likely events by
# backing off from the longest context to the shortest (weighting longer contexts
# higher, ``weight = 2**k``) and forecasts each event's arrival from its mean
# inter-arrival interval.
#
# HistogramPredictiveEngine is the simpler predecessor: a (day-of-week x hour)
# histogram of recurring needs, scoring the fraction of a need's mass that falls
# inside the horizon window.
#
# ── clock ──
# The C# engines read ``DateTimeOffset.UtcNow`` directly. This port keeps that
# default but accepts an optional ``now_provider`` callable so tests can pin the
# clock deterministically. This is a pure test seam — it does not alter behaviour
# or any produced value.

from __future__ import annotations

import math
import threading
from datetime import datetime, timedelta, timezone
from typing import Callable, List, Optional, Sequence, Tuple

from .herjarvis_contracts import AnticipatedNeed, IPredictiveEngine

NowProvider = Callable[[], datetime]


def _default_now() -> datetime:
    return datetime.now(timezone.utc)


# ======================================================================
# SequencePredictiveEngine — variable-order Markov chain over the timeline.
# ======================================================================
class SequencePredictiveEngine(IPredictiveEngine):
    """Variable-order Markov (n-gram) predictive engine.

    Mirrors ``CircleAI.Companion.SequencePredictiveEngine``.
    """

    __slots__ = ("_transitions", "_inter_arrivals", "_history", "_order", "_lock", "_now")

    def __init__(self, order: int = 3, *, now_provider: Optional[NowProvider] = None) -> None:
        if order < 1 or order > 6:
            raise ValueError("order must be in [1, 6]")
        # (previous-n-events tuple joined by "|") -> { next event -> count }
        self._transitions: dict[str, dict[str, int]] = {}
        # event -> (count, sum_seconds)  — per-event mean interval for forecasting
        self._inter_arrivals: dict[str, Tuple[int, float]] = {}
        self._history: List[Tuple[str, datetime]] = []
        self._order = order
        self._lock = threading.Lock()
        self._now: NowProvider = now_provider or _default_now

    def observe(self, event: str, at_utc: datetime) -> None:
        """Add one event to the user timeline."""
        if event is None or len(event.strip()) == 0:
            raise ValueError("event required")
        with self._lock:
            self._history.append((event, at_utc))
            n = len(self._history)
            # Build n-gram contexts up to _order.
            k = 1
            while k <= self._order and n > k:
                context_start = n - 1 - k
                if context_start < 0:
                    break
                context_items = [e[0] for e in self._history[context_start : context_start + k]]
                key = "|".join(context_items)
                bucket = self._transitions.get(key)
                if bucket is None:
                    bucket = {}
                    self._transitions[key] = bucket
                bucket[event] = bucket.get(event, 0) + 1
                k += 1
            # Track inter-arrival time for this event.
            if n >= 2:
                last_event, last_at = self._history[-2]
                if last_event == event:
                    gap = (at_utc - last_at).total_seconds()
                    prev = self._inter_arrivals.get(event)
                    if prev is None:
                        self._inter_arrivals[event] = (1, gap)
                    else:
                        self._inter_arrivals[event] = (prev[0] + 1, prev[1] + gap)

    async def anticipate_async(
        self, horizon_minutes: int, *, ct: Optional[object] = None
    ) -> Sequence[AnticipatedNeed]:
        if horizon_minutes <= 0:
            raise ValueError("horizon_minutes must be > 0")

        with self._lock:
            snapshot = list(self._history)
            if len(snapshot) == 0:
                return []

            # Take the most recent _order events as the prediction context.
            context_len = min(self._order, len(snapshot))
            context = [e[0] for e in snapshot[len(snapshot) - context_len :]]

            total_score: dict[str, float] = {}
            # Walk from longest context to shortest (back-off), longer = higher weight.
            for k in range(len(context), 0, -1):
                key = "|".join(context[len(context) - k :])
                bucket = self._transitions.get(key)
                if bucket is None:
                    continue
                total_for_ctx = sum(bucket.values())
                if total_for_ctx == 0:
                    continue
                weight = math.pow(2, k)
                for nxt, count in bucket.items():
                    prob = count / total_for_ctx
                    total_score[nxt] = total_score.get(nxt, 0.0) + weight * prob

            if len(total_score) == 0:
                return []

            total_weight = sum(total_score.values())
            horizon_sec = horizon_minutes * 60.0
            now = self._now()
            anticipated: List[AnticipatedNeed] = []
            # OrderByDescending(kv => kv.Value): stable, ties keep insertion order.
            ordered = sorted(total_score.items(), key=lambda kv: kv[1], reverse=True)
            for ev, raw in ordered:
                prob = raw / total_weight
                if prob <= 0:
                    continue
                ia = self._inter_arrivals.get(ev)
                if ia is not None and ia[0] > 0:
                    mean_interval = ia[1] / ia[0]
                else:
                    mean_interval = horizon_sec * 0.5
                if mean_interval > horizon_sec:
                    continue  # not expected within window
                anticipated.append(
                    AnticipatedNeed(
                        description=ev,
                        expected_by_utc=now + timedelta(seconds=mean_interval),
                        probability=prob,
                    )
                )
            return anticipated


# ======================================================================
# HistogramPredictiveEngine — (day-of-week x hour) histogram of needs.
# ======================================================================
class HistogramPredictiveEngine(IPredictiveEngine):
    """Time-of-day histogram predictive engine.

    Mirrors ``CircleAI.Companion.HerJarvis.HistogramPredictiveEngine``.
    """

    __slots__ = ("_hist", "_lock", "_now")

    _SLOTS = 24 * 7

    def __init__(self, *, now_provider: Optional[NowProvider] = None) -> None:
        # description -> long[24*7], case-insensitive on description
        self._hist: dict[str, Tuple[str, List[int]]] = {}
        self._lock = threading.Lock()
        self._now: NowProvider = now_provider or _default_now

    @staticmethod
    def _slot_of(dt: datetime) -> int:
        """(int)DayOfWeek * 24 + UtcHour, with .NET DayOfWeek (Sunday = 0)."""
        u = dt.astimezone(timezone.utc)
        # Python isoweekday(): Mon=1..Sun=7 -> % 7 gives Sun=0, Mon=1..Sat=6 (== .NET).
        dow = u.isoweekday() % 7
        return dow * 24 + u.hour

    def observe(self, description: str, at_utc: datetime) -> None:
        """Tell the engine: this need occurred at this UTC time."""
        if description is None or len(description.strip()) == 0:
            raise ValueError("description required")
        with self._lock:
            lk = description.lower()
            entry = self._hist.get(lk)
            if entry is None:
                entry = (description, [0] * self._SLOTS)
                self._hist[lk] = entry
            entry[1][self._slot_of(at_utc)] += 1

    async def anticipate_async(
        self, horizon_minutes: int, *, ct: Optional[object] = None
    ) -> Sequence[AnticipatedNeed]:
        if horizon_minutes <= 0:
            raise ValueError("horizon_minutes must be > 0")
        now = self._now()
        with self._lock:
            results: List[AnticipatedNeed] = []
            for _lk, (desc, arr) in self._hist.items():
                total = sum(arr)
                upcoming = 0
                m = 0
                while m <= horizon_minutes:
                    when = now + timedelta(minutes=m)
                    upcoming += arr[self._slot_of(when)]
                    m += 30
                if total == 0 or upcoming == 0:
                    continue
                results.append(
                    AnticipatedNeed(
                        description=desc,
                        # horizonMinutes / 2 is integer division in C#.
                        expected_by_utc=now + timedelta(minutes=horizon_minutes // 2),
                        probability=upcoming / total,
                    )
                )
            results.sort(key=lambda r: r.probability, reverse=True)
            return results


__all__ = [
    "SequencePredictiveEngine",
    "HistogramPredictiveEngine",
]
