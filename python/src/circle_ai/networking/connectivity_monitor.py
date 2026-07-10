# connectivity_monitor.py
#
# InMemoryConnectivityMonitor — a working, deterministic IConnectivityMonitor.
# The live network state is driven by an injected feed (a real monitor would
# poll the OS); here the host calls set_context(...) to publish a new snapshot.
#
# watch_async fan-out obeys the Wave-1 concurrency rules:
#   • Each watcher's queue is registered SYNCHRONOUSLY by watch_async() before
#     any await, and the CURRENT snapshot is seeded into it at registration —
#     so a watcher started right before a set_context cannot miss an update.
#   • Publishing snapshots the watcher set, RELEASES the lock, THEN enqueues.
#   • Watcher queues are UNBOUNDED so publish never blocks.

from __future__ import annotations

import asyncio
import threading
from typing import AsyncIterator, List, Optional, Set

from .interfaces import IConnectivityMonitor
from .network_types import ConnectivityState, NetworkContext

_CLOSED = object()


class InMemoryConnectivityMonitor(IConnectivityMonitor):
    """`IConnectivityMonitor` whose state is published by the host.

    Construct with an initial :class:`NetworkContext` (defaults to
    :meth:`NetworkContext.offline`). Call :meth:`set_context` to transition;
    every live :meth:`watch_async` iterator receives the new snapshot.
    """

    def __init__(self, initial: Optional[NetworkContext] = None) -> None:
        self._context: NetworkContext = initial or NetworkContext.offline()
        self._lock = threading.Lock()
        self._watchers: Set["asyncio.Queue[object]"] = set()

    @property
    def current_state(self) -> ConnectivityState:
        return self._context.state

    def get_snapshot(self) -> NetworkContext:
        return self._context

    def set_context(self, context: NetworkContext) -> None:
        """Publish a new snapshot to all watchers.

        Snapshot the watcher set under the lock (also swapping in the new
        context so late-joiners seed from it), release, then enqueue.
        """
        if context is None:
            raise ValueError("context required")
        with self._lock:
            self._context = context
            watchers = list(self._watchers)
        for q in watchers:
            q.put_nowait(context)

    def close(self) -> None:
        """End every live watcher iterator."""
        with self._lock:
            watchers = list(self._watchers)
            self._watchers.clear()
        for q in watchers:
            q.put_nowait(_CLOSED)

    def watch_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[NetworkContext]:
        # Register synchronously and seed the current snapshot atomically under
        # the lock, so the watcher both (a) yields the state at subscribe time
        # and (b) cannot miss a concurrent set_context.
        q: "asyncio.Queue[object]" = asyncio.Queue()
        with self._lock:
            q.put_nowait(self._context)
            self._watchers.add(q)

        async def _iter() -> AsyncIterator[NetworkContext]:
            try:
                while True:
                    item = await q.get()
                    if item is _CLOSED:
                        return
                    yield item  # type: ignore[misc]
            finally:
                with self._lock:
                    self._watchers.discard(q)

        return _iter()

    @property
    def watcher_count(self) -> int:
        """Number of live watchers (observability / test aid)."""
        with self._lock:
            return len(self._watchers)
