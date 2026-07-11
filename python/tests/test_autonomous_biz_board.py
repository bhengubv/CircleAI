"""test_autonomous_biz_board.py — CircleAI.AutonomousBiz port.

Covers the treasury snapshot (currency-matched running balance from the revenue
loop), the revenue-loop fan-out pub/sub with kept history + since-filter +
dispose, the append-only decision log (newest-first with limit), and the
fail-closed null defaults. C# is the exact spec.
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timedelta, timezone
from decimal import Decimal

import pytest

from circle_ai.autonomous_biz import (
    AutonomousDecision,
    IDecisionLog,
    InMemoryDecisionLog,
    InMemoryRevenueLoop,
    InMemoryTreasury,
    IRevenueLoop,
    ITreasury,
    NullDecisionLog,
    NullRevenueLoop,
    NullTreasury,
    RevenueEvent,
    TreasurySnapshot,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(mins: int) -> datetime:
    return _T0 + timedelta(minutes=mins)


async def test_revenue_loop_history_and_since_filter():
    loop = InMemoryRevenueLoop()
    assert isinstance(loop, IRevenueLoop) and loop.backend_id == "in-memory"
    loop.publish(RevenueEvent("e0", Decimal("10"), "ZAR", "src", _at(0)))
    loop.publish(RevenueEvent("e1", Decimal("20"), "ZAR", "src", _at(10)))
    all_events = await loop.read_async(_at(0))
    assert [e.event_id for e in all_events] == ["e0", "e1"]
    since = await loop.read_async(_at(5))
    assert [e.event_id for e in since] == ["e1"]


async def test_revenue_loop_fanout_and_dispose():
    loop = InMemoryRevenueLoop()
    seen: list[str] = []

    async def handler(e: RevenueEvent) -> None:
        seen.append(e.event_id)

    token = loop.subscribe(handler)
    loop.publish(RevenueEvent("e0", Decimal("1"), "ZAR", "s", _at(0)))
    await asyncio.sleep(0)  # let the fire-and-forget task run
    assert seen == ["e0"]
    token.dispose()
    loop.publish(RevenueEvent("e1", Decimal("1"), "ZAR", "s", _at(1)))
    await asyncio.sleep(0)
    assert seen == ["e0"]  # no delivery after dispose
    token.dispose()  # idempotent


async def test_revenue_publish_none_raises():
    with pytest.raises(ValueError):
        InMemoryRevenueLoop().publish(None)  # type: ignore[arg-type]


async def test_treasury_sums_currency_matched_events():
    loop = InMemoryRevenueLoop()
    loop.publish(RevenueEvent("e0", Decimal("100.00"), "ZAR", "s", _at(0)))
    loop.publish(RevenueEvent("e1", Decimal("50.00"), "USD", "s", _at(1)))  # ignored
    loop.publish(RevenueEvent("e2", Decimal("25.00"), "zar", "s", _at(2)))  # case-insensitive
    tre = InMemoryTreasury(loop, "ZAR")
    assert isinstance(tre, ITreasury)
    snap = await tre.get_snapshot_async()
    assert isinstance(snap, TreasurySnapshot)
    assert snap.balance == Decimal("125.00")
    assert snap.currency == "ZAR"


def test_treasury_none_loop_raises():
    with pytest.raises(ValueError):
        InMemoryTreasury(None)  # type: ignore[arg-type]


async def test_decision_log_newest_first_with_limit():
    log = InMemoryDecisionLog()
    assert isinstance(log, IDecisionLog)
    for i in range(4):
        await log.append_async(AutonomousDecision(f"d{i}", "why", "act", _at(i)))
    recent = await log.read_async(2)
    assert [d.decision_id for d in recent] == ["d3", "d2"]
    with pytest.raises(ValueError):
        await log.read_async(0)


async def test_null_implementations_fail_closed():
    t = NullTreasury.Instance
    r = NullRevenueLoop.Instance
    d = NullDecisionLog.Instance
    assert t.backend_id == "null" and r.backend_id == "null" and d.backend_id == "null"
    snap = await t.get_snapshot_async()
    assert snap.balance == Decimal(0) and snap.currency == "ZAR"
    tok = r.subscribe(lambda e: None)  # type: ignore[arg-type]
    tok.dispose()  # empty disposable, no-op
    assert await r.read_async(_at(0)) == []
    assert await d.read_async() == []
    await d.append_async(AutonomousDecision("d", "w", "a", _at(0)))  # no-op
