"""test_collaboration_board.py — CircleAI.Collaboration port.

Covers the domain records, the in-memory channel/message/presence stores
(team filter ordered by name, per-channel descending-time read with limit,
presence get), and the fail-closed null defaults. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.collaboration import (
    Channel,
    IChannelStore,
    IMessageStore,
    InMemoryChannelStore,
    InMemoryMessageStore,
    InMemoryPresence,
    IPresence,
    Message,
    NullChannelStore,
    NullMessageStore,
    NullPresence,
    PresenceState,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(mins: int) -> datetime:
    return _T0 + timedelta(minutes=mins)


def test_records_are_frozen():
    c = Channel("c", "n", "t")
    with pytest.raises(Exception):
        c.name = "x"  # type: ignore[misc]


async def test_channel_get_and_team_list_ordered_by_name():
    store = InMemoryChannelStore()
    assert isinstance(store, IChannelStore)
    store.upsert(Channel("c2", "zeta", "team-1"))
    store.upsert(Channel("c1", "alpha", "team-1"))
    store.upsert(Channel("c3", "other", "team-2"))
    assert (await store.get_async("c1")).name == "alpha"
    assert await store.get_async("missing") is None
    team1 = await store.list_for_team_async("team-1")
    assert [c.name for c in team1] == ["alpha", "zeta"]  # ordered by name


async def test_channel_whitespace_guards():
    store = InMemoryChannelStore()
    with pytest.raises(ValueError):
        await store.get_async("   ")
    with pytest.raises(ValueError):
        await store.list_for_team_async("")


async def test_message_post_and_read_descending_with_limit():
    store = InMemoryMessageStore()
    assert isinstance(store, IMessageStore)
    for i in range(5):
        await store.post_async(Message(f"m{i}", "chan", "u", f"body{i}", _at(i)))
    recent = await store.read_async("chan", 3)
    assert [m.message_id for m in recent] == ["m4", "m3", "m2"]
    assert await store.read_async("empty") == []


async def test_message_channel_id_required():
    store = InMemoryMessageStore()
    with pytest.raises(ValueError):
        await store.post_async(Message("m", "  ", "u", "b", _at(0)))


async def test_presence_set_and_get():
    p = InMemoryPresence()
    assert isinstance(p, IPresence)
    p.set(PresenceState("u1", True, _at(0)))
    got = await p.get_async("u1")
    assert got is not None and got.online is True
    assert await p.get_async("u2") is None


async def test_null_implementations_fail_closed():
    ch = NullChannelStore.Instance
    ms = NullMessageStore.Instance
    pr = NullPresence.Instance
    assert ch.backend_id == "null" and ms.backend_id == "null" and pr.backend_id == "null"
    assert await ch.get_async("x") is None
    assert await ch.list_for_team_async("x") == []
    msg = Message("m", "c", "u", "b", _at(0))
    assert (await ms.post_async(msg)) is msg
    assert await ms.read_async("c") == []
    assert await pr.get_async("u") is None
    assert NullChannelStore.Instance is NullChannelStore.Instance
