# companion/proactive/null_implementations.py
#
# Safe null defaults + in-memory source + delegate runner. Ported from
# CircleAI.Companion.Proactive (NullImplementations.cs) — the C# reference.

from __future__ import annotations

import threading
from typing import Awaitable, Callable, Dict, List, Mapping, Optional, Sequence, Tuple

from .contracts import IProactiveTaskRunner, IProactiveTaskSource
from .primitives import (
    ProactiveTask,
    ProactiveTaskLoadError,
    ProactiveTaskRunResult,
)


class NullProactiveTaskSource(IProactiveTaskSource):
    """Empty source — no tasks, no errors.

    Mirrors ``CircleAI.Companion.Proactive.NullProactiveTaskSource``.
    """

    _instance: Optional["NullProactiveTaskSource"] = None

    @property
    def backend_id(self) -> str:
        return "null"

    async def get_tasks_async(self, *, ct: Optional[object] = None) -> Sequence[ProactiveTask]:
        return []

    async def get_errors_async(
        self, *, ct: Optional[object] = None
    ) -> Sequence[ProactiveTaskLoadError]:
        return []


# Singleton instance (mirrors the C# ``Instance`` field).
NullProactiveTaskSource._instance = NullProactiveTaskSource()
NullProactiveTaskSource.Instance = NullProactiveTaskSource._instance  # type: ignore[attr-defined]


class NullProactiveTaskRunner(IProactiveTaskRunner):
    """Fail-closed runner — reports every run as a "no runner registered" failure.

    Mirrors ``CircleAI.Companion.Proactive.NullProactiveTaskRunner``. Fail-closed
    so a host that forgot to wire a real runner notices on first scheduled fire
    rather than silently doing nothing.
    """

    _instance: Optional["NullProactiveTaskRunner"] = None

    @property
    def backend_id(self) -> str:
        return "null"

    async def run_async(
        self,
        task: ProactiveTask,
        variables: Optional[Mapping[str, str]] = None,
        *,
        ct: Optional[object] = None,
    ) -> ProactiveTaskRunResult:
        return ProactiveTaskRunResult(
            task_id=task.id,
            success=False,
            failure_message="No IProactiveTaskRunner registered; using NullProactiveTaskRunner.",
        )


NullProactiveTaskRunner._instance = NullProactiveTaskRunner()
NullProactiveTaskRunner.Instance = NullProactiveTaskRunner._instance  # type: ignore[attr-defined]


class InMemoryProactiveTaskSource(IProactiveTaskSource):
    """In-memory source for testing + simple consumers.

    Mirrors ``CircleAI.Companion.Proactive.InMemoryProactiveTaskSource``. Keyed by
    (source_context, id) — both compared case-insensitively — so multi-tenant
    hosts can hold the same task id in two contexts without collision.
    ``source_context`` defaults to "" when ``None``.
    """

    __slots__ = ("_gate", "_by_key", "_errors")

    def __init__(self) -> None:
        self._gate = threading.Lock()
        # (ctx_lower, id_lower) -> ProactiveTask
        self._by_key: Dict[Tuple[str, str], ProactiveTask] = {}
        self._errors: List[ProactiveTaskLoadError] = []

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def upsert(self, task: ProactiveTask) -> None:
        if task is None:
            raise ValueError("task required")
        with self._gate:
            self._by_key[self._key(task)] = task

    def remove(self, id: str, source_context: Optional[str] = None) -> bool:
        if id is None or len(id.strip()) == 0:
            raise ValueError("id required")
        with self._gate:
            key = ((source_context or "").lower(), id.lower())
            if key in self._by_key:
                del self._by_key[key]
                return True
            return False

    def clear(self) -> None:
        with self._gate:
            self._by_key.clear()
            self._errors.clear()

    def record_error(self, error: ProactiveTaskLoadError) -> None:
        if error is None:
            raise ValueError("error required")
        with self._gate:
            self._errors.append(error)

    async def get_tasks_async(self, *, ct: Optional[object] = None) -> Sequence[ProactiveTask]:
        with self._gate:
            return list(self._by_key.values())

    async def get_errors_async(
        self, *, ct: Optional[object] = None
    ) -> Sequence[ProactiveTaskLoadError]:
        with self._gate:
            return list(self._errors)

    @staticmethod
    def _key(task: ProactiveTask) -> Tuple[str, str]:
        return ((task.source_context or "").lower(), task.id.lower())


DelegateHandler = Callable[
    [ProactiveTask, Optional[Mapping[str, str]], Optional[object]],
    Awaitable[ProactiveTaskRunResult],
]


class DelegateProactiveTaskRunner(IProactiveTaskRunner):
    """Runner that hands every task off to a host-supplied coroutine.

    Mirrors ``CircleAI.Companion.Proactive.DelegateProactiveTaskRunner``.
    """

    __slots__ = ("_handler",)

    def __init__(self, handler: DelegateHandler) -> None:
        if handler is None:
            raise ValueError("handler required")
        self._handler = handler

    @property
    def backend_id(self) -> str:
        return "delegate"

    async def run_async(
        self,
        task: ProactiveTask,
        variables: Optional[Mapping[str, str]] = None,
        *,
        ct: Optional[object] = None,
    ) -> ProactiveTaskRunResult:
        return await self._handler(task, variables, ct)


__all__ = [
    "NullProactiveTaskSource",
    "NullProactiveTaskRunner",
    "InMemoryProactiveTaskSource",
    "DelegateProactiveTaskRunner",
]
