"""IScheduledTaskStore + InMemoryScheduledTaskStore — ports of
CircleAI.Hosting.IScheduledTaskStore and InMemoryScheduledTaskStore.

Persistence contract for B! cron jobs (Track 3) plus the thread-safe in-memory
default. All operations are async and thread-safe.
"""
from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from datetime import datetime, timezone
from typing import Dict, List, Optional

from .cron_job_models import CronJob

__all__ = ["IScheduledTaskStore", "InMemoryScheduledTaskStore"]


class IScheduledTaskStore(ABC):
    """Persistence abstraction for :class:`CronJob` records. Mirrors
    ``IScheduledTaskStore``. Implementations may be in-memory, SQLite, etc.
    """

    @abstractmethod
    async def list_async(self, ct: object = None) -> List[CronJob]:
        """Return every registered job, regardless of enabled/disabled state."""
        ...

    @abstractmethod
    async def get_async(self, id: str, ct: object = None) -> Optional[CronJob]:
        """Return the job with ``id``, or ``None`` if not found."""
        ...

    @abstractmethod
    async def upsert_async(self, job: CronJob, ct: object = None) -> CronJob:
        """Insert or replace the job identified by ``CronJob.id``; return it."""
        ...

    @abstractmethod
    async def delete_async(self, id: str, ct: object = None) -> None:
        """Remove the job with ``id``. No-op if it does not exist."""
        ...

    @abstractmethod
    async def get_due_jobs_async(self, ct: object = None) -> List[CronJob]:
        """Return all enabled jobs whose ``next_run_utc`` is in the past
        (``<= utcnow``).
        """
        ...


class InMemoryScheduledTaskStore(IScheduledTaskStore):
    """Thread-safe, in-memory :class:`IScheduledTaskStore`. All state is lost
    when the process exits. Mirrors ``InMemoryScheduledTaskStore``.
    """

    __slots__ = ("_store", "_gate")

    def __init__(self) -> None:
        self._store: Dict[str, CronJob] = {}
        self._gate = threading.RLock()

    async def list_async(self, ct: object = None) -> List[CronJob]:
        with self._gate:
            return list(self._store.values())

    async def get_async(self, id: str, ct: object = None) -> Optional[CronJob]:
        if id is None or not id.strip():
            raise ValueError("id is required")
        with self._gate:
            return self._store.get(id)

    async def upsert_async(self, job: CronJob, ct: object = None) -> CronJob:
        if job is None:
            raise ValueError("job is required")
        with self._gate:
            self._store[job.id] = job
        return job

    async def delete_async(self, id: str, ct: object = None) -> None:
        if id is None or not id.strip():
            raise ValueError("id is required")
        with self._gate:
            self._store.pop(id, None)

    async def get_due_jobs_async(self, ct: object = None) -> List[CronJob]:
        now = datetime.now(timezone.utc)
        with self._gate:
            return [
                j
                for j in self._store.values()
                if j.is_enabled
                and j.next_run_utc is not None
                and _as_utc(j.next_run_utc) <= now
            ]


def _as_utc(dt: datetime) -> datetime:
    if dt.tzinfo is None:
        return dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)
