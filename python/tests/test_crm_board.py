"""test_crm_board.py — CircleAI.CRM port.

Covers the domain records, the async in-memory backends (contact upsert +
name/email substring search ordered case-insensitively, deal stage listing
ordered by Value desc, per-contact activity log newest-first with limit) with
blank-id / None-query / topK guards, and the fail-closed null defaults with their
Instance singletons. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone
from decimal import Decimal

import pytest

from circle_ai.crm import (
    Activity,
    Company,
    Contact,
    Deal,
    IActivityLog,
    IContactStore,
    IDealPipeline,
    InMemoryActivityLog,
    InMemoryContactStore,
    InMemoryDealPipeline,
    NullActivityLog,
    NullContactStore,
    NullDealPipeline,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(mins: int) -> datetime:
    return _T0 + timedelta(minutes=mins)


def test_backends_are_contracts():
    assert isinstance(InMemoryContactStore(), IContactStore)
    assert isinstance(InMemoryDealPipeline(), IDealPipeline)
    assert isinstance(InMemoryActivityLog(), IActivityLog)
    assert InMemoryContactStore().backend_id == "in-memory"


def test_company_record_shape():
    c = Company("co1", "Acme", "Manufacturing")
    assert (c.company_id, c.name, c.industry) == ("co1", "Acme", "Manufacturing")


async def test_contact_upsert_get_and_search():
    store = InMemoryContactStore()
    await store.upsert_async(Contact("c1", "Ann Smith", "ann@x.com", None, None))
    await store.upsert_async(Contact("c2", "Bob Jones", "bob@y.com", None, None))
    await store.upsert_async(Contact("c3", "Zed Ann", "zed@z.com", None, None))
    got = await store.get_async("c1")
    assert got is not None and got.full_name == "Ann Smith"
    # substring, case-insensitive, ordered by FullName (case-insensitive)
    hits = await store.search_async("ann")
    assert [c.contact_id for c in hits] == ["c1", "c3"]
    # email substring also matches
    by_email = await store.search_async("bob@")
    assert [c.contact_id for c in by_email] == ["c2"]


async def test_contact_search_topk_and_guards():
    store = InMemoryContactStore()
    for i in range(5):
        await store.upsert_async(Contact(f"c{i}", f"Name{i}", None, None, None))
    assert len(await store.search_async("Name", top_k=2)) == 2
    with pytest.raises(ValueError):
        await store.search_async(None)  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        await store.search_async("x", top_k=0)


async def test_contact_upsert_blank_id_raises():
    with pytest.raises(ValueError):
        await InMemoryContactStore().upsert_async(Contact(" ", "X", None, None, None))


async def test_deal_list_by_stage_ordered_by_value_desc():
    pipe = InMemoryDealPipeline()
    await pipe.upsert_async(Deal("d1", "co1", "Small", Decimal("100"), "ZAR", "Won"))
    await pipe.upsert_async(Deal("d2", "co1", "Big", Decimal("900"), "ZAR", "won"))
    await pipe.upsert_async(Deal("d3", "co1", "Mid", Decimal("500"), "ZAR", "Lost"))
    won = await pipe.list_by_stage_async("WON")  # case-insensitive
    assert [d.deal_id for d in won] == ["d2", "d1"]  # Value descending


async def test_deal_blank_stage_and_id_guards():
    pipe = InMemoryDealPipeline()
    with pytest.raises(ValueError):
        await pipe.list_by_stage_async("  ")
    with pytest.raises(ValueError):
        await pipe.upsert_async(Deal("", "co", "n", Decimal("1"), "ZAR", "Won"))


async def test_activity_log_newest_first_with_limit_and_empty():
    log = InMemoryActivityLog()
    await log.append_async(Activity("a1", "c1", "call", "first", _at(0)))
    await log.append_async(Activity("a2", "c1", "email", "second", _at(10)))
    await log.append_async(Activity("a3", "c1", "note", "third", _at(5)))
    got = await log.read_for_contact_async("c1", limit=2)
    assert [a.activity_id for a in got] == ["a2", "a3"]
    assert await log.read_for_contact_async("nobody") == []


async def test_activity_log_guards():
    log = InMemoryActivityLog()
    with pytest.raises(ValueError):
        await log.append_async(Activity("a1", " ", "call", "x", _at(0)))
    with pytest.raises(ValueError):
        await log.read_for_contact_async("  ")


async def test_null_defaults_fail_closed():
    assert NullContactStore.Instance.backend_id == "null"
    assert await NullContactStore.Instance.get_async("x") is None
    assert await NullContactStore.Instance.search_async("x") == []
    await NullContactStore.Instance.upsert_async(Contact("c", "n", None, None, None))
    assert await NullDealPipeline.Instance.list_by_stage_async("Won") == []
    assert await NullActivityLog.Instance.read_for_contact_async("c") == []
    assert isinstance(NullContactStore.Instance, IContactStore)
