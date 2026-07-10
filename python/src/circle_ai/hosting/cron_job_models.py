"""CronJobModels — port of CircleAI.Hosting.CronJobModels.

Domain models for B! scheduled tasks (Track 3). These types are intentionally
free of any external dependencies. Mirrors ``DeliveryTarget``, ``CronJobState``
and the ``CronJob`` record.
"""
from __future__ import annotations

import dataclasses
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import Optional

__all__ = ["DeliveryTarget", "CronJobState", "CronJob"]


class DeliveryTarget(IntEnum):
    """Delivery channel for a scheduled job's output. Mirrors ``DeliveryTarget``.

    Ordinals follow the C# declaration order (implicit 0..4).
    """

    LOCAL = 0
    """Deliver via in-process IAIObserver callback."""

    PUSH = 1
    """Deliver via push notification (requires IPushNotificationSender)."""

    TELEGRAM = 2
    """Deliver as a Telegram message (requires webhook config)."""

    EMAIL = 3
    """Deliver via email (requires SMTP config)."""

    CUSTOM = 4
    """Caller handles delivery via custom callback."""


class CronJobState(IntEnum):
    """State of a scheduled job's last execution. Mirrors ``CronJobState``."""

    PENDING = 0
    """Job has never run."""

    RUNNING = 1
    """Job is currently executing."""

    SUCCEEDED = 2
    """Last run completed without error."""

    FAILED = 3
    """Last run threw an exception or the model returned an error."""

    PAUSED = 4
    """Job has been manually paused and will not fire until re-enabled."""


@dataclass(frozen=True, slots=True)
class CronJob:
    """A named, recurring B! task with a cron schedule. Mirrors the C#
    ``CronJob`` record. Immutable — use :meth:`with_` to derive a modified copy
    (the C# uses ``with`` expressions).
    """

    id: str
    name: str
    prompt: str
    cron_expression: str
    delivery: DeliveryTarget
    last_run_utc: Optional[datetime] = None
    next_run_utc: Optional[datetime] = None
    state: CronJobState = CronJobState.PENDING
    is_enabled: bool = True

    def with_(self, **changes: object) -> "CronJob":
        """Return a copy with the given fields replaced. Python equivalent of a
        C# ``record with`` expression.
        """
        return dataclasses.replace(self, **changes)  # type: ignore[arg-type]
