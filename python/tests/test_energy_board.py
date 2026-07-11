"""test_energy_board.py — CircleAI.Energy port.

Covers InMemoryEnergyBoard (readings, total kWh = last-first with <2 guard,
cost estimate via peak rate, active outages) and EnergyDomainContext. C# is the
exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone
from decimal import Decimal

import pytest

from circle_ai import (
    EnergyDomainContext,
    EnergyTariff,
    IEnergyBoard,
    InMemoryEnergyBoard,
    MeterReading,
    Outage,
)


def _at(h: int) -> datetime:
    return datetime(2026, 1, 1, tzinfo=timezone.utc) + timedelta(hours=h)


def test_board_is_ienergyboard():
    assert isinstance(InMemoryEnergyBoard(), IEnergyBoard)


def test_total_kwh_last_minus_first():
    b = InMemoryEnergyBoard()
    b.record(MeterReading("m1", 100.0, _at(0)))
    b.record(MeterReading("m1", 130.0, _at(2)))
    b.record(MeterReading("m1", 115.0, _at(1)))
    assert b.total_kwh_since("m1", _at(0)) == pytest.approx(30.0)


def test_total_kwh_single_reading_is_zero():
    b = InMemoryEnergyBoard()
    b.record(MeterReading("m1", 100.0, _at(0)))
    assert b.total_kwh_since("m1", _at(0)) == 0.0


def test_estimate_cost_uses_peak_rate():
    b = InMemoryEnergyBoard()
    b.record(MeterReading("m1", 100.0, _at(0)))
    b.record(MeterReading("m1", 110.0, _at(1)))
    b.set_tariff(EnergyTariff("t1", "Home", 2.5, 1.5, "ZAR"))
    # 10 kWh * 2.5 = 25.0
    assert b.estimate_cost("m1", "t1", _at(0)) == Decimal("25")


def test_estimate_cost_unknown_tariff_raises():
    with pytest.raises(RuntimeError):
        InMemoryEnergyBoard().estimate_cost("m1", "nope", _at(0))


def test_active_outages_only_open():
    b = InMemoryEnergyBoard()
    b.log_outage(Outage("o1", "Zone A", _at(0), None, "storm"))
    b.log_outage(Outage("o2", "Zone B", _at(0), _at(2), "fixed"))
    assert {o.outage_id for o in b.active_outages()} == {"o1"}


def test_energy_domain_context():
    assert EnergyDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Energy]")
    assert "NERSA" in EnergyDomainContext.ComplianceFlags
