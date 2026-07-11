"""test_markets_board.py — CircleAI.Markets port.

Covers OrderSide/OrderType enums, domain records, the async in-memory feed
(case-insensitive quote get, subscribe/broadcast fan-out, dispose unsubscribes,
throwing subscriber does not break publish), the searchable catalog, the
rules-based order router (positive-quantity / limit-price / known-symbol / accept
+ monotonic ids), and the fail-closed null defaults. C# is the exact spec.
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timezone
from decimal import Decimal

import pytest

from circle_ai.markets import (
    IDisposable,
    IInstrumentCatalog,
    IMarketDataFeed,
    IOrderRouter,
    Instrument,
    InMemoryInstrumentCatalog,
    InMemoryMarketDataFeed,
    InMemoryOrderRouter,
    NullInstrumentCatalog,
    NullMarketDataFeed,
    NullOrderRouter,
    OrderRequest,
    OrderResult,
    OrderSide,
    OrderType,
    Quote,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)
_GUID_EMPTY = "00000000-0000-0000-0000-000000000000"


def _q(symbol: str, last: str) -> Quote:
    return Quote(symbol, Decimal(last), Decimal(last), Decimal(last), _T0)


def test_enums_ordinals():
    assert (OrderSide.Buy, OrderSide.Sell) == (0, 1)
    assert (OrderType.Market, OrderType.Limit) == (0, 1)


def test_backends_are_contracts():
    cat = InMemoryInstrumentCatalog()
    assert isinstance(cat, IInstrumentCatalog)
    assert isinstance(InMemoryMarketDataFeed(), IMarketDataFeed)
    assert isinstance(InMemoryOrderRouter(cat), IOrderRouter)
    assert cat.backend_id == "in-memory"


async def test_catalog_get_case_insensitive_and_search():
    cat = InMemoryInstrumentCatalog()
    cat.add(Instrument("AAPL", "NASDAQ", "USD", "Equity"))
    cat.add(Instrument("ABBV", "NYSE", "USD", "Equity"))
    cat.add(Instrument("MSFT", "NASDAQ", "USD", "Equity"))
    assert (await cat.get_async("aapl")).exchange == "NASDAQ"  # case-insensitive
    hits = await cat.search_async("A")
    assert [i.symbol for i in hits] == ["AAPL", "ABBV"]  # ordinal ascending
    with pytest.raises(ValueError):
        await cat.search_async("x", top_k=0)
    with pytest.raises(ValueError):
        await cat.get_async("  ")


async def test_feed_get_quote_case_insensitive():
    feed = InMemoryMarketDataFeed()
    feed.publish(_q("AAPL", "150"))
    assert (await feed.get_quote_async("aapl")).last == Decimal("150")
    assert await feed.get_quote_async("nope") is None


async def test_feed_subscribe_broadcast_and_dispose():
    feed = InMemoryMarketDataFeed()
    received: list[Quote] = []

    async def handler(q: Quote) -> None:
        received.append(q)

    sub = feed.subscribe_quotes("AAPL", handler)
    assert isinstance(sub, IDisposable)
    feed.publish(_q("aapl", "150"))  # case-insensitive routing to the subscriber
    await asyncio.sleep(0)  # let the fire-and-forget task run
    assert [q.last for q in received] == [Decimal("150")]

    sub.dispose()
    feed.publish(_q("AAPL", "151"))
    await asyncio.sleep(0)
    assert len(received) == 1  # no delivery after dispose


async def test_feed_throwing_subscriber_does_not_break_publish():
    feed = InMemoryMarketDataFeed()
    good: list[Quote] = []

    async def bad(q: Quote) -> None:
        raise RuntimeError("boom")

    async def good_handler(q: Quote) -> None:
        good.append(q)

    feed.subscribe_quotes("X", bad)
    feed.subscribe_quotes("X", good_handler)
    feed.publish(_q("X", "10"))  # must not raise
    await asyncio.sleep(0)
    assert [q.last for q in good] == [Decimal("10")]


async def test_order_router_rules():
    cat = InMemoryInstrumentCatalog()
    cat.add(Instrument("AAPL", "NASDAQ", "USD", "Equity"))
    router = InMemoryOrderRouter(cat)

    ok = await router.submit_async(
        OrderRequest("AAPL", OrderSide.Buy, OrderType.Market, Decimal("10"), None)
    )
    assert ok.accepted and ok.failure_reason is None and ok.order_id == "ord-1"

    neg = await router.submit_async(
        OrderRequest("AAPL", OrderSide.Buy, OrderType.Market, Decimal("0"), None)
    )
    assert not neg.accepted and "positive" in neg.failure_reason

    bad_limit = await router.submit_async(
        OrderRequest("AAPL", OrderSide.Buy, OrderType.Limit, Decimal("5"), None)
    )
    assert not bad_limit.accepted and "LimitPrice" in bad_limit.failure_reason

    unknown = await router.submit_async(
        OrderRequest("ZZZZ", OrderSide.Sell, OrderType.Market, Decimal("1"), None)
    )
    assert not unknown.accepted and unknown.failure_reason == "Unknown symbol"


async def test_order_router_ids_monotonic():
    cat = InMemoryInstrumentCatalog()
    cat.add(Instrument("AAPL", "NASDAQ", "USD", "Equity"))
    router = InMemoryOrderRouter(cat)
    r1 = await router.submit_async(
        OrderRequest("AAPL", OrderSide.Buy, OrderType.Market, Decimal("1"), None)
    )
    r2 = await router.submit_async(
        OrderRequest("AAPL", OrderSide.Buy, OrderType.Market, Decimal("2"), None)
    )
    assert (r1.order_id, r2.order_id) == ("ord-1", "ord-2")


async def test_null_defaults_fail_closed():
    assert NullMarketDataFeed.Instance.backend_id == "null"
    assert await NullMarketDataFeed.Instance.get_quote_async("AAPL") is None
    # null subscribe returns a no-op disposable
    d = NullMarketDataFeed.Instance.subscribe_quotes("AAPL", lambda q: asyncio.sleep(0))
    assert isinstance(d, IDisposable)
    d.dispose()
    assert await NullInstrumentCatalog.Instance.search_async("x") == []
    res = await NullOrderRouter.Instance.submit_async(
        OrderRequest("AAPL", OrderSide.Buy, OrderType.Market, Decimal("1"), None)
    )
    assert isinstance(res, OrderResult)
    assert res.order_id == _GUID_EMPTY and not res.accepted
    assert res.failure_reason == "NullOrderRouter — fail-closed."
