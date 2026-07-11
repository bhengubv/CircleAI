"""test_wearable_board.py — CircleAI.Wearable port.

Covers WearableKind/WearableTelemetryKind, WearableContext, and
InMemoryWearableBoard (device registry ordered by vendor, record requires known
device, read-since window, latest value, average incl. NaN when empty).
C# is the exact spec.
"""
from __future__ import annotations

import math
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    IWearableBoard,
    InMemoryWearableBoard,
    WearableContext,
    WearableDevice,
    WearableKind,
    WearableSample,
    WearableTelemetryKind,
)


def _at(mins: int) -> datetime:
    return datetime(2026, 1, 1, tzinfo=timezone.utc) + timedelta(minutes=mins)


def _dev(did: str, vendor: str) -> WearableDevice:
    return WearableDevice(did, WearableKind.SMARTWATCH, vendor, "1.0", 100.0)


def test_board_is_iwearableboard():
    assert isinstance(InMemoryWearableBoard(), IWearableBoard)


def test_devices_ordered_by_vendor():
    b = InMemoryWearableBoard()
    b.add(_dev("d2", "Zephyr"))
    b.add(_dev("d1", "Apple"))
    assert [d.device_id for d in b.devices] == ["d1", "d2"]


def test_record_unknown_device_raises():
    b = InMemoryWearableBoard()
    with pytest.raises(RuntimeError):
        b.record(WearableSample("ghost", WearableTelemetryKind.HEART_RATE, 70.0, _at(0)))


def test_read_since_and_latest_and_average():
    b = InMemoryWearableBoard()
    b.add(_dev("d1", "Apple"))
    b.record(WearableSample("d1", WearableTelemetryKind.HEART_RATE, 60.0, _at(0)))
    b.record(WearableSample("d1", WearableTelemetryKind.HEART_RATE, 80.0, _at(10)))
    b.record(WearableSample("d1", WearableTelemetryKind.HEART_RATE, 40.0, _at(-100)))  # before window
    since = _at(-1)
    win = b.read_since("d1", WearableTelemetryKind.HEART_RATE, since)
    assert [s.value for s in win] == [60.0, 80.0]
    assert b.latest_value("d1", WearableTelemetryKind.HEART_RATE) == 80.0
    assert b.average_value("d1", WearableTelemetryKind.HEART_RATE, since) == pytest.approx(70.0)


def test_latest_value_none_and_average_nan_when_empty():
    b = InMemoryWearableBoard()
    b.add(_dev("d1", "Apple"))
    assert b.latest_value("d1", WearableTelemetryKind.STEPS) is None
    assert math.isnan(b.average_value("d1", WearableTelemetryKind.STEPS, _at(0)))


def test_wearable_context_record():
    ctx = WearableContext(72.0, 5000, 98.0, 33.5, True, _at(0))
    assert ctx.heart_rate_bpm == 72.0
    assert ctx.is_workout_active is True


def test_telemetry_kind_ordinals_stable():
    assert int(WearableTelemetryKind.HEART_RATE) == 0
    assert int(WearableTelemetryKind.OXYGEN_PCT) == 6
