"""test_wearable_biosignals.py — CircleAI.Wearable.Biosignals port.

Covers BiosignalKind ordinals, BiosignalSample.create (clamp + guid + ts), the
null + recorded sources (streaming), the sliding-window aggregator (min/max/mean
over in-window samples), and the deterministic biosignal->affect mapper.
asyncio_mode = auto. C# is the exact spec.
"""
from __future__ import annotations

import uuid
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    BiosignalAggregator,
    BiosignalKind,
    BiosignalSample,
    IBiosignalSource,
    NullBiosignalSource,
    RecordedBiosignalSource,
    apply_biosignal_to_affect,
)
from circle_ai.memory.affect_state import AffectState


def _sample(kind: BiosignalKind, value: float, at: datetime, conf: float = 1.0) -> BiosignalSample:
    return BiosignalSample(uuid.uuid4(), kind, value, "u", conf, False, at)


def test_kind_ordinals_stable():
    assert int(BiosignalKind.HEART_RATE) == 0
    assert int(BiosignalKind.GALVANIC_SKIN_RESPONSE) == 7
    assert int(BiosignalKind.UNKNOWN) == 8


def test_sample_create_clamps_confidence_and_stamps():
    s = BiosignalSample.create(BiosignalKind.HEART_RATE, 72.0, "bpm", confidence=5.0)
    assert s.confidence == 1.0
    assert isinstance(s.id, uuid.UUID)
    assert s.measured_at.tzinfo is not None
    s2 = BiosignalSample.create(BiosignalKind.STEPS, 100.0, "count", confidence=-1.0)
    assert s2.confidence == 0.0


def test_null_source_supports_and_emits_nothing():
    src = NullBiosignalSource()
    assert isinstance(src, IBiosignalSource)
    assert tuple(src.supported_kinds) == ()


async def test_null_source_stream_empty():
    src = NullBiosignalSource()
    got = [s async for s in src.stream_async(None)]
    assert got == []
    assert await src.is_supported_async(BiosignalKind.HEART_RATE, None) is False


async def test_recorded_source_replays_and_supported_kinds():
    t = datetime(2026, 1, 1, tzinfo=timezone.utc)
    samples = [
        _sample(BiosignalKind.HEART_RATE, 60.0, t),
        _sample(BiosignalKind.OXYGEN_SATURATION, 98.0, t),
    ]
    src = RecordedBiosignalSource(samples)
    assert set(src.supported_kinds) == {BiosignalKind.HEART_RATE, BiosignalKind.OXYGEN_SATURATION}
    assert await src.is_supported_async(BiosignalKind.HEART_RATE, None) is True
    assert await src.is_supported_async(BiosignalKind.STEPS, None) is False
    got = [s async for s in src.stream_async(None)]
    assert len(got) == 2


async def test_aggregator_min_max_mean():
    now = datetime.now(timezone.utc)
    samples = [
        _sample(BiosignalKind.HEART_RATE, 60.0, now),
        _sample(BiosignalKind.HEART_RATE, 80.0, now),
        _sample(BiosignalKind.HEART_RATE, 100.0, now),
    ]
    agg = BiosignalAggregator(RecordedBiosignalSource(samples))
    snap = await agg.snapshot_async(timedelta(minutes=5), None)
    stats = snap.stats[BiosignalKind.HEART_RATE]
    assert stats.sample_count == 3
    assert stats.min == pytest.approx(60.0)
    assert stats.max == pytest.approx(100.0)
    assert stats.mean == pytest.approx(80.0)


async def test_aggregator_skips_out_of_window():
    now = datetime.now(timezone.utc)
    samples = [
        _sample(BiosignalKind.HEART_RATE, 60.0, now),
        _sample(BiosignalKind.HEART_RATE, 999.0, now - timedelta(hours=1)),  # before cutoff
    ]
    agg = BiosignalAggregator(RecordedBiosignalSource(samples))
    snap = await agg.snapshot_async(timedelta(minutes=5), None)
    stats = snap.stats[BiosignalKind.HEART_RATE]
    assert stats.sample_count == 1
    assert stats.max == pytest.approx(60.0)


async def test_aggregator_zero_window_raises():
    agg = BiosignalAggregator(NullBiosignalSource())
    with pytest.raises(ValueError):
        await agg.snapshot_async(timedelta(0), None)


def test_affect_mapper_high_heart_rate():
    a = AffectState()
    base_energy = a.energy
    base_unc = a.uncertainty
    apply_biosignal_to_affect(_sample(BiosignalKind.HEART_RATE, 140.0, datetime.now(timezone.utc)), a)
    assert a.energy > base_energy
    assert a.uncertainty > base_unc


def test_affect_mapper_low_confidence_no_mutation():
    a = AffectState()
    before = (a.energy, a.uncertainty, a.rapport, a.engagement)
    apply_biosignal_to_affect(
        _sample(BiosignalKind.HEART_RATE, 140.0, datetime.now(timezone.utc), conf=0.2), a
    )
    assert (a.energy, a.uncertainty, a.rapport, a.engagement) == before


def test_affect_mapper_low_spo2_raises_uncertainty():
    a = AffectState()
    base = a.uncertainty
    apply_biosignal_to_affect(_sample(BiosignalKind.OXYGEN_SATURATION, 85.0, datetime.now(timezone.utc)), a)
    assert a.uncertainty == pytest.approx(min(1.0, base + 0.10), abs=1e-6)


def test_affect_mapper_sleep_stage_no_mutation():
    a = AffectState()
    before = (a.energy, a.uncertainty, a.rapport, a.engagement)
    apply_biosignal_to_affect(_sample(BiosignalKind.SLEEP_STAGE, 2.0, datetime.now(timezone.utc)), a)
    assert (a.energy, a.uncertainty, a.rapport, a.engagement) == before
