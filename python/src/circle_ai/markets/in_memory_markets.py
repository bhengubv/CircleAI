# in_memory_markets.py
#
# Port of CircleAI.Markets InMemoryMarkets.cs (C# — the EXACT spec).
#
# (3.3.0) Real in-memory market-data feed + instrument catalog + order router.
# The feed supports subscribe/broadcast quote pushes; the order router accepts
# and rejects based on simple rules (positive quantity, known instrument, valid
# limit price for limit orders).
#
# C# ConcurrentDictionary(StringComparer.OrdinalIgnoreCase) maps to a plain dict
# keyed by the case-folded symbol (so lookups are case-insensitive) that stores
# the original record. C# ValueTask methods map to async def. A monotonically
# increasing order id "ord-{n}" is minted from a lock-guarded counter.
#
# Publish is a *sync* fan-out: it snapshots the subscriber list under the gate
# and invokes each async handler OUTSIDE the gate (so a handler that re-enters
# subscribe/dispose cannot self-deadlock), scheduling the returned coroutine
# fire-and-forget and swallowing any exception — mirroring the C# `_ = s(q)` in a
# try/catch. This preserves the "a throwing subscriber never breaks Publish"
# contract of the C# feed.

from __future__ import annotations

import asyncio
import threading
from typing import Dict, List, Optional

from .contracts import (
    IDisposable,
    IInstrumentCatalog,
    IMarketDataFeed,
    IOrderRouter,
    Instrument,
    OrderRequest,
    OrderResult,
    OrderType,
    Quote,
    QuoteHandler,
)


class InMemoryInstrumentCatalog(IInstrumentCatalog):
    """Thread-safe in-memory :class:`IInstrumentCatalog` (case-insensitive
    symbol keys).
    """

    def __init__(self) -> None:
        self._items: Dict[str, Instrument] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def add(self, item: Instrument) -> None:
        if item is None:
            raise ValueError("item must not be None")
        with self._lock:
            self._items[item.symbol.casefold()] = item

    async def get_async(
        self, symbol: str, ct: Optional[object] = None
    ) -> Optional[Instrument]:
        if symbol is None or not symbol.strip():
            raise ValueError("symbol required")
        with self._lock:
            return self._items.get(symbol.casefold())

    async def search_async(
        self, query: str, top_k: int = 20, ct: Optional[object] = None
    ) -> List[Instrument]:
        if query is None:
            raise ValueError("query must not be None")
        if top_k <= 0:
            raise ValueError("top_k")
        q = query.casefold()
        with self._lock:
            hits = [i for i in self._items.values() if q in i.symbol.casefold()]
        # C# OrderBy(i => i.Symbol) — ordinal (case-sensitive) ascending.
        hits.sort(key=lambda i: i.symbol)
        return hits[:top_k]


class InMemoryMarketDataFeed(IMarketDataFeed):
    """Thread-safe in-memory :class:`IMarketDataFeed` with subscribe/broadcast."""

    def __init__(self) -> None:
        self._quotes: Dict[str, Quote] = {}
        self._subs: Dict[str, List[QuoteHandler]] = {}
        self._gate = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def publish(self, q: Quote) -> None:
        if q is None:
            raise ValueError("quote must not be None")
        key = q.symbol.casefold()
        with self._gate:
            self._quotes[key] = q
            snap = list(self._subs.get(key, ()))
        for handler in snap:
            _fire(handler, q)

    async def get_quote_async(
        self, symbol: str, ct: Optional[object] = None
    ) -> Optional[Quote]:
        if symbol is None or not symbol.strip():
            raise ValueError("symbol required")
        with self._gate:
            return self._quotes.get(symbol.casefold())

    def subscribe_quotes(self, symbol: str, handler: QuoteHandler) -> IDisposable:
        if symbol is None or not symbol.strip():
            raise ValueError("symbol required")
        if handler is None:
            raise ValueError("handler must not be None")
        key = symbol.casefold()
        with self._gate:
            self._subs.setdefault(key, []).append(handler)
        return _Subscription(self, key, handler)

    def _unsubscribe(self, key: str, handler: QuoteHandler) -> None:
        with self._gate:
            lst = self._subs.get(key)
            if lst is not None:
                try:
                    lst.remove(handler)
                except ValueError:
                    pass


class _Subscription(IDisposable):
    def __init__(
        self, owner: InMemoryMarketDataFeed, key: str, handler: QuoteHandler
    ) -> None:
        self._owner = owner
        self._key = key
        self._handler = handler
        self._disposed = False
        self._lock = threading.Lock()

    def dispose(self) -> None:
        with self._lock:
            if self._disposed:
                return
            self._disposed = True
        self._owner._unsubscribe(self._key, self._handler)


class InMemoryOrderRouter(IOrderRouter):
    """In-memory :class:`IOrderRouter` — accepts/rejects on simple rules."""

    def __init__(self, catalog: IInstrumentCatalog) -> None:
        if catalog is None:
            raise ValueError("catalog must not be None")
        self._catalog = catalog
        self._seq = 0
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def submit_async(
        self, req: OrderRequest, ct: Optional[object] = None
    ) -> OrderResult:
        if req is None:
            raise ValueError("req must not be None")
        if req.quantity <= 0:
            return OrderResult(self._next_id(), False, "Quantity must be positive")
        if req.type == OrderType.Limit and (
            req.limit_price is None or req.limit_price <= 0
        ):
            return OrderResult(
                self._next_id(), False, "Limit order requires positive LimitPrice"
            )
        inst = await self._catalog.get_async(req.symbol, ct)
        if inst is None:
            return OrderResult(self._next_id(), False, "Unknown symbol")
        return OrderResult(self._next_id(), True, None)

    def _next_id(self) -> str:
        with self._lock:
            self._seq += 1
            return f"ord-{self._seq}"


def _fire(handler: QuoteHandler, q: Quote) -> None:
    """Invoke an async quote handler fire-and-forget, swallowing any exception —
    mirroring the C# ``try { _ = s(q); } catch { ... }``. If a running event loop
    is present the coroutine is scheduled on it; otherwise it is driven to
    completion on a throwaway loop so a synchronous caller still delivers.
    """
    try:
        result = handler(q)
    except Exception:  # noqa: BLE001 — a throwing subscriber must not break publish
        return
    if not asyncio.iscoroutine(result):
        return
    try:
        loop = asyncio.get_running_loop()
    except RuntimeError:
        loop = None
    if loop is not None:
        task = loop.create_task(result)
        # Swallow a later handler exception so it never surfaces as an unretrieved
        # task exception (matches the C# fire-and-forget try/catch).
        task.add_done_callback(_drain_task_exception)
    else:
        try:
            asyncio.run(result)
        except Exception:  # noqa: BLE001
            return


def _drain_task_exception(task: "asyncio.Task[None]") -> None:
    if task.cancelled():
        return
    # Retrieving the exception marks it as handled.
    task.exception()
