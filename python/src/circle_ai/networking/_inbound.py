# _inbound.py
#
# InboundChannel — the Python analogue of C# System.Threading.Channels
# Channel.CreateUnbounded<NetworkPayload>() as used by the concrete transports
# (Bluetooth/Aether/etc.) for their inbound receive loop.
#
# The C# transports expose a ChannelWriter to their platform adapter and return
# reader.ReadAllAsync() from ReceiveAsync. This helper provides both halves as a
# single object with an unbounded fan-out so that:
#   • the adapter (or any producer) calls write()/try_complete() — the writer;
#   • each receive_async() iterator gets EVERY written payload — the reader.
#
# Concurrency (Wave-1 rules):
#   • read_all() registers its queue SYNCHRONOUSLY before any await, so a payload
#     written immediately after subscribe is never raced away.
#   • Queues are UNBOUNDED, so write() never blocks and payloads buffered before
#     a consumer drains are retained (matching an unbounded C# Channel).
#   • write() snapshots the subscriber set, RELEASES the lock, THEN enqueues —
#     a reader's finally-block deregister takes the same lock without deadlock.
#   • try_complete() ends every live reader (matching Writer.TryComplete()).

from __future__ import annotations

import asyncio
import threading
from typing import AsyncIterator, Generic, Set, TypeVar

T = TypeVar("T")

_CLOSED = object()


class InboundChannel(Generic[T]):
    """An unbounded, fan-out inbound channel — writer + multi-reader in one.

    Mirrors the role of ``Channel.CreateUnbounded<T>()`` in the C# transports,
    but delivers each written item to every active reader (fan-out) rather than
    to a single competing consumer, honouring the Wave-1 no-lost-message and
    no-teardown-deadlock rules.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._readers: Set["asyncio.Queue[object]"] = set()
        self._completed = False

    @property
    def is_completed(self) -> bool:
        with self._lock:
            return self._completed

    def write(self, item: T) -> bool:
        """Fan ``item`` out to every live reader. Returns False if the channel
        is already completed (matching ``ChannelWriter.TryWrite`` on a completed
        writer), True otherwise.
        """
        with self._lock:
            if self._completed:
                return False
            readers = list(self._readers)
        for q in readers:
            q.put_nowait(item)
        return True

    def try_complete(self) -> bool:
        """Complete the channel and end every live reader. Idempotent; returns
        False if already completed (matching ``ChannelWriter.TryComplete``).
        """
        with self._lock:
            if self._completed:
                return False
            self._completed = True
            readers = list(self._readers)
            self._readers.clear()
        for q in readers:
            q.put_nowait(_CLOSED)
        return True

    def read_all(self) -> AsyncIterator[T]:
        """Async-iterate every item written from now until completion — the C#
        ``reader.ReadAllAsync()``. Registers synchronously before any await.
        """
        q: "asyncio.Queue[object]" = asyncio.Queue()
        with self._lock:
            # If already completed, hand back an immediately-exhausted iterator.
            if self._completed:
                already_done = True
            else:
                already_done = False
                self._readers.add(q)

        async def _iter() -> AsyncIterator[T]:
            if already_done:
                return
            try:
                while True:
                    item = await q.get()
                    if item is _CLOSED:
                        return
                    yield item  # type: ignore[misc]
            finally:
                with self._lock:
                    self._readers.discard(q)

        return _iter()

    @property
    def reader_count(self) -> int:
        """Number of live readers (observability / test aid)."""
        with self._lock:
            return len(self._readers)
