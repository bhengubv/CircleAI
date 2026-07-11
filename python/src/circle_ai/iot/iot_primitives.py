# iot_primitives.py
#
# Port of CircleAI.IoT IoTPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the IoT vertical: devices,
# telemetry samples, commands.
#
# C# ConcurrentDictionary device store maps to a plain dict; the telemetry and
# command lists are guarded by a single lock (mirroring the C# monitor lock).
# C# DateTimeOffset -> datetime. `Devices` orders by Name (ordinal). LatestValue
# returns NaN (float("nan")) when there is no matching sample. History returns
# newest-first, limited to `limit`; limit<=0 raises. CommandsFor returns that
# device's commands newest-first.

from __future__ import annotations

import math
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class IoTDevice:
    """Mirrors ``CircleAI.IoT.IoTDevice`` — ``record(string DeviceId,
    string Name, string Kind, string FirmwareVersion, DateTimeOffset LastSeenUtc)``.
    """

    device_id: str
    name: str
    kind: str
    firmware_version: str
    last_seen_utc: datetime


@dataclass(frozen=True, slots=True)
class IoTTelemetry:
    """Mirrors ``CircleAI.IoT.IoTTelemetry`` — ``record(string DeviceId,
    string Metric, double Value, DateTimeOffset AtUtc)``.
    """

    device_id: str
    metric: str
    value: float
    at_utc: datetime


@dataclass(frozen=True, slots=True)
class IoTCommand:
    """Mirrors ``CircleAI.IoT.IoTCommand`` — ``record(string CommandId,
    string DeviceId, string Action, string ArgumentsJson, DateTimeOffset SentUtc)``.
    """

    command_id: str
    device_id: str
    action: str
    arguments_json: str
    sent_utc: datetime


class IIoTBoard(ABC):
    """In-memory board for IoT devices, telemetry and commands."""

    @abstractmethod
    def register(self, d: IoTDevice) -> None:
        ...

    @abstractmethod
    def get_device(self, id: str) -> Optional[IoTDevice]:
        ...

    @property
    @abstractmethod
    def devices(self) -> List[IoTDevice]:
        ...

    @abstractmethod
    def record_telemetry(self, t: IoTTelemetry) -> None:
        ...

    @abstractmethod
    def latest_value(self, device_id: str, metric: str) -> float:
        ...

    @abstractmethod
    def history(
        self, device_id: str, metric: str, limit: int = 100
    ) -> List[IoTTelemetry]:
        ...

    @abstractmethod
    def send_command(self, c: IoTCommand) -> None:
        ...

    @abstractmethod
    def commands_for(self, device_id: str) -> List[IoTCommand]:
        ...


class InMemoryIoTBoard(IIoTBoard):
    """Thread-safe in-memory :class:`IIoTBoard`."""

    def __init__(self) -> None:
        self._devices: Dict[str, IoTDevice] = {}
        self._telemetry: List[IoTTelemetry] = []
        self._commands: List[IoTCommand] = []
        self._lock = threading.Lock()

    def register(self, d: IoTDevice) -> None:
        if d is None:
            raise ValueError("device must not be None")
        with self._lock:
            self._devices[d.device_id] = d

    def get_device(self, id: str) -> Optional[IoTDevice]:
        with self._lock:
            return self._devices.get(id)

    @property
    def devices(self) -> List[IoTDevice]:
        with self._lock:
            return sorted(self._devices.values(), key=lambda d: d.name)

    def record_telemetry(self, t: IoTTelemetry) -> None:
        if t is None:
            raise ValueError("telemetry must not be None")
        with self._lock:
            self._telemetry.append(t)

    def latest_value(self, device_id: str, metric: str) -> float:
        with self._lock:
            matches = [
                t
                for t in self._telemetry
                if t.device_id == device_id and t.metric == metric
            ]
            if not matches:
                return math.nan
            return max(matches, key=lambda t: t.at_utc).value

    def history(
        self, device_id: str, metric: str, limit: int = 100
    ) -> List[IoTTelemetry]:
        if limit <= 0:
            raise ValueError("limit")
        with self._lock:
            matches = [
                t
                for t in self._telemetry
                if t.device_id == device_id and t.metric == metric
            ]
        ordered = sorted(matches, key=lambda t: t.at_utc, reverse=True)
        return ordered[:limit]

    def send_command(self, c: IoTCommand) -> None:
        if c is None:
            raise ValueError("command must not be None")
        with self._lock:
            self._commands.append(c)

    def commands_for(self, device_id: str) -> List[IoTCommand]:
        with self._lock:
            matches = [c for c in self._commands if c.device_id == device_id]
        return sorted(matches, key=lambda c: c.sent_utc, reverse=True)
