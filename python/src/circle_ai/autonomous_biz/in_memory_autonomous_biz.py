# in_memory_autonomous_biz.py
#
# Port of CircleAI.AutonomousBiz InMemoryAutonomousBiz.cs (C# — the EXACT spec).
#
# (3.3.0) Real in-memory treasury / revenue-loop / decision-log implementations.
# Treasury maintains a running balance from revenue events; revenue loop is a
# fan-out pub/sub with a kept history; decision log is an append-only list.
#
# Concurrency: the C# Publish snapshots subscribers under the lock and fires
# them OUTSIDE it (a subscriber that re-enters Subscribe/Dispose cannot
# self-deadlock). C# fires each async handler fire-and-forget (`_ = s(e)`) and
# swallows exceptions. We reproduce that: snapshot under the lock, release, then
# schedule each coroutine on the running loop when one exists (fire-and-forget),
# else run it to completion synchronously. Handler exceptions are swallowed.

from __future__ import annotations

import asyncio
import threading
from datetime import datetime, timezone
from decimal import Decimal
from typing import List, Optional

from .contracts import (
    AutonomousDecision,
    IDecisionLog,
    IRevenueLoop,
    ITreasury,
    RevenueEvent,
    RevenueHandler,
    TreasurySnapshot,
)

# DateTimeOffset.MinValue — used as the "read everything" floor.
_MIN_UTC = datetime(1, 1, 1, tzinfo=timezone.utc)


class _Token:
    """IDisposable subscription token. Idempotent dispose; also a context
    manager so ``with loop.subscribe(h):`` works."""

    def __init__(self, owner: "InMemoryRevenueLoop", handler: RevenueHandler) -> None:
        self._owner = owner
        self._handler = handler
        self._disposed = False
        self._lock = threading.Lock()

    def dispose(self) -> None:
        with self._lock:
            if self._disposed:
                return
            self._disposed = True
        self._owner._remove(self._handler)

    def __enter__(self) -> "_Token":
        return self

    def __exit__(self, *exc: object) -> None:
        self.dispose()


def _fire(handler: RevenueHandler, e: RevenueEvent) -> None:
    """Fire an async revenue handler fire-and-forget, swallowing exceptions —
    mirrors the C# ``try { _ = s(e); } catch { ... }``."""
    try:
        coro = handler(e)
    except Exception:
        return
    if coro is None:
        return
    try:
        loop = asyncio.get_running_loop()
    except RuntimeError:
        loop = None
    if loop is not None:
        loop.create_task(_guard(coro))
    else:
        try:
            asyncio.run(_guard(coro))
        except Exception:
            pass


async def _guard(coro) -> None:
    try:
        await coro
    except Exception:
        # An unhealthy revenue subscriber must not corrupt the loop.
        pass


class InMemoryRevenueLoop(IRevenueLoop):
    """(3.3.0) Fan-out revenue pub/sub with a kept history."""

    def __init__(self) -> None:
        self._history: List[RevenueEvent] = []
        self._subs: List[RevenueHandler] = []
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def publish(self, e: RevenueEvent) -> None:
        if e is None:
            raise ValueError("e must not be None")
        with self._lock:
            self._history.append(e)
            snap = list(self._subs)
        for s in snap:
            _fire(s, e)

    def subscribe(self, handler: RevenueHandler) -> _Token:
        if handler is None:
            raise ValueError("handler must not be None")
        with self._lock:
            self._subs.append(handler)
        return _Token(self, handler)

    async def read_async(self, since: datetime, ct: Optional[object] = None) -> List[RevenueEvent]:
        with self._lock:
            return [e for e in self._history if e.at_utc >= since]

    def _remove(self, handler: RevenueHandler) -> None:
        with self._lock:
            try:
                self._subs.remove(handler)
            except ValueError:
                pass


class InMemoryTreasury(ITreasury):
    """(3.3.0) Treasury — running balance summed from the revenue loop's
    currency-matched events."""

    def __init__(self, loop: IRevenueLoop, currency: str = "ZAR") -> None:
        if loop is None:
            raise ValueError("loop must not be None")
        self._loop = loop
        self._currency = currency

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def get_snapshot_async(self, ct: Optional[object] = None) -> TreasurySnapshot:
        events = await self._loop.read_async(_MIN_UTC, ct)
        bal = sum(
            (e.amount for e in events if e.currency.casefold() == self._currency.casefold()),
            Decimal(0),
        )
        return TreasurySnapshot(bal, self._currency, datetime.now(timezone.utc))


class InMemoryDecisionLog(IDecisionLog):
    """(3.3.0) Append-only decision log; reads newest-first with a limit."""

    def __init__(self) -> None:
        self._items: List[AutonomousDecision] = []
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def append_async(self, d: AutonomousDecision, ct: Optional[object] = None) -> None:
        if d is None:
            raise ValueError("d must not be None")
        with self._lock:
            self._items.append(d)

    async def read_async(self, limit: int = 100, ct: Optional[object] = None) -> List[AutonomousDecision]:
        if limit <= 0:
            raise ValueError("limit must be positive")
        with self._lock:
            ordered = sorted(self._items, key=lambda d: d.at_utc, reverse=True)
            return ordered[:limit]
