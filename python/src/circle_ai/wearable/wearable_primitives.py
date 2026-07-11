# wearable_primitives.py
#
# Port of CircleAI.Wearable WearablePrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Wearable vertical: device
# descriptors and telemetry samples. C# ConcurrentDictionary -> dict; the
# samples list is guarded by a single lock. DateTimeOffset -> datetime.
# ``double? LatestValue`` -> Optional[float]. Record raises when the device is
# unknown. Devices are ordered by vendor. ReadSince returns a device+kind's
# samples at/after `since`, ordered by time. AverageValue returns NaN when the
# window is empty.

from __future__ import annotations

import math
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import Dict, List, Optional


class WearableKind(IntEnum):
    """Mirrors ``CircleAI.Wearable.WearableKind``. Stable ordinals."""

    SMARTWATCH = 0
    FITNESS_BAND = 1
    CHEST_STRAP = 2
    PATCH = 3
    HEADSET = 4


class WearableTelemetryKind(IntEnum):
    """Mirrors ``CircleAI.Wearable.WearableTelemetryKind``. Stable ordinals."""

    HEART_RATE = 0
    STEPS = 1
    CALORIES = 2
    SLEEP_STAGE = 3
    SKIN_TEMP_C = 4
    STRESS = 5
    OXYGEN_PCT = 6


@dataclass(frozen=True, slots=True)
class WearableDevice:
    """Mirrors ``CircleAI.Wearable.WearableDevice``."""

    device_id: str
    kind: WearableKind
    vendor: str
    firmware_version: str
    battery_pct: float


@dataclass(frozen=True, slots=True)
class WearableSample:
    """Mirrors ``CircleAI.Wearable.WearableSample``."""

    device_id: str
    kind: WearableTelemetryKind
    value: float
    at_utc: datetime


class IWearableBoard(ABC):
    """In-memory board for wearable devices and telemetry samples."""

    @abstractmethod
    def add(self, d: WearableDevice) -> None:
        ...

    @abstractmethod
    def get_device(self, id: str) -> Optional[WearableDevice]:
        ...

    @property
    @abstractmethod
    def devices(self) -> List[WearableDevice]:
        ...

    @abstractmethod
    def record(self, s: WearableSample) -> None:
        ...

    @abstractmethod
    def read_since(
        self, device_id: str, kind: WearableTelemetryKind, since: datetime
    ) -> List[WearableSample]:
        ...

    @abstractmethod
    def latest_value(
        self, device_id: str, kind: WearableTelemetryKind
    ) -> Optional[float]:
        ...

    @abstractmethod
    def average_value(
        self, device_id: str, kind: WearableTelemetryKind, since: datetime
    ) -> float:
        ...


class InMemoryWearableBoard(IWearableBoard):
    """Thread-safe in-memory :class:`IWearableBoard`."""

    def __init__(self) -> None:
        self._devices: Dict[str, WearableDevice] = {}
        self._samples: List[WearableSample] = []
        self._lock = threading.Lock()

    def add(self, d: WearableDevice) -> None:
        if d is None:
            raise ValueError("wearable device must not be None")
        with self._lock:
            self._devices[d.device_id] = d

    def get_device(self, id: str) -> Optional[WearableDevice]:
        with self._lock:
            return self._devices.get(id)

    @property
    def devices(self) -> List[WearableDevice]:
        with self._lock:
            items = list(self._devices.values())
        items.sort(key=lambda d: d.vendor)
        return items

    def record(self, s: WearableSample) -> None:
        if s is None:
            raise ValueError("wearable sample must not be None")
        with self._lock:
            if s.device_id not in self._devices:
                raise RuntimeError(f"Unknown device {s.device_id}")
            self._samples.append(s)

    def _read_since_unlocked(
        self, device_id: str, kind: WearableTelemetryKind, since: datetime
    ) -> List[WearableSample]:
        items = [
            s
            for s in self._samples
            if s.device_id == device_id and s.kind == kind and s.at_utc >= since
        ]
        items.sort(key=lambda s: s.at_utc)
        return items

    def read_since(
        self, device_id: str, kind: WearableTelemetryKind, since: datetime
    ) -> List[WearableSample]:
        with self._lock:
            return self._read_since_unlocked(device_id, kind, since)

    def latest_value(
        self, device_id: str, kind: WearableTelemetryKind
    ) -> Optional[float]:
        with self._lock:
            matches = [
                s
                for s in self._samples
                if s.device_id == device_id and s.kind == kind
            ]
            if not matches:
                return None
            return max(matches, key=lambda s: s.at_utc).value

    def average_value(
        self, device_id: str, kind: WearableTelemetryKind, since: datetime
    ) -> float:
        with self._lock:
            items = self._read_since_unlocked(device_id, kind, since)
        if not items:
            return math.nan
        return sum(s.value for s in items) / len(items)
