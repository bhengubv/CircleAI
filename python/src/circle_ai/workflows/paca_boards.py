# paca_boards.py
#
# Port of CircleAI.Workflows PacaBoards.cs (C# — the EXACT spec).
#
# (3.3.0) Sprintboard surface ported from paca: rich JSON description, custom
# fields, story points, importance, parent/child relations, status columns with
# position-ordered workflow, drag-and-drop status transitions, sprints with
# lifecycle states, Scrumban swimlanes, per-view persistent configs (filters +
# sort + visible fields), tags, lazy-load pagination per column.
#
# The C# ConcurrentDictionary<(string,int), TaskBoardMetadata> maps to a plain
# dict keyed on a (project_id, number) tuple. Records → frozen dataclasses;
# record-with → dataclasses.replace.

from __future__ import annotations

import threading
from dataclasses import dataclass, field, replace
from datetime import datetime, timezone
from enum import IntEnum
from typing import Callable, Dict, List, Optional, Tuple

from .paca_projects import InMemoryPacaStore, PacaTask


class SprintState(IntEnum):
    """(3.3.0) Sprint lifecycle."""

    Planning = 0
    Active = 1
    Completed = 2


@dataclass(frozen=True, slots=True)
class StatusColumn:
    """(3.3.0) Status column in the workflow.

    ``name`` is "todo" / "in_progress" / "in_review" / "done"; ``category`` is
    "open" / "in-flight" / "review" / "closed" / "cancelled" / "blocked"."""

    name: str
    category: str
    position: int
    collapsed: bool


@dataclass(frozen=True, slots=True)
class PacaSprint:
    """(3.3.0) Sprint."""

    id: str
    project_id: str
    name: str
    goal: str
    start_date: datetime
    end_date: datetime
    state: SprintState


@dataclass(frozen=True, slots=True)
class TaskBoardMetadata:
    """(3.3.0) Extra board-only metadata on top of :class:`PacaTask`.
    ``importance`` is 0..5."""

    project_id: str
    number: int
    story_points: int
    importance: int
    assignee_member_id: Optional[str]
    reporter_member_id: Optional[str]
    parent_task_number: Optional[int]
    sprint_id: Optional[str]
    tags: List[str]
    custom_fields: Dict[str, str]
    position_in_column: int


@dataclass(frozen=True, slots=True)
class BoardView:
    """(3.3.0) A per-user / per-board "named view".

    ``sort_by`` is "importance" / "story_points" / "newest"."""

    name: str
    filter_tags_csv: Optional[str]
    filter_assignee: Optional[str]
    sort_by: Optional[str]
    sort_descending: bool
    visible_columns: List[str]
    visible_fields: List[str]


