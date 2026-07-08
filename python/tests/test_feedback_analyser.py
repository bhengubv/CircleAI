"""test_feedback_analyser.py

Exercises FeedbackAnalyser (persona-adaptation deltas from a window of signals)
and the InMemoryFeedbackStore. Mirrors the TypeScript feedback_analyser.test.ts
and the C# FeedbackAnalyser rules / FeedbackStoreTests.
"""
from __future__ import annotations

import struct
import uuid
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.memory.feedback_analyser import FeedbackAnalyser, PersonaAdaptation
from circle_ai.memory.feedback_signal import FeedbackPolarity, FeedbackSignal
from circle_ai.memory.stores import InMemoryFeedbackStore


def _f32(x: float) -> float:
    return struct.unpack("<f", struct.pack("<f", x))[0]


# FP32-narrowed deltas — must equal the C# `float` literals exactly.
VERBOSITY_DOWN = _f32(-0.1)
VERBOSITY_UP = _f32(0.05)

_BASE = datetime(2023, 11, 14, tzinfo=timezone.utc)
_seq = 0


def make(
    polarity: FeedbackPolarity,
    at: datetime | None = None,
    user: str = "user",
) -> FeedbackSignal:
    global _seq
    ts = at if at is not None else _BASE + timedelta(seconds=_seq)
    _seq += 1
    return FeedbackSignal(
        id=uuid.uuid4(),
        recorded_at_utc=ts,
        user_text=user,
        assistant_text="response",
        polarity=polarity,
    )


# ══════════════════════════════════════════════════════════════════════════
# FeedbackAnalyser
# ══════════════════════════════════════════════════════════════════════════


def test_rejects_a_window_size_below_1():
    with pytest.raises(ValueError):
        FeedbackAnalyser(0)


def test_returns_zero_deltas_for_empty_signal_set():
    a = FeedbackAnalyser().analyse([])
    assert a.verbosity_delta == 0
    assert a.formality_delta == 0
    assert a.preferred_topics == []


def test_drops_verbosity_when_over_70_percent_negative():
    analyser = FeedbackAnalyser()
    # 8 negative + 2 positive = 80% negative.
    signals = [make(FeedbackPolarity.NEGATIVE) for _ in range(8)]
    signals += [make(FeedbackPolarity.POSITIVE) for _ in range(2)]

    a = analyser.analyse(signals)
    assert a.verbosity_delta == VERBOSITY_DOWN
    assert a.formality_delta == 0
    assert a.preferred_topics == []


def test_raises_verbosity_when_over_70_percent_positive():
    analyser = FeedbackAnalyser()
    signals = [make(FeedbackPolarity.POSITIVE) for _ in range(8)]
    signals += [make(FeedbackPolarity.NEGATIVE) for _ in range(2)]

    a = analyser.analyse(signals)
    assert a.verbosity_delta == VERBOSITY_UP


def test_leaves_verbosity_at_0_for_balanced_window():
    analyser = FeedbackAnalyser()
    signals = [make(FeedbackPolarity.POSITIVE) for _ in range(5)]
    signals += [make(FeedbackPolarity.NEGATIVE) for _ in range(5)]

    assert analyser.analyse(signals).verbosity_delta == 0


def test_treats_exactly_70_percent_as_not_crossing_threshold():
    analyser = FeedbackAnalyser(10)
    # Exactly 7/10 negative — 0.70 is not > 0.70.
    signals = [make(FeedbackPolarity.NEGATIVE) for _ in range(7)]
    signals += [make(FeedbackPolarity.POSITIVE) for _ in range(3)]

    assert analyser.analyse(signals).verbosity_delta == 0


def test_only_considers_most_recent_window_size_signals_newest_first():
    analyser = FeedbackAnalyser(3)
    # Older bulk is positive; the 3 newest are negative → window is 100% negative.
    older = [
        make(FeedbackPolarity.POSITIVE, datetime(2023, 1, 1, tzinfo=timezone.utc) + timedelta(seconds=i))
        for i in range(10)
    ]
    newest = [
        make(FeedbackPolarity.NEGATIVE, datetime(2024, 1, 1, tzinfo=timezone.utc) + timedelta(seconds=i))
        for i in range(3)
    ]

    a = analyser.analyse([*older, *newest])
    assert a.verbosity_delta == VERBOSITY_DOWN


def test_ignores_correction_signals_in_the_ratio():
    analyser = FeedbackAnalyser()
    # 8 negative + 2 correction = 8/10 = 80% negative → down.
    signals = [make(FeedbackPolarity.NEGATIVE) for _ in range(8)]
    signals += [make(FeedbackPolarity.CORRECTION) for _ in range(2)]
    assert analyser.analyse(signals).verbosity_delta == VERBOSITY_DOWN


def test_rejects_a_null_signal_set():
    with pytest.raises(ValueError):
        FeedbackAnalyser().analyse(None)  # type: ignore[arg-type]


def test_persona_adaptation_is_a_dataclass():
    a = PersonaAdaptation(0.0, 0.0, [])
    assert a.verbosity_delta == 0.0
    assert a.formality_delta == 0.0
    assert a.preferred_topics == []


# ══════════════════════════════════════════════════════════════════════════
# InMemoryFeedbackStore
# ══════════════════════════════════════════════════════════════════════════


async def test_store_rejects_a_null_signal():
    store = InMemoryFeedbackStore()
    with pytest.raises(ValueError):
        await store.add_async(None)  # type: ignore[arg-type]


async def test_store_add_increments_the_count():
    store = InMemoryFeedbackStore()
    await store.add_async(make(FeedbackPolarity.POSITIVE))
    assert await store.count_async() == 1


async def test_store_get_recent_on_empty_returns_empty():
    store = InMemoryFeedbackStore()
    assert await store.get_recent_async(10) == []


async def test_store_get_recent_returns_newest_first():
    store = InMemoryFeedbackStore()
    now = datetime.now(timezone.utc)
    await store.add_async(make(FeedbackPolarity.POSITIVE, now - timedelta(minutes=10), "old"))
    await store.add_async(make(FeedbackPolarity.NEGATIVE, now, "new"))

    result = await store.get_recent_async(10)
    assert len(result) == 2
    assert result[0].user_text == "new"


async def test_store_positive_ratio_returns_none_with_no_signals():
    store = InMemoryFeedbackStore()
    assert await store.positive_ratio_async() is None


async def test_store_positive_ratio_returns_1_when_all_positive():
    store = InMemoryFeedbackStore()
    await store.add_async(make(FeedbackPolarity.POSITIVE))
    await store.add_async(make(FeedbackPolarity.POSITIVE))
    assert await store.positive_ratio_async() == 1.0


async def test_store_positive_ratio_returns_right_fraction_for_mixed():
    store = InMemoryFeedbackStore()
    await store.add_async(make(FeedbackPolarity.POSITIVE))
    await store.add_async(make(FeedbackPolarity.POSITIVE))
    await store.add_async(make(FeedbackPolarity.NEGATIVE))
    ratio = await store.positive_ratio_async()
    assert ratio is not None and 0.66 < ratio < 0.68  # 2/3


async def test_store_evicts_oldest_when_max_signals_exceeded_fifo():
    store = InMemoryFeedbackStore(3)
    for i in range(5):
        await store.add_async(make(FeedbackPolarity.POSITIVE, None, f"u{i}"))
    assert await store.count_async() == 3


def test_store_rejects_non_positive_max_signals():
    with pytest.raises(ValueError):
        InMemoryFeedbackStore(0)
