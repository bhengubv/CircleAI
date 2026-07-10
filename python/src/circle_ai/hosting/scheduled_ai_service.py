"""ScheduledAIService — port of CircleAI.Hosting.ScheduledAIService.

Background polling service that fires due B! cron jobs. Checks the store every
30 seconds, runs each due job via :meth:`IAIService.ask_async`, updates the
job's state + next-run, and notifies subscribers via the ``on_job_completed``
event so the host can route delivery (push, email, Telegram, …).

Delivery routing is intentionally left to the host — this SDK has no dependency
on platform-specific notification libraries. Mirrors ``ScheduledAIService`` and
``JobCompletedEventArgs``.
"""
from __future__ import annotations

import asyncio
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Callable, List, Optional

from .cron_job_models import CronJob, CronJobState
from .cron_schedule_parser import CronScheduleParser
from .scheduled_task_store import IScheduledTaskStore

__all__ = ["JobCompletedEventArgs", "ScheduledAIService"]

# Poll interval in seconds — matches the C# 30-second cadence.
_POLL_INTERVAL_SECONDS = 30.0

# Subscriber callback signature: receives the completed-job event.
JobCompletedHandler = Callable[["JobCompletedEventArgs"], None]


@dataclass(frozen=True, slots=True)
class JobCompletedEventArgs:
    """Event data emitted when a scheduled job finishes (success or failure).
    Mirrors ``JobCompletedEventArgs``.
    """

    job: CronJob
    """The job that was executed (with updated state fields)."""

    response: str
    """The AI response text, or an empty string on failure."""

    error: Optional[BaseException]
    """Non-null when execution failed."""


class ScheduledAIService:
    """Runs a background loop that polls :class:`IScheduledTaskStore` for due
    :class:`CronJob` records every 30 seconds, executes them via
    :meth:`IAIService.ask_async`, and raises ``on_job_completed``. Mirrors
    ``ScheduledAIService``.

    ``poll_interval_seconds`` is injectable so tests can drive a fast loop or
    (preferably) call :meth:`process_due_jobs_async` directly.
    """

    __slots__ = (
        "_butler",
        "_store",
        "_poll_interval",
        "_handlers",
        "_loop_task",
        "_stopping",
    )

    def __init__(
        self,
        butler,
        store: IScheduledTaskStore,
        poll_interval_seconds: float = _POLL_INTERVAL_SECONDS,
    ) -> None:
        if butler is None:
            raise ValueError("butler is required")
        if store is None:
            raise ValueError("store is required")
        self._butler = butler
        self._store = store
        self._poll_interval = poll_interval_seconds
        self._handlers: List[JobCompletedHandler] = []
        self._loop_task: Optional[asyncio.Task] = None
        self._stopping: Optional[asyncio.Event] = None

    # ── Event subscription (C# `event OnJobCompleted`) ─────────────────────

    def add_job_completed_handler(self, handler: JobCompletedHandler) -> None:
        """Subscribe to job-completion notifications. Mirrors ``+= OnJobCompleted``."""
        if handler is None:
            raise ValueError("handler is required")
        self._handlers.append(handler)

    def remove_job_completed_handler(self, handler: JobCompletedHandler) -> None:
        """Unsubscribe. Mirrors ``-= OnJobCompleted``."""
        try:
            self._handlers.remove(handler)
        except ValueError:
            pass

    # ── Lifecycle ──────────────────────────────────────────────────────────

    async def start_async(self) -> None:
        """Start the background polling loop. No-op when already running.
        Mirrors ``StartAsync``.
        """
        if self._loop_task is not None and not self._loop_task.done():
            return
        self._stopping = asyncio.Event()
        self._loop_task = asyncio.ensure_future(self._run_loop_async(self._stopping))

    async def stop_async(self) -> None:
        """Signal the polling loop to stop and wait for it to exit. Mirrors
        ``StopAsync``.
        """
        if self._stopping is None:
            return
        self._stopping.set()
        if self._loop_task is not None:
            try:
                await self._loop_task
            except asyncio.CancelledError:  # pragma: no cover - defensive
                pass
        self._loop_task = None

    async def dispose_async(self) -> None:
        """Async-dispose — stops the loop. Mirrors ``DisposeAsync``."""
        await self.stop_async()

    # ── Core loop ──────────────────────────────────────────────────────────

    async def _run_loop_async(self, stopping: asyncio.Event) -> None:
        while not stopping.is_set():
            try:
                await self.process_due_jobs_async()
            except Exception:  # noqa: BLE001 - loop must not die on a poll error
                pass

            # Delay one poll interval, or bail early if stop is signalled.
            try:
                await asyncio.wait_for(stopping.wait(), timeout=self._poll_interval)
            except asyncio.TimeoutError:
                continue  # interval elapsed — poll again
            # stop signalled during the wait
            break

    async def process_due_jobs_async(self, ct: object = None) -> None:
        """Fetch and execute all currently-due jobs. Public for direct
        test-driving; the loop calls this internally. Mirrors ``ProcessDueJobsAsync``.
        """
        due_jobs = await self._store.get_due_jobs_async()
        if len(due_jobs) == 0:
            return
        for job in due_jobs:
            await self._execute_job_async(job)

    async def _execute_job_async(self, job: CronJob) -> None:
        now = datetime.now(timezone.utc)

        # Mark as Running.
        running = job.with_(state=CronJobState.RUNNING)
        await self._store.upsert_async(running)

        response = ""
        error: Optional[BaseException] = None

        try:
            response = await self._butler.ask_async(job.prompt)
        except Exception as ex:  # noqa: BLE001 - a job failure is not fatal
            error = ex

        next_run = self._compute_next_run(job.cron_expression, now)
        updated_state = CronJobState.SUCCEEDED if error is None else CronJobState.FAILED

        updated = job.with_(
            last_run_utc=now,
            next_run_utc=next_run,
            state=updated_state,
        )

        try:
            await self._store.upsert_async(updated)
        except Exception:  # noqa: BLE001 - best-effort persist
            pass

        # Fire event on best-effort basis — subscriber errors must not crash.
        for handler in list(self._handlers):
            try:
                handler(JobCompletedEventArgs(updated, response, error))
            except Exception:  # noqa: BLE001
                pass

    # ── Helpers ────────────────────────────────────────────────────────────

    @staticmethod
    def _compute_next_run(cron_expression: str, after: datetime) -> Optional[datetime]:
        try:
            return CronScheduleParser.get_next_occurrence(cron_expression, after)
        except Exception:  # noqa: BLE001 - invalid expr → no next run
            return None
