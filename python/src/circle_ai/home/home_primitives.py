# home_primitives.py
#
# Port of CircleAI.Home HomePrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Home vertical: rooms,
# smart-home devices, maintenance tasks.
#
# C# ConcurrentDictionary stores map to plain dicts guarded by a single lock.
# C# DateTime DueOn maps to datetime. `Rooms` orders by Name (ordinal);
# `ActiveDevices` filters IsOn. UpcomingTasks(by) returns incomplete tasks due
# on/before `by`, ordered by DueOn. Toggling an unknown device or completing an
# unknown task raises RuntimeError.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, replace
from datetime import datetime
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class Room:
    """Mirrors ``CircleAI.Home.Room`` — ``record(string RoomId, string Name,
    double AreaM2)``.
    """

    room_id: str
    name: str
    area_m2: float


@dataclass(frozen=True, slots=True)
class HomeDevice:
    """Mirrors ``CircleAI.Home.HomeDevice`` — ``record(string DeviceId,
    string Name, string Kind, string? RoomId, bool IsOn)``.
    """

    device_id: str
    name: str
    kind: str
    room_id: Optional[str]
    is_on: bool


@dataclass(frozen=True, slots=True)
class MaintenanceTask:
    """Mirrors ``CircleAI.Home.MaintenanceTask`` — ``record(string TaskId,
    string Description, DateTime DueOn, bool Completed)``.
    """

    task_id: str
    description: str
    due_on: datetime
    completed: bool


class IHomeBoard(ABC):
    """In-memory board for rooms, devices and maintenance tasks."""

    @abstractmethod
    def add_room(self, r: Room) -> None:
        ...

    @abstractmethod
    def get_room(self, id: str) -> Optional[Room]:
        ...

    @property
    @abstractmethod
    def rooms(self) -> List[Room]:
        ...

    @abstractmethod
    def add_device(self, d: HomeDevice) -> None:
        ...

    @abstractmethod
    def toggle(self, device_id: str, on: bool) -> None:
        ...

    @abstractmethod
    def devices_in(self, room_id: str) -> List[HomeDevice]:
        ...

    @property
    @abstractmethod
    def active_devices(self) -> List[HomeDevice]:
        ...

    @abstractmethod
    def schedule_task(self, t: MaintenanceTask) -> None:
        ...

    @abstractmethod
    def complete_task(self, task_id: str) -> None:
        ...

    @abstractmethod
    def upcoming_tasks(self, by: datetime) -> List[MaintenanceTask]:
        ...


class InMemoryHomeBoard(IHomeBoard):
    """Thread-safe in-memory :class:`IHomeBoard`."""

    def __init__(self) -> None:
        self._rooms: Dict[str, Room] = {}
        self._devices: Dict[str, HomeDevice] = {}
        self._tasks: Dict[str, MaintenanceTask] = {}
        self._lock = threading.Lock()

    def add_room(self, r: Room) -> None:
        if r is None:
            raise ValueError("room must not be None")
        with self._lock:
            self._rooms[r.room_id] = r

    def get_room(self, id: str) -> Optional[Room]:
        with self._lock:
            return self._rooms.get(id)

    @property
    def rooms(self) -> List[Room]:
        with self._lock:
            return sorted(self._rooms.values(), key=lambda r: r.name)

    def add_device(self, d: HomeDevice) -> None:
        if d is None:
            raise ValueError("device must not be None")
        with self._lock:
            self._devices[d.device_id] = d

    def toggle(self, device_id: str, on: bool) -> None:
        with self._lock:
            d = self._devices.get(device_id)
            if d is None:
                raise RuntimeError(f"Unknown device {device_id}")
            self._devices[device_id] = replace(d, is_on=on)

    def devices_in(self, room_id: str) -> List[HomeDevice]:
        with self._lock:
            return [d for d in self._devices.values() if d.room_id == room_id]

    @property
    def active_devices(self) -> List[HomeDevice]:
        with self._lock:
            return [d for d in self._devices.values() if d.is_on]

    def schedule_task(self, t: MaintenanceTask) -> None:
        if t is None:
            raise ValueError("task must not be None")
        with self._lock:
            self._tasks[t.task_id] = t

    def complete_task(self, task_id: str) -> None:
        with self._lock:
            t = self._tasks.get(task_id)
            if t is None:
                raise RuntimeError(f"Unknown task {task_id}")
            self._tasks[task_id] = replace(t, completed=True)

    def upcoming_tasks(self, by: datetime) -> List[MaintenanceTask]:
        with self._lock:
            rows = [
                t for t in self._tasks.values() if not t.completed and t.due_on <= by
            ]
        return sorted(rows, key=lambda t: t.due_on)
