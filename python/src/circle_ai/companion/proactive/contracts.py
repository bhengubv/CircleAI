# companion/proactive/contracts.py
#
# Proactive scheduling contract surface. Ported from
# CircleAI.Companion.Proactive (Contracts.cs) — the C# reference. Three
# interfaces split cleanly so consumers can replace one without touching the
# others:
#
#   IProactiveTaskSource — where do tasks come from? (vault FS, DB, in-memory …)
#   IProactiveTaskRunner — how do we execute one? (workflow engine, delegate …)
#   IProactiveScheduler  — when do they fire? (cron tick loop + last-run tracking)

from __future__ import annotations

from abc import ABC, abstractmethod
from datetime import datetime
from typing import Mapping, Optional, Sequence

from .primitives import (
    ProactiveTask,
    ProactiveTaskLoadError,
    ProactiveTaskRunResult,
)


class IProactiveTaskSource(ABC):
    """Where the active set of tasks comes from.

    Mirrors ``CircleAI.Companion.Proactive.IProactiveTaskSource``.
    """

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "vault-fs", "in-memory", "null"."""
        ...

    @abstractmethod
    async def get_tasks_async(self, *, ct: Optional[object] = None) -> Sequence[ProactiveTask]:
        """Snapshot the current set of tasks."""
        ...

    @abstractmethod
    async def get_errors_async(
        self, *, ct: Optional[object] = None
    ) -> Sequence[ProactiveTaskLoadError]:
        """Any parse / load failures surfaced from the last refresh."""
        ...


class IProactiveTaskRunner(ABC):
    """Executes one task.

    Mirrors ``CircleAI.Companion.Proactive.IProactiveTaskRunner``.
    """

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification — "workflow-engine", "delegate", "null"."""
        ...

    @abstractmethod
    async def run_async(
        self,
        task: ProactiveTask,
        variables: Optional[Mapping[str, str]] = None,
        *,
        ct: Optional[object] = None,
    ) -> ProactiveTaskRunResult:
        """Execute one task. ``variables`` carry trigger-time context."""
        ...


class IProactiveScheduler(ABC):
    """The scheduling loop — owns cron parsing, last-run tracking, event dispatch.

    Mirrors ``CircleAI.Companion.Proactive.IProactiveScheduler``.
    """

    @property
    @abstractmethod
    def backend_id(self) -> str:
        """Backend self-identification."""
        ...

    @property
    @abstractmethod
    def tasks(self) -> Sequence[ProactiveTask]:
        """Current snapshot — populated by :meth:`refresh_async`."""
        ...

    @property
    @abstractmethod
    def load_errors(self) -> Sequence[ProactiveTaskLoadError]:
        """Any load errors from the source."""
        ...

    @abstractmethod
    def get_next_run(self, task: ProactiveTask, after: datetime) -> Optional[datetime]:
        """Next cron firing for a task; ``None`` for non-cron / unparseable triggers."""
        ...

    @abstractmethod
    async def refresh_async(self, *, ct: Optional[object] = None) -> None:
        """Re-snapshot tasks from the source, dropping stale last-run state."""
        ...

    @abstractmethod
    async def tick_async(self, now: datetime, *, ct: Optional[object] = None) -> None:
        """Run every task whose cron next-run is at-or-before ``now`` and unfired for the minute."""
        ...

    @abstractmethod
    async def dispatch_event_async(
        self,
        event_name: str,
        variables: Optional[Mapping[str, str]] = None,
        *,
        ct: Optional[object] = None,
    ) -> None:
        """Fire every event-triggered task matching the event name."""
        ...

    @abstractmethod
    async def run_by_id_async(
        self,
        id: str,
        variables: Optional[Mapping[str, str]] = None,
        *,
        ct: Optional[object] = None,
    ) -> ProactiveTaskRunResult:
        """One-shot manual run by task id."""
        ...


__all__ = [
    "IProactiveTaskSource",
    "IProactiveTaskRunner",
    "IProactiveScheduler",
]
