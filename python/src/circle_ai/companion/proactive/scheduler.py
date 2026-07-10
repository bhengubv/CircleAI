# companion/proactive/scheduler.py
#
# Generic IProactiveScheduler — owns cron parsing, last-run tracking, refresh,
# and event dispatch. Ported from CircleAI.Companion.Proactive
# (ProactiveScheduler.cs) — the C# reference. Calls into a host-supplied
# IProactiveTaskSource (what tasks exist) and IProactiveTaskRunner (how to
# execute one).
#
# Per-context (source_context) last-run tracking is preserved so multi-tenant
# hosts keep tenants' schedules separate. Context key = ``source_context`` or ""
# if None, compared case-insensitively.

from __future__ import annotations

import threading
from datetime import datetime, timedelta, timezone
from typing import Dict, List, Mapping, Optional, Sequence

from .contracts import IProactiveScheduler, IProactiveTaskRunner, IProactiveTaskSource
from .cron_expression import CronExpression
from .primitives import ProactiveTask, ProactiveTaskLoadError, ProactiveTaskRunResult

# Sentinel for "never run" — the analogue of C#'s ``DateTimeOffset.MinValue``.
_MIN_TIME = datetime(1, 1, 1, tzinfo=timezone.utc)


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class ProactiveScheduler(IProactiveScheduler):
    """Default :class:`IProactiveScheduler`. Thread-safe (singleton-safe).

    Mirrors ``CircleAI.Companion.Proactive.ProactiveScheduler``. Refresh / tick is
    the contract; a host ticks every minute.
    """

    __slots__ = ("_source", "_runner", "_gate", "_tasks", "_errors", "_last_runs", "_now")

    def __init__(
        self,
        source: IProactiveTaskSource,
        runner: IProactiveTaskRunner,
        *,
        now_provider=None,
    ) -> None:
        if source is None:
            raise ValueError("source required")
        if runner is None:
            raise ValueError("runner required")
        self._source = source
        self._runner = runner
        self._gate = threading.Lock()
        self._tasks: List[ProactiveTask] = []
        self._errors: List[ProactiveTaskLoadError] = []
        # ctx_key -> { task_id -> last_run }
        self._last_runs: Dict[str, Dict[str, datetime]] = {}
        self._now = now_provider or _utc_now

    @property
    def backend_id(self) -> str:
        return "default"

    @property
    def tasks(self) -> Sequence[ProactiveTask]:
        with self._gate:
            return list(self._tasks)

    @property
    def load_errors(self) -> Sequence[ProactiveTaskLoadError]:
        with self._gate:
            return list(self._errors)

    def get_next_run(self, task: ProactiveTask, after: datetime) -> Optional[datetime]:
        if task.trigger.cron is None:
            return None
        try:
            expr = CronExpression.parse(task.trigger.cron)
            return expr.get_next_occurrence(after)
        except Exception:  # noqa: BLE001 — parse/spin failures -> None
            return None

    async def refresh_async(self, *, ct: Optional[object] = None) -> None:
        snapshot = await self._source.get_tasks_async(ct=ct)
        errors = await self._source.get_errors_async(ct=ct)
        with self._gate:
            self._tasks = list(snapshot)
            self._errors = list(errors)
            # Drop last-run state for (context, id) pairs the source no longer
            # reports — prevents unbounded growth as tasks come and go.
            live = {
                (self._context_key(t.source_context), t.id.lower())
                for t in self._tasks
            }
            for ctx_key in list(self._last_runs.keys()):
                ids = self._last_runs[ctx_key]
                for tid in list(ids.keys()):
                    if (ctx_key, tid.lower()) not in live:
                        del ids[tid]
                if len(ids) == 0:
                    del self._last_runs[ctx_key]

    async def tick_async(self, now: datetime, *, ct: Optional[object] = None) -> None:
        with self._gate:
            candidates = [t for t in self._tasks if t.trigger.cron is not None]

        for task in candidates:
            _raise_if_cancelled(ct)
            ctx_key = self._context_key(task.source_context)
            with self._gate:
                mp = self._last_runs.get(ctx_key)
                if mp is None:
                    mp = {}
                    self._last_runs[ctx_key] = mp
                last_run = mp.get(task.id, _MIN_TIME)
            try:
                expr = CronExpression.parse(task.trigger.cron)  # type: ignore[arg-type]
                anchor = now - timedelta(minutes=1) if last_run == _MIN_TIME else last_run
                nxt = expr.get_next_occurrence(anchor)
                if nxt <= now:
                    await self._runner.run_async(task, None, ct=ct)
                    self._mark_run(task, now)
            except Exception:  # noqa: BLE001 — parse error already surfaced; skip
                continue

    async def dispatch_event_async(
        self,
        event_name: str,
        variables: Optional[Mapping[str, str]] = None,
        *,
        ct: Optional[object] = None,
    ) -> None:
        if event_name is None or len(event_name.strip()) == 0:
            raise ValueError("event_name required")
        low = event_name.lower()
        with self._gate:
            matched = [
                t
                for t in self._tasks
                if t.trigger.on_event is not None and t.trigger.on_event.lower() == low
            ]
        for task in matched:
            _raise_if_cancelled(ct)
            await self._runner.run_async(task, variables, ct=ct)
            self._mark_run(task, self._now())

    async def run_by_id_async(
        self,
        id: str,
        variables: Optional[Mapping[str, str]] = None,
        *,
        ct: Optional[object] = None,
    ) -> ProactiveTaskRunResult:
        if id is None or len(id.strip()) == 0:
            raise ValueError("id required")
        low = id.lower()
        with self._gate:
            task = next((t for t in self._tasks if t.id.lower() == low), None)
        if task is None:
            return ProactiveTaskRunResult(id, success=False, failure_message=f"No task with id '{id}'.")
        result = await self._runner.run_async(task, variables, ct=ct)
        self._mark_run(task, self._now())
        return result

    def _mark_run(self, task: ProactiveTask, when: datetime) -> None:
        ctx_key = self._context_key(task.source_context)
        with self._gate:
            mp = self._last_runs.get(ctx_key)
            if mp is None:
                mp = {}
                self._last_runs[ctx_key] = mp
            mp[task.id] = when

    @staticmethod
    def _context_key(source_context: Optional[str]) -> str:
        # ContextKey folds None -> "". The C# dict is OrdinalIgnoreCase-keyed;
        # lowercasing the key gives the same collapse for lookups here.
        return (source_context or "").lower()


def _raise_if_cancelled(ct: Optional[object]) -> None:
    """Cooperative-cancellation check — honours a token exposing ``is_cancelled``/
    ``cancelled`` or an ``asyncio.Event``-like ``is_set``."""
    if ct is None:
        return
    for attr in ("is_cancellation_requested", "is_cancelled", "cancelled"):
        val = getattr(ct, attr, None)
        if val is True:
            raise _cancelled_error()
    is_set = getattr(ct, "is_set", None)
    if callable(is_set) and is_set():
        raise _cancelled_error()


def _cancelled_error() -> BaseException:
    import asyncio

    return asyncio.CancelledError()


__all__ = ["ProactiveScheduler"]
