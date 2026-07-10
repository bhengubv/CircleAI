"""Server-wide counters + bounded admission gate.

Ports ``CircleAI.Inference.Server.Models.ServerCounters`` and
``CircleAI.Inference.Server.Hosting.AdmissionControl``. The C# counters are
lock-free interlocked longs; Python's GIL makes plain int increments atomic
enough for this coarse-grain telemetry, and a lock guards the admission gate.
"""
from __future__ import annotations

import threading
from datetime import datetime, timezone
from typing import Optional

__all__ = ["ServerCounters", "AdmissionControl", "AdmissionSlot"]


class ServerCounters:
    """Thread-safe counters for diagnostics rendering. Mirrors ``ServerCounters``."""

    __slots__ = ("_lock", "_total", "_rejected", "_failed", "_active", "_started_at")

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._total = 0
        self._rejected = 0
        self._failed = 0
        self._active = 0
        self._started_at = datetime.now(timezone.utc)

    @property
    def started_at(self) -> datetime:
        return self._started_at

    @property
    def total_requests(self) -> int:
        with self._lock:
            return self._total

    @property
    def rejected_requests(self) -> int:
        with self._lock:
            return self._rejected

    @property
    def failed_requests(self) -> int:
        with self._lock:
            return self._failed

    @property
    def active_requests(self) -> int:
        with self._lock:
            return self._active

    def account_admitted(self) -> None:
        with self._lock:
            self._total += 1
            self._active += 1

    def account_completed(self) -> None:
        with self._lock:
            self._active -= 1

    def account_rejected(self) -> None:
        with self._lock:
            self._rejected += 1

    def account_failed(self) -> None:
        with self._lock:
            self._failed += 1


class AdmissionSlot:
    """A held admission slot. Release exactly once (use as a context manager).

    Mirrors the C# ``AdmissionControl.Slot`` — idempotent release that both
    frees the gate and decrements the active counter.
    """

    __slots__ = ("_gate", "_counters", "_disposed")

    def __init__(self, gate: "AdmissionControl", counters: ServerCounters) -> None:
        self._gate = gate
        self._counters = counters
        self._disposed = False

    def release(self) -> None:
        if not self._disposed:
            self._disposed = True
            self._gate._release()  # noqa: SLF001 - intentional internal handoff
            self._counters.account_completed()

    def __enter__(self) -> "AdmissionSlot":
        return self

    def __exit__(self, *exc) -> None:
        self.release()


class AdmissionControl:
    """Bounded admission gate — at most ``max_concurrent_requests`` in flight.
    Mirrors ``CircleAI.Inference.Server.Hosting.AdmissionControl``.

    :meth:`try_enter` returns an :class:`AdmissionSlot` (release via ``with`` or
    ``.release()``) or ``None`` when saturated — the endpoint maps ``None`` to
    HTTP 503.
    """

    __slots__ = ("_lock", "_available", "_max", "_counters")

    def __init__(self, options, counters: ServerCounters) -> None:
        if options is None:
            raise ValueError("options is required")
        if counters is None:
            raise ValueError("counters is required")
        self._lock = threading.Lock()
        self._max = max(1, options.max_concurrent_requests)
        self._available = self._max
        self._counters = counters

    @property
    def max_concurrent_requests(self) -> int:
        return self._max

    def try_enter(self) -> Optional[AdmissionSlot]:
        with self._lock:
            if self._available > 0:
                self._available -= 1
                admitted = True
            else:
                admitted = False
        if admitted:
            self._counters.account_admitted()
            return AdmissionSlot(self, self._counters)
        self._counters.account_rejected()
        return None

    def _release(self) -> None:
        with self._lock:
            if self._available < self._max:
                self._available += 1
