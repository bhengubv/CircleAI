# business_primitives.py
#
# Port of CircleAI.Business BusinessPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Business vertical:
# business units (a parent/child tree), KPI samples, quarterly targets.
#
# C# ConcurrentDictionary stores map to plain dicts; the KPI list is guarded by
# a single lock (mirroring the C# monitor lock). C# DateTimeOffset -> datetime.
# LatestKpi returns NaN (float("nan")) when there is no sample, mirroring the C#
# `...FirstOrDefault()?.Value ?? double.NaN`. TargetAchievement returns NaN when
# the target is missing or its Target is 0. The target key format is exactly
# "{UnitId}/{Metric}/{Year}Q{Quarter}".

from __future__ import annotations

import math
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import Dict, List, Optional, Sequence


@dataclass(frozen=True, slots=True)
class BusinessUnit:
    """Mirrors ``CircleAI.Business.BusinessUnit`` — ``record(string UnitId,
    string Name, string ParentUnitId, IReadOnlyList<string> KpiTags)``.
    """

    unit_id: str
    name: str
    parent_unit_id: str
    kpi_tags: Sequence[str]


@dataclass(frozen=True, slots=True)
class KpiSample:
    """Mirrors ``CircleAI.Business.KpiSample`` — ``record(string UnitId,
    string Metric, double Value, DateTimeOffset AtUtc)``.
    """

    unit_id: str
    metric: str
    value: float
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class QuarterTarget:
    """Mirrors ``CircleAI.Business.QuarterTarget`` — ``record(string UnitId,
    string Metric, int Year, int Quarter, double Target)``.
    """

    unit_id: str
    metric: str
    year: int
    quarter: int
    target: float


class IBusinessBoard(ABC):
    """In-memory board for business units, KPI samples and quarter targets."""

    @abstractmethod
    def add(self, u: BusinessUnit) -> None:
        ...

    @abstractmethod
    def get_unit(self, id: str) -> Optional[BusinessUnit]:
        ...

    @abstractmethod
    def children_of(self, parent_unit_id: str) -> List[BusinessUnit]:
        ...

    @abstractmethod
    def record(self, s: KpiSample) -> None:
        ...

    @abstractmethod
    def latest_kpi(self, unit_id: str, metric: str) -> float:
        ...

    @abstractmethod
    def set_target(self, t: QuarterTarget) -> None:
        ...

    @abstractmethod
    def target_achievement(
        self, unit_id: str, metric: str, year: int, quarter: int
    ) -> float:
        ...


class InMemoryBusinessBoard(IBusinessBoard):
    """Thread-safe in-memory :class:`IBusinessBoard`."""

    def __init__(self) -> None:
        self._units: Dict[str, BusinessUnit] = {}
        self._kpis: List[KpiSample] = []
        self._targets: Dict[str, QuarterTarget] = {}
        self._lock = threading.Lock()

    def add(self, u: BusinessUnit) -> None:
        if u is None:
            raise ValueError("business unit must not be None")
        with self._lock:
            self._units[u.unit_id] = u

    def get_unit(self, id: str) -> Optional[BusinessUnit]:
        with self._lock:
            return self._units.get(id)

    def children_of(self, parent_unit_id: str) -> List[BusinessUnit]:
        with self._lock:
            return [u for u in self._units.values() if u.parent_unit_id == parent_unit_id]

    def record(self, s: KpiSample) -> None:
        if s is None:
            raise ValueError("kpi sample must not be None")
        with self._lock:
            self._kpis.append(s)

    def latest_kpi(self, unit_id: str, metric: str) -> float:
        with self._lock:
            return self._latest_kpi_unlocked(unit_id, metric)

    def _latest_kpi_unlocked(self, unit_id: str, metric: str) -> float:
        matches = [
            k for k in self._kpis if k.unit_id == unit_id and k.metric == metric
        ]
        if not matches:
            return math.nan
        # OrderByDescending(AtUtc).FirstOrDefault() -> the newest sample.
        latest = max(matches, key=lambda k: k.at_utc)
        return latest.value

    def set_target(self, t: QuarterTarget) -> None:
        if t is None:
            raise ValueError("quarter target must not be None")
        with self._lock:
            self._targets[f"{t.unit_id}/{t.metric}/{t.year}Q{t.quarter}"] = t

    def target_achievement(
        self, unit_id: str, metric: str, year: int, quarter: int
    ) -> float:
        key = f"{unit_id}/{metric}/{year}Q{quarter}"
        with self._lock:
            target = self._targets.get(key)
            if target is None or target.target == 0:
                return math.nan
            return self._latest_kpi_unlocked(unit_id, metric) / target.target
