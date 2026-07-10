"""test_companion_resolver.py — InMemoryCompanionSessionResolver + companion endpoint."""
from __future__ import annotations

import asyncio

import pytest

from circle_ai.companion.companion_types import InterfaceKind
from circle_ai.inference_server import (
    AdmissionControl,
    CompanionTurnHandler,
    CompanionTurnRequest,
    InferenceServerOptions,
    InMemoryCompanionSessionResolver,
    ServerCounters,
)


class _FakeSession:
    def __init__(self, identity_id, interface):
        self.identity_id = identity_id
        self.interface = interface
        self._history = []

    @property
    def history(self):
        return self._history

    async def send_async(self, message, *, ct=None):
        self._history.append(message)
        return f"reply:{message}"

    async def agent_async(self, message, *, ct=None):
        self._history.append(message)
        return f"agent:{message}"

    async def stream_async(self, message, *, ct=None):
        self._history.append(message)
        for part in ["a", "b", "c"]:
            yield part


class _FakeFactory:
    def __init__(self):
        self.create_calls = 0

    async def create_async(self, identity_id, interface, *, ct=None):
        self.create_calls += 1
        return _FakeSession(identity_id, interface)


class _FailingFactory:
    def __init__(self):
        self.calls = 0

    async def create_async(self, identity_id, interface, *, ct=None):
        self.calls += 1
        raise RuntimeError("construct failed")


# ── resolver ─────────────────────────────────────────────────────────────


async def test_resolve_creates_and_caches_per_key():
    factory = _FakeFactory()
    resolver = InMemoryCompanionSessionResolver(factory)
    s1 = await resolver.resolve_async("sess", "id")
    s2 = await resolver.resolve_async("sess", "id")
    assert s1 is s2
    assert factory.create_calls == 1
    assert resolver.cached_session_count == 1


async def test_resolve_distinct_keys_distinct_sessions():
    factory = _FakeFactory()
    resolver = InMemoryCompanionSessionResolver(factory)
    a = await resolver.resolve_async("s1", "id")
    b = await resolver.resolve_async("s2", "id")
    assert a is not b
    assert factory.create_calls == 2


async def test_resolve_default_interface_is_web():
    resolver = InMemoryCompanionSessionResolver(_FakeFactory())
    s = await resolver.resolve_async("s", "id")
    assert s.interface == InterfaceKind.WEB


async def test_resolve_blank_ids_return_none():
    resolver = InMemoryCompanionSessionResolver(_FakeFactory())
    assert await resolver.resolve_async("", "id") is None
    assert await resolver.resolve_async("s", "  ") is None


async def test_failed_construction_not_cached():
    factory = _FailingFactory()
    resolver = InMemoryCompanionSessionResolver(factory)
    with pytest.raises(RuntimeError):
        await resolver.resolve_async("s", "id")
    # Slot dropped — a retry re-invokes the factory.
    with pytest.raises(RuntimeError):
        await resolver.resolve_async("s", "id")
    assert factory.calls == 2


async def test_concurrent_resolution_single_flights():
    factory = _FakeFactory()
    resolver = InMemoryCompanionSessionResolver(factory)
    results = await asyncio.gather(*[resolver.resolve_async("s", "id") for _ in range(5)])
    assert all(r is results[0] for r in results)
    assert factory.create_calls == 1


# ── companion endpoint ───────────────────────────────────────────────────


def _handler(resolver):
    counters = ServerCounters()
    return CompanionTurnHandler(resolver, AdmissionControl(InferenceServerOptions(), counters), counters)


async def test_companion_turn_send():
    resolver = InMemoryCompanionSessionResolver(_FakeFactory())
    h = _handler(resolver)
    res = await h.handle(CompanionTurnRequest(session_id="s", identity_id="id", message="hi"))
    assert res.status_code == 200
    d = res.body_dict
    assert d["reply"] == "reply:hi"
    assert d["agentic"] is False
    assert d["turn_index"] == 1


async def test_companion_turn_agentic():
    resolver = InMemoryCompanionSessionResolver(_FakeFactory())
    h = _handler(resolver)
    res = await h.handle(CompanionTurnRequest(session_id="s", identity_id="id", message="do", agentic=True))
    assert res.body_dict["reply"] == "agent:do"
    assert res.body_dict["agentic"] is True


async def test_companion_turn_stream():
    resolver = InMemoryCompanionSessionResolver(_FakeFactory())
    h = _handler(resolver)
    res = await h.handle(CompanionTurnRequest(session_id="s", identity_id="id", message="hi", stream=True))
    assert res.sse_frames is not None
    assert res.sse_frames[-1] == "data: [DONE]\n\n"
    # Three delta frames + terminator.
    assert len(res.sse_frames) == 4


async def test_companion_turn_missing_fields_400():
    resolver = InMemoryCompanionSessionResolver(_FakeFactory())
    h = _handler(resolver)
    res = await h.handle(CompanionTurnRequest(session_id="", identity_id="id", message="hi"))
    assert res.status_code == 400
    assert res.body_dict["error"]["code"] == "missing_field"


async def test_companion_turn_session_not_found_404():
    # A resolver that always returns None (blank identity path won't hit here,
    # so use a resolver whose factory yields None).
    class _NoneFactory:
        async def create_async(self, identity_id, interface, *, ct=None):
            return None

    resolver = InMemoryCompanionSessionResolver(_NoneFactory())
    h = _handler(resolver)
    res = await h.handle(CompanionTurnRequest(session_id="s", identity_id="id", message="hi"))
    assert res.status_code == 404
    assert res.body_dict["error"]["code"] == "session_not_found"
