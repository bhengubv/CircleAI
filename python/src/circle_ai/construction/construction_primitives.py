# construction_primitives.py
#
# Port of CircleAI.Construction ConstructionPrimitives.cs (C# — the EXACT spec).
#
# (3.3.0) Real domain types + in-memory board for the Construction vertical:
# projects, tasks, cost entries. C# ConcurrentDictionary -> dict; the costs list
# is guarded by a single lock. ``decimal Budget/Amount`` -> Decimal, DateTime ->
# datetime, DateTime? -> Optional. OpenConstructionTasksFor returns incomplete
# tasks ordered by due date; SpendFor sums a project's costs; RemainingBudget =
# Budget - SpendFor.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class Project:
    """Mirrors ``CircleAI.Construction.Project`` — ``decimal Budget``,
    ``DateTime? EndOn``.
    """

    project_id: str
    name: str
    start_on: datetime
    end_on: Optional[datetime]
    budget: Decimal
    currency: str


@dataclass(frozen=True, slots=True)
class ConstructionTask:
    """Mirrors ``CircleAI.Construction.ConstructionTask``."""

    construction_task_id: str
    project_id: str
    description: str
    due_on: datetime
    completed: bool


@dataclass(frozen=True, slots=True)
class CostEntry:
    """Mirrors ``CircleAI.Construction.CostEntry`` — ``decimal Amount``."""

    entry_id: str
    project_id: str
    category: str
    amount: Decimal
    at_utc: datetime


class IConstructionBoard(ABC):
    """In-memory board for projects, tasks and cost entries."""

    @abstractmethod
    def create(self, p: Project) -> None:
        ...

    @abstractmethod
    def get_project(self, id: str) -> Optional[Project]:
        ...

    @abstractmethod
    def add(self, t: ConstructionTask) -> None:
        ...

    @abstractmethod
    def complete(self, task_id: str) -> None:
        ...

    @abstractmethod
    def open_construction_tasks_for(self, project_id: str) -> List[ConstructionTask]:
        ...

    @abstractmethod
    def record_cost(self, c: CostEntry) -> None:
        ...

    @abstractmethod
    def spend_for(self, project_id: str) -> Decimal:
        ...

    @abstractmethod
    def remaining_budget(self, project_id: str) -> Decimal:
        ...


class InMemoryConstructionBoard(IConstructionBoard):
    """Thread-safe in-memory :class:`IConstructionBoard`."""

    def __init__(self) -> None:
        self._projects: Dict[str, Project] = {}
        self._tasks: Dict[str, ConstructionTask] = {}
        self._costs: List[CostEntry] = []
        self._lock = threading.Lock()

    def create(self, p: Project) -> None:
        if p is None:
            raise ValueError("project must not be None")
        with self._lock:
            self._projects[p.project_id] = p

    def get_project(self, id: str) -> Optional[Project]:
        with self._lock:
            return self._projects.get(id)

    def add(self, t: ConstructionTask) -> None:
        if t is None:
            raise ValueError("construction task must not be None")
        with self._lock:
            self._tasks[t.construction_task_id] = t

    def complete(self, task_id: str) -> None:
        with self._lock:
            t = self._tasks.get(task_id)
            if t is None:
                raise RuntimeError(f"Unknown task {task_id}")
            self._tasks[task_id] = ConstructionTask(
                t.construction_task_id, t.project_id, t.description, t.due_on, True
            )

    def open_construction_tasks_for(self, project_id: str) -> List[ConstructionTask]:
        with self._lock:
            items = [
                t
                for t in self._tasks.values()
                if t.project_id == project_id and not t.completed
            ]
        items.sort(key=lambda t: t.due_on)
        return items

    def record_cost(self, c: CostEntry) -> None:
        if c is None:
            raise ValueError("cost entry must not be None")
        with self._lock:
            self._costs.append(c)

    def _spend_for_unlocked(self, project_id: str) -> Decimal:
        total = Decimal(0)
        for c in self._costs:
            if c.project_id == project_id:
                total += c.amount
        return total

    def spend_for(self, project_id: str) -> Decimal:
        with self._lock:
            return self._spend_for_unlocked(project_id)

    def remaining_budget(self, project_id: str) -> Decimal:
        with self._lock:
            p = self._projects.get(project_id)
            if p is None:
                raise RuntimeError(f"Unknown project {project_id}")
            return p.budget - self._spend_for_unlocked(project_id)
