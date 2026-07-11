# energy_primitives.py
#
# Port of CircleAI.Energy EnergyPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Energy vertical: meter
# readings, tariffs, outages. C# ConcurrentDictionary -> dict; the readings list
# is guarded by a single lock. DateTimeOffset -> datetime, DateTimeOffset? ->
# Optional. ReadingsFor returns a meter's readings at/after `since`, ordered by
# time. TotalKwhSince is (last - first) over that window, or 0.0 with < 2
# readings. EstimateCost = (decimal)(kwh * tariff.PeakKwhRate) — the C# cast of a
# double product to decimal; reproduced with ``Decimal(kwh * rate)``.
# ActiveOutages lists outages with no EndUtc.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class MeterReading:
    """Mirrors ``CircleAI.Energy.MeterReading``."""

    meter_id: str
    kwh: float
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class EnergyTariff:
    """Mirrors ``CircleAI.Energy.EnergyTariff``."""

    tariff_id: str
    name: str
    peak_kwh_rate: float
    off_peak_kwh_rate: float
    currency: str


@dataclass(frozen=True, slots=True)
class Outage:
    """Mirrors ``CircleAI.Energy.Outage`` — ``DateTimeOffset? EndUtc``,
    ``string? Reason``.
    """

    outage_id: str
    area: str
    start_utc: datetime
    end_utc: Optional[datetime]
    reason: Optional[str]


class IEnergyBoard(ABC):
    """In-memory board for meter readings, tariffs and outages."""

    @abstractmethod
    def record(self, r: MeterReading) -> None:
        ...

    @abstractmethod
    def readings_for(self, meter_id: str, since: datetime) -> List[MeterReading]:
        ...

    @abstractmethod
    def total_kwh_since(self, meter_id: str, since: datetime) -> float:
        ...

    @abstractmethod
    def set_tariff(self, t: EnergyTariff) -> None:
        ...

    @abstractmethod
    def get_tariff(self, id: str) -> Optional[EnergyTariff]:
        ...

    @abstractmethod
    def estimate_cost(
        self, meter_id: str, tariff_id: str, since: datetime
    ) -> Decimal:
        ...

    @abstractmethod
    def log_outage(self, o: Outage) -> None:
        ...

    @abstractmethod
    def active_outages(self) -> List[Outage]:
        ...


class InMemoryEnergyBoard(IEnergyBoard):
    """Thread-safe in-memory :class:`IEnergyBoard`."""

    def __init__(self) -> None:
        self._readings: List[MeterReading] = []
        self._tariffs: Dict[str, EnergyTariff] = {}
        self._outages: Dict[str, Outage] = {}
        self._lock = threading.Lock()

    def record(self, r: MeterReading) -> None:
        if r is None:
            raise ValueError("meter reading must not be None")
        with self._lock:
            self._readings.append(r)

    def _readings_for_unlocked(
        self, meter_id: str, since: datetime
    ) -> List[MeterReading]:
        items = [
            r for r in self._readings if r.meter_id == meter_id and r.at_utc >= since
        ]
        items.sort(key=lambda r: r.at_utc)
        return items

    def readings_for(self, meter_id: str, since: datetime) -> List[MeterReading]:
        with self._lock:
            return self._readings_for_unlocked(meter_id, since)

    def _total_kwh_since_unlocked(self, meter_id: str, since: datetime) -> float:
        rows = self._readings_for_unlocked(meter_id, since)
        if len(rows) < 2:
            return 0.0
        return rows[-1].kwh - rows[0].kwh

    def total_kwh_since(self, meter_id: str, since: datetime) -> float:
        with self._lock:
            return self._total_kwh_since_unlocked(meter_id, since)

    def set_tariff(self, t: EnergyTariff) -> None:
        if t is None:
            raise ValueError("tariff must not be None")
        with self._lock:
            self._tariffs[t.tariff_id] = t

    def get_tariff(self, id: str) -> Optional[EnergyTariff]:
        with self._lock:
            return self._tariffs.get(id)

    def estimate_cost(
        self, meter_id: str, tariff_id: str, since: datetime
    ) -> Decimal:
        with self._lock:
            t = self._tariffs.get(tariff_id)
            if t is None:
                raise RuntimeError(f"Unknown tariff {tariff_id}")
            kwh = self._total_kwh_since_unlocked(meter_id, since)
            # C# `(decimal)(kwh * t.PeakKwhRate)` — cast the double product.
            return Decimal(kwh * t.peak_kwh_rate)

    def log_outage(self, o: Outage) -> None:
        if o is None:
            raise ValueError("outage must not be None")
        with self._lock:
            self._outages[o.outage_id] = o

    def active_outages(self) -> List[Outage]:
        with self._lock:
            return [o for o in self._outages.values() if o.end_utc is None]
