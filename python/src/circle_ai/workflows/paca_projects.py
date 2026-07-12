# paca_projects.py
#
# Port of CircleAI.Workflows PacaProjects.cs (C# — the EXACT spec).
#
# (3.3.0) Project + task primitives ported from paca. Auto-generates task IDs as
# <PROJECT_PREFIX>-N. Soft deletes via DeletedAtUtc. Row-level project scoping
# via every query taking a project_id. Records map to frozen slotted dataclasses;
# record-with updates map to dataclasses.replace.

from __future__ import annotations

import threading
from dataclasses import dataclass, replace
from datetime import datetime, timezone
from typing import Callable, Dict, List, Optional


@dataclass(frozen=True, slots=True)
class PacaProject:
    """(3.3.0) A workspace that contains tasks."""

    id: str
    name: str
    prefix: str
    settings_json: str
    created_at_utc: datetime
    deleted_at_utc: Optional[datetime]


@dataclass(frozen=True, slots=True)
class PacaTask:
    """(3.3.0) A unit of work inside a project. ``number`` is sequential within
    the project (PACA-1, PACA-2, ...)."""

    project_id: str
    number: int
    title: str
    description_json: str
    status: str
    created_at_utc: datetime
    deleted_at_utc: Optional[datetime]

    def reference(self, prefix: str) -> str:
        return f"{prefix}-{self.number}"


class InMemoryPacaStore:
    """(3.3.0) In-memory project + task store. Replace for production storage."""

    def __init__(self, clock: Optional[Callable[[], datetime]] = None) -> None:
        self._clock = clock if clock is not None else (lambda: datetime.now(timezone.utc))
        self._projects: Dict[str, PacaProject] = {}
        self._tasks_by_project: Dict[str, List[PacaTask]] = {}
        self._next_number: Dict[str, int] = {}
        self._lock = threading.Lock()

    def create_project(
        self, id: str, name: str, prefix: str, settings_json: Optional[str] = None
    ) -> PacaProject:
        """(3.3.0) Create a new project. Throws if the id already exists."""
        if id is None or id.strip() == "":
            raise ValueError("id required")
        if name is None or name.strip() == "":
            raise ValueError("name required")
        if prefix is None or prefix.strip() == "":
            raise ValueError("prefix required")

        project = PacaProject(
            id=id,
            name=name,
            prefix=prefix,
            settings_json=settings_json if settings_json is not None else "{}",
            created_at_utc=self._clock(),
            deleted_at_utc=None,
        )
        with self._lock:
            if id in self._projects:
                raise RuntimeError(f"Project '{id}' already exists.")
            self._projects[id] = project
            self._tasks_by_project[id] = []
            self._next_number[id] = 1
        return project

    def get_project(self, id: str) -> Optional[PacaProject]:
        """(3.3.0) Get a live project by id (excludes soft-deleted)."""
        with self._lock:
            p = self._projects.get(id)
            return p if (p is not None and p.deleted_at_utc is None) else None

    def delete_project(self, id: str) -> None:
        """(3.3.0) Soft-delete a project. Idempotent."""
        with self._lock:
            existing = self._projects.get(id)
            if existing is None or existing.deleted_at_utc is not None:
                return
            self._projects[id] = replace(existing, deleted_at_utc=self._clock())

    def update_project_settings(self, project_id: str, new_settings_json: str) -> PacaProject:
        """(3.3.0) Update the JSON settings bag on a project."""
        with self._lock:
            existing = self._projects.get(project_id)
            if existing is None or existing.deleted_at_utc is not None:
                raise RuntimeError(f"Project '{project_id}' not found.")
            updated = replace(
                existing, settings_json=new_settings_json if new_settings_json is not None else "{}"
            )
            self._projects[project_id] = updated
            return updated

    def add_task(
        self,
        project_id: str,
        title: str,
        description_json: Optional[str] = None,
        status: str = "todo",
    ) -> PacaTask:
        """(3.3.0) Add a task to a project. Auto-numbers it."""
        with self._lock:
            project = self._projects.get(project_id)
            if project is None or project.deleted_at_utc is not None:
                raise RuntimeError(f"Project '{project_id}' not found.")
            number = self._next_number[project_id]
            self._next_number[project_id] = number + 1
            task = PacaTask(
                project_id=project_id,
                number=number,
                title=title if title is not None else "",
                description_json=description_json if description_json is not None else "{}",
                status=status if status is not None else "todo",
                created_at_utc=self._clock(),
                deleted_at_utc=None,
            )
            self._tasks_by_project[project_id].append(task)
            return task

    def list_tasks(self, project_id: str) -> List[PacaTask]:
        """(3.3.0) List live tasks for a project, ordered by number ascending."""
        with self._lock:
            lst = self._tasks_by_project.get(project_id)
            if lst is None:
                return []
            return sorted((t for t in lst if t.deleted_at_utc is None), key=lambda t: t.number)

    def get_task_by_reference(self, project_id: str, reference: str) -> Optional[PacaTask]:
        """(3.3.0) Find one task by reference like "PACA-3"."""
        with self._lock:
            project = self._projects.get(project_id)
            if project is None or project.deleted_at_utc is not None:
                return None
            expected_prefix = project.prefix + "-"
            if not reference.lower().startswith(expected_prefix.lower()):
                return None
            try:
                n = int(reference[len(expected_prefix) :])
            except ValueError:
                return None
            lst = self._tasks_by_project.get(project_id)
            if lst is None:
                return None
            return next((t for t in lst if t.number == n and t.deleted_at_utc is None), None)

    def update_task(self, updated: PacaTask) -> None:
        """(3.3.0) Update a task in place. Caller mutates via dataclasses.replace."""
        if updated is None:
            raise ValueError("updated must not be None")
        with self._lock:
            lst = self._tasks_by_project.get(updated.project_id)
            if lst is None:
                return
            for i in range(len(lst)):
                if lst[i].number == updated.number:
                    lst[i] = updated
                    return

    def delete_task(self, project_id: str, number: int) -> None:
        """(3.3.0) Soft-delete a task."""
        with self._lock:
            lst = self._tasks_by_project.get(project_id)
            if lst is None:
                return
            for i in range(len(lst)):
                if lst[i].number == number:
                    lst[i] = replace(lst[i], deleted_at_utc=self._clock())
                    return
