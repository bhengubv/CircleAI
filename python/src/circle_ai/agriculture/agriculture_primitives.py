# agriculture_primitives.py
#
# Port of CircleAI.Agriculture AgriculturePrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Agriculture vertical:
# fields, crops, yield records. C# ConcurrentDictionary -> dict; the yields list
# is guarded by a single lock. DateTime -> datetime, DateTime? -> Optional.
# AvgYieldOfVariety joins yields to crops and averages (case-insensitive variety
# match); returns 0.0 when there are no rows.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class Field:
    """Mirrors ``CircleAI.Agriculture.Field``."""

    field_id: str
    area_ha: float
    soil_type: str
    irrigation_kind: str


@dataclass(frozen=True, slots=True)
class Crop:
    """Mirrors ``CircleAI.Agriculture.Crop`` — ``DateTime? ExpectedHarvest``."""

    crop_id: str
    field_id: str
    variety: str
    planted_on: datetime
    expected_harvest: Optional[datetime]


@dataclass(frozen=True, slots=True)
class YieldRecord:
    """Mirrors ``CircleAI.Agriculture.YieldRecord``."""

    crop_id: str
    tons_per_ha: float
    harvested_on: datetime


class IFarmBoard(ABC):
    """In-memory board for fields, crops and yield records."""

    @abstractmethod
    def add_field(self, f: Field) -> None:
        ...

    @abstractmethod
    def plant(self, c: Crop) -> None:
        ...

    @abstractmethod
    def record_yield(self, y: YieldRecord) -> None:
        ...

    @abstractmethod
    def get_field(self, id: str) -> Optional[Field]:
        ...

    @abstractmethod
    def crops_for_field(self, field_id: str) -> List[Crop]:
        ...

    @abstractmethod
    def avg_yield_of_variety(self, variety: str) -> float:
        ...


class InMemoryFarmBoard(IFarmBoard):
    """Thread-safe in-memory :class:`IFarmBoard`."""

    def __init__(self) -> None:
        self._fields: Dict[str, Field] = {}
        self._crops: Dict[str, Crop] = {}
        self._yields: List[YieldRecord] = []
        self._lock = threading.Lock()

    def add_field(self, f: Field) -> None:
        if f is None:
            raise ValueError("field must not be None")
        with self._lock:
            self._fields[f.field_id] = f

    def plant(self, c: Crop) -> None:
        if c is None:
            raise ValueError("crop must not be None")
        with self._lock:
            self._crops[c.crop_id] = c

    def record_yield(self, y: YieldRecord) -> None:
        if y is None:
            raise ValueError("yield record must not be None")
        with self._lock:
            self._yields.append(y)

    def get_field(self, id: str) -> Optional[Field]:
        with self._lock:
            return self._fields.get(id)

    def crops_for_field(self, field_id: str) -> List[Crop]:
        with self._lock:
            items = [c for c in self._crops.values() if c.field_id == field_id]
        items.sort(key=lambda c: c.planted_on)
        return items

    def avg_yield_of_variety(self, variety: str) -> float:
        target = variety.casefold()
        with self._lock:
            rows = [
                y
                for y in self._yields
                if (c := self._crops.get(y.crop_id)) is not None
                and c.variety.casefold() == target
            ]
            if not rows:
                return 0.0
            return sum(r.tons_per_ha for r in rows) / len(rows)

    @property
    def field_count(self) -> int:
        """Number of registered fields (C#: ``FieldCount``)."""
        with self._lock:
            return len(self._fields)

    def remove_field(self, field_id: str) -> bool:
        """Remove a field. Returns True if one was present (C#: ``RemoveField``)."""
        with self._lock:
            return self._fields.pop(field_id, None) is not None

    def total_area_ha(self) -> float:
        """Total area of every registered field, in hectares
        (C#: ``TotalAreaHa``).
        """
        with self._lock:
            return sum(f.area_ha for f in self._fields.values())

    def fields_by_soil(self, soil_type: str) -> List[Field]:
        """Fields of a given soil type (case-insensitive), largest area first
        (C#: ``FieldsBySoil``).
        """
        target = soil_type.casefold()
        with self._lock:
            matches = [
                f
                for f in self._fields.values()
                if f.soil_type.casefold() == target
            ]
        return sorted(matches, key=lambda f: f.area_ha, reverse=True)

    def due_for_harvest(self, as_of: datetime) -> List[Crop]:
        """Crops whose expected harvest is on or before ``as_of``, ordered by
        expected harvest (C#: ``DueForHarvest`` — crops with no expected
        harvest are excluded).
        """
        with self._lock:
            matches = [
                c
                for c in self._crops.values()
                if c.expected_harvest is not None
                and c.expected_harvest <= as_of
            ]
        return sorted(matches, key=lambda c: c.expected_harvest)

    def best_yielding_variety(self) -> Optional[str]:
        """The variety with the highest average yield, or None when nothing has
        been harvested (C#: ``BestYieldingVariety`` — variety grouped
        case-insensitively; ties keep first-seen order/casing).
        """
        with self._lock:
            averages: Dict[str, List] = {}  # casefold -> [display, sum, count]
            for y in self._yields:
                c = self._crops.get(y.crop_id)
                if c is None:
                    continue
                key = c.variety.casefold()
                agg = averages.get(key)
                if agg is None:
                    averages[key] = [c.variety, y.tons_per_ha, 1]
                else:
                    agg[1] += y.tons_per_ha
                    agg[2] += 1
            if not averages:
                return None
        ranked = sorted(
            averages.values(), key=lambda a: a[1] / a[2], reverse=True
        )
        return ranked[0][0]