class PacaBoard:
    """(3.3.0) Board service over a project. Sprints + columns + per-task
    metadata + views."""

    def __init__(
        self, tasks: InMemoryPacaStore, clock: Optional[Callable[[], datetime]] = None
    ) -> None:
        if tasks is None:
            raise ValueError("tasks must not be None")
        self._tasks = tasks
        self._clock = clock if clock is not None else (lambda: datetime.now(timezone.utc))
        self._columns: Dict[str, StatusColumn] = {}
        self._sprints: Dict[str, PacaSprint] = {}
        self._metadata: Dict[Tuple[str, int], TaskBoardMetadata] = {}
        self._views: Dict[str, BoardView] = {}
        self._lock = threading.Lock()
        self._add_default_columns()

    def _add_default_columns(self) -> None:
        self._columns["todo"] = StatusColumn("todo", "open", 0, False)
        self._columns["in_progress"] = StatusColumn("in_progress", "in-flight", 1, False)
        self._columns["in_review"] = StatusColumn("in_review", "review", 2, False)
        self._columns["done"] = StatusColumn("done", "closed", 3, False)
        self._columns["cancelled"] = StatusColumn("cancelled", "cancelled", 4, False)
        self._columns["blocked"] = StatusColumn("blocked", "blocked", 5, True)

    @property
    def columns(self) -> List[StatusColumn]:
        with self._lock:
            return sorted(self._columns.values(), key=lambda c: c.position)

    def add_column(self, col: StatusColumn) -> None:
        if col is None:
            raise ValueError("col must not be None")
        with self._lock:
            self._columns[col.name] = col

    def collapse_column(self, name: str, collapsed: bool) -> None:
        with self._lock:
            col = self._columns.get(name)
            if col is not None:
                self._columns[name] = replace(col, collapsed=collapsed)

    def move_task(self, project_id: str, number: int, new_status: str, new_position: int) -> None:
        """(3.3.0) Move a task between status columns, updating its in-column
        position."""
        task = self._tasks.get_task_by_reference(project_id, f"{project_id}-{number}")
        if task is None:
            task = next((t for t in self._tasks.list_tasks(project_id) if t.number == number), None)
        if task is None:
            raise RuntimeError("Task not found.")
        with self._lock:
            if new_status not in self._columns:
                raise ValueError(f"Unknown status '{new_status}'.")
        self._tasks.update_task(replace(task, status=new_status))
        with self._lock:
            meta = replace(
                self._get_or_create_metadata_locked(project_id, number),
                position_in_column=new_position,
            )
            self._metadata[(project_id, number)] = meta

    def set_task_metadata(self, metadata: TaskBoardMetadata) -> None:
        """(3.3.0) Attach board metadata to an existing task."""
        if metadata is None:
            raise ValueError("metadata must not be None")
        with self._lock:
            self._metadata[(metadata.project_id, metadata.number)] = metadata

    def get_task_metadata(self, project_id: str, number: int) -> Optional[TaskBoardMetadata]:
        with self._lock:
            return self._metadata.get((project_id, number))

    def tasks_in_column(
        self, project_id: str, status: str, skip: int = 0, take: int = 50
    ) -> List[PacaTask]:
        """(3.3.0) Paginated column read for lazy loading."""
        live = [t for t in self._tasks.list_tasks(project_id) if t.status == status]
        with self._lock:
            ordered = sorted(
                live, key=lambda t: self._get_or_create_metadata_locked(t.project_id, t.number).position_in_column
            )
        return ordered[skip : skip + take]

    def tasks_in_sprint(self, sprint_id: str) -> List[PacaTask]:
        """(3.3.0) Tasks bucketed by sprint, useful for the Scrumban board."""
        with self._lock:
            metas = [m for m in self._metadata.values() if m.sprint_id == sprint_id]
        result: List[PacaTask] = []
        for m in metas:
            task = next((t for t in self._tasks.list_tasks(m.project_id) if t.number == m.number), None)
            if task is not None:
                result.append(task)
        return result

    def create_sprint(
        self, id: str, project_id: str, name: str, goal: str, start: datetime, end: datetime
    ) -> PacaSprint:
        """(3.3.0) Create a sprint in Planning."""
        s = PacaSprint(id, project_id, name, goal, start, end, SprintState.Planning)
        with self._lock:
            self._sprints[id] = s
        return s

    def get_sprint(self, id: str) -> Optional[PacaSprint]:
        with self._lock:
            return self._sprints.get(id)

    def start_sprint(self, id: str) -> PacaSprint:
        return self._transition(id, SprintState.Active)

    def complete_sprint(self, id: str) -> PacaSprint:
        return self._transition(id, SprintState.Completed)

    def _transition(self, id: str, to: SprintState) -> PacaSprint:
        with self._lock:
            sprint = self._sprints.get(id)
            if sprint is None:
                raise RuntimeError(f"Sprint '{id}' not found.")
            updated = replace(sprint, state=to)
            self._sprints[id] = updated
            return updated

    def save_view(self, view: BoardView) -> None:
        """(3.3.0) Save a named view (filters + sort + visible fields)."""
        with self._lock:
            self._views[view.name] = view

    def get_view(self, name: str) -> Optional[BoardView]:
        with self._lock:
            return self._views.get(name)

    def list_views(self) -> List[BoardView]:
        with self._lock:
            return sorted(self._views.values(), key=lambda v: v.name)

    def _get_or_create_metadata_locked(self, project_id: str, number: int) -> TaskBoardMetadata:
        key = (project_id, number)
        meta = self._metadata.get(key)
        if meta is None:
            meta = TaskBoardMetadata(
                project_id, number, 0, 3, None, None, None, None, [], {}, 0
            )
            self._metadata[key] = meta
        return meta
