"""test_predictive_engine.py

Verifies the IPredictiveEngine reasoning core ported from CircleAI.Companion:

  * SequencePredictiveEngine  — variable-order Markov (n-gram) over the timeline.
  * HistogramPredictiveEngine — (day-of-week x hour) histogram of needs.

Covers timeline learning, back-off weighting, inter-arrival forecasting, the
day-of-week slot math (.NET DayOfWeek with Sunday=0), horizon filtering, and
ordering. Cross-checked byte-for-byte against a standalone C# harness of the
reference algorithms (SequencePredictiveEngine.cs + HerJarvisRealImplementations.cs).

A pinned ``now_provider`` makes the ETAs deterministic — the C# reference reads
``DateTimeOffset.UtcNow`` directly; the injected clock is a pure test seam.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.companion.herjarvis_contracts import AnticipatedNeed, IPredictiveEngine
from circle_ai.companion.predictive_engine import (
    HistogramPredictiveEngine,
    SequencePredictiveEngine,
)

REF = datetime(2026, 1, 1, 12, 0, 0, tzinfo=timezone.utc)  # a Thursday


def _fixed(dt: datetime):
    return lambda: dt


# ── contracts / construction ──────────────────────────────────────────────


def test_both_engines_implement_ipredictiveengine() -> None:
    assert isinstance(SequencePredictiveEngine(), IPredictiveEngine)
    assert isinstance(HistogramPredictiveEngine(), IPredictiveEngine)


def test_sequence_rejects_order_out_of_range() -> None:
    with pytest.raises(ValueError):
        SequencePredictiveEngine(0)
    with pytest.raises(ValueError):
        SequencePredictiveEngine(7)
    # boundaries are valid
    SequencePredictiveEngine(1)
    SequencePredictiveEngine(6)


def test_sequence_observe_rejects_blank_event() -> None:
    s = SequencePredictiveEngine()
    with pytest.raises(ValueError):
        s.observe("", REF)
    with pytest.raises(ValueError):
        s.observe("   ", REF)


def test_histogram_observe_rejects_blank_description() -> None:
    h = HistogramPredictiveEngine()
    with pytest.raises(ValueError):
        h.observe("", REF)


async def test_anticipate_rejects_non_positive_horizon() -> None:
    for eng in (SequencePredictiveEngine(), HistogramPredictiveEngine()):
        with pytest.raises(ValueError):
            await eng.anticipate_async(0)
        with pytest.raises(ValueError):
            await eng.anticipate_async(-5)


# ── empty state ───────────────────────────────────────────────────────────


async def test_sequence_empty_history_returns_nothing() -> None:
    assert list(await SequencePredictiveEngine().anticipate_async(60)) == []


async def test_histogram_empty_returns_nothing() -> None:
    assert list(await HistogramPredictiveEngine().anticipate_async(60)) == []


# ── sequence engine ───────────────────────────────────────────────────────


async def test_sequence_predicts_next_event_from_repeated_pattern() -> None:
    s = SequencePredictiveEngine(order=3, now_provider=_fixed(REF))
    events = ["wake", "coffee", "email", "wake", "coffee", "email", "wake", "coffee"]
    t = REF
    for e in events:
        s.observe(e, t)
        t += timedelta(minutes=30)

    # Context ends "...wake, coffee"; the pattern always continues to "email".
    needs = list(await s.anticipate_async(120))
    assert len(needs) == 1
    assert needs[0].description == "email"
    assert needs[0].probability == pytest.approx(1.0)
    # "email" is never preceded by "email", so it has no inter-arrival record;
    # the engine falls back to horizon_sec * 0.5 = 60 min -> ETA REF + 60min.
    assert needs[0].expected_by_utc == REF + timedelta(minutes=60)


async def test_sequence_inter_arrival_forecasts_eta_for_self_repeating_event() -> None:
    s = SequencePredictiveEngine(order=2, now_provider=_fixed(REF))
    t = REF
    # "ping" every 20 min: each occurrence is immediately preceded by "ping",
    # so its mean inter-arrival is 20 min.
    for _ in range(4):
        s.observe("ping", t)
        t += timedelta(minutes=20)
    needs = list(await s.anticipate_async(600))
    assert [n.description for n in needs] == ["ping"]
    assert needs[0].expected_by_utc == REF + timedelta(minutes=20)


async def test_sequence_filters_events_beyond_horizon() -> None:
    s = SequencePredictiveEngine(order=2, now_provider=_fixed(REF))
    # "a" then "b" spaced 2 hours apart, repeated; "b" mean interval = 120 min.
    t = REF
    for _ in range(3):
        s.observe("a", t)
        s.observe("b", t + timedelta(minutes=120))
        t += timedelta(hours=6)
    # A 60-min horizon is shorter than b's 120-min mean interval -> filtered out.
    needs = list(await s.anticipate_async(60))
    assert all(n.description != "b" for n in needs)


async def test_sequence_longer_context_outweighs_shorter() -> None:
    s = SequencePredictiveEngine(order=3, now_provider=_fixed(REF))
    # Establish "x,y -> z" strongly, plus a lone "y -> w" so the 1-gram context
    # "y" carries a little mass toward "w".
    t = REF
    for _ in range(4):
        s.observe("x", t)
        s.observe("y", t + timedelta(minutes=10))
        s.observe("z", t + timedelta(minutes=20))
        t += timedelta(hours=1)
    s.observe("y", t)
    s.observe("w", t + timedelta(minutes=5))
    t += timedelta(hours=1)
    # End on "x, y" so the prediction context is "...x, y" — the 2-gram "x|y"
    # (weight 2**2) is available and dominates the 1-gram "y" (weight 2**1).
    s.observe("x", t)
    s.observe("y", t + timedelta(minutes=10))

    needs = list(await s.anticipate_async(600))
    assert len(needs) >= 1
    top = max(needs, key=lambda n: n.probability)
    assert top.description == "z"
    # "w" still surfaces from the shorter context, but with far less mass.
    z = next(n for n in needs if n.description == "z")
    assert all(n.probability <= z.probability for n in needs)


async def test_sequence_results_probabilities_sum_to_one() -> None:
    s = SequencePredictiveEngine(order=2, now_provider=_fixed(REF))
    t = REF
    # From context "a": sometimes b, sometimes c. Tight inter-arrivals so both
    # fall inside the horizon.
    seq = ["a", "b", "a", "c", "a", "b"]
    for e in seq:
        s.observe(e, t)
        t += timedelta(minutes=1)
    needs = list(await s.anticipate_async(600))
    assert len(needs) >= 1
    assert sum(n.probability for n in needs) == pytest.approx(1.0)


# ── histogram engine ──────────────────────────────────────────────────────


async def test_histogram_scores_recurring_slot() -> None:
    h = HistogramPredictiveEngine(now_provider=_fixed(REF))
    for _ in range(3):
        h.observe("standup", REF)
    needs = list(await h.anticipate_async(60))
    assert len(needs) == 1
    assert needs[0].description == "standup"
    # now=12:00, horizon=60: slots 12:00, 12:30 land in hour 12 (count 3 each),
    # 13:00 lands in hour 13 (count 0) -> upcoming = 6, total = 3 -> 2.0.
    assert needs[0].probability == pytest.approx(2.0)
    # ETA is now + horizon//2 minutes.
    assert needs[0].expected_by_utc == REF + timedelta(minutes=30)


async def test_histogram_drops_needs_not_in_window() -> None:
    now = datetime(2026, 1, 1, 11, 45, 0, tzinfo=timezone.utc)
    h = HistogramPredictiveEngine(now_provider=_fixed(now))
    h.observe("lunch", datetime(2026, 1, 1, 12, 0, 0, tzinfo=timezone.utc))
    h.observe("lunch", datetime(2026, 1, 1, 12, 30, 0, tzinfo=timezone.utc))
    h.observe("dinner", datetime(2026, 1, 1, 18, 0, 0, tzinfo=timezone.utc))
    needs = list(await h.anticipate_async(120))
    # dinner's 18:00 slot is outside the 2h window from 11:45 -> dropped.
    assert [n.description for n in needs] == ["lunch"]
    assert needs[0].probability == pytest.approx(2.0)
    assert needs[0].expected_by_utc == now + timedelta(minutes=60)


async def test_histogram_orders_by_probability_descending() -> None:
    now = datetime(2026, 1, 1, 12, 0, 0, tzinfo=timezone.utc)
    h = HistogramPredictiveEngine(now_provider=_fixed(now))
    # "often" always at this slot; "rare" mostly elsewhere.
    for _ in range(5):
        h.observe("often", now)
    h.observe("rare", now)
    h.observe("rare", datetime(2026, 1, 2, 3, 0, 0, tzinfo=timezone.utc))  # other slot
    needs = list(await h.anticipate_async(30))
    descs = [n.description for n in needs]
    assert descs.index("often") < descs.index("rare")
    # descending order invariant
    probs = [n.probability for n in needs]
    assert probs == sorted(probs, reverse=True)


async def test_histogram_day_of_week_slot_uses_dotnet_sunday_zero() -> None:
    # Observe on a Sunday; a Sunday "now" must hit the same slot, a Thursday
    # "now" must not. Validates isoweekday()%7 == .NET DayOfWeek (Sun=0).
    sunday = datetime(2026, 1, 4, 9, 0, 0, tzinfo=timezone.utc)  # 2026-01-04 is Sun
    h = HistogramPredictiveEngine(now_provider=_fixed(sunday))
    h.observe("church", sunday)
    hit = list(await h.anticipate_async(30))
    assert [n.description for n in hit] == ["church"]

    thursday = datetime(2026, 1, 1, 9, 0, 0, tzinfo=timezone.utc)  # Thu, same hour
    h2 = HistogramPredictiveEngine(now_provider=_fixed(thursday))
    h2.observe("church", sunday)  # recorded on Sunday
    miss = list(await h2.anticipate_async(30))
    assert miss == []  # different day-of-week slot -> no upcoming mass


async def test_histogram_case_insensitive_description() -> None:
    h = HistogramPredictiveEngine(now_provider=_fixed(REF))
    h.observe("Standup", REF)
    h.observe("standup", REF)  # same case-insensitive bucket
    needs = list(await h.anticipate_async(30))
    assert len(needs) == 1
    # first-seen casing preserved
    assert needs[0].description == "Standup"


def test_anticipated_need_is_frozen() -> None:
    n = AnticipatedNeed("x", REF, 0.5)
    with pytest.raises(Exception):
        n.probability = 0.9  # type: ignore[misc]
