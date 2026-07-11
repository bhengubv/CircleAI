"""test_iot_board.py — CircleAI.IoT port.

Covers the domain records and InMemoryIoTBoard (device register + name ordering,
telemetry recording with latest-by-time + NaN-when-empty, newest-first history
with limit + limit guard, command send + newest-first per-device listing). The
C# IoT assembly ships no DomainContext. C# is the exact spec.
"""
from __future__ import annotations

import math
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    IIoTBoard,
    InMemoryIoTBoard,
    IoTCommand,
    IoTDevice,
    IoTTelemetry,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(mins: int) -> datetime:
    return _T0 + timedelta(minutes=mins)


def test_board_is_iiotboard():
    assert isinstance(InMemoryIoTBoard(), IIoTBoard)


def test_devices_ordered_by_name():
    board = InMemoryIoTBoard()
    board.register(IoTDevice("d2", "Thermostat", "hvac", "1.0", _at(0)))
    board.register(IoTDevice("d1", "Camera", "cam", "1.0", _at(0)))
    assert [d.name for d in board.devices] == ["Camera", "Thermostat"]
    assert board.get_device("d1").kind == "cam"


def test_latest_value_and_nan():
    board = InMemoryIoTBoard()
    board.record_telemetry(IoTTelemetry("d1", "temp", 20.0, _at(0)))
    board.record_telemetry(IoTTelemetry("d1", "temp", 22.5, _at(10)))
    board.record_telemetry(IoTTelemetry("d1", "temp", 21.0, _at(5)))
    assert board.latest_value("d1", "temp") == 22.5
    assert math.isnan(board.latest_value("d1", "humidity"))


def test_history_newest_first_and_limit():
    board = InMemoryIoTBoard()
    for i in range(5):
        board.record_telemetry(IoTTelemetry("d1", "temp", float(i), _at(i)))
    hist = board.history("d1", "temp", limit=3)
    assert [t.value for t in hist] == [4.0, 3.0, 2.0]


def test_history_bad_limit_raises():
    with pytest.raises(ValueError):
        InMemoryIoTBoard().history("d1", "temp", limit=0)


def test_commands_for_newest_first():
    board = InMemoryIoTBoard()
    board.send_command(IoTCommand("c1", "d1", "on", "{}", _at(0)))
    board.send_command(IoTCommand("c2", "d1", "off", "{}", _at(10)))
    board.send_command(IoTCommand("c3", "d2", "on", "{}", _at(5)))
    ids = [c.command_id for c in board.commands_for("d1")]
    assert ids == ["c2", "c1"]


def test_record_and_send_none_guards():
    board = InMemoryIoTBoard()
    with pytest.raises(ValueError):
        board.record_telemetry(None)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        board.send_command(None)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        board.register(None)  # type: ignore[arg-type]
