"""test_micro_agents_board.py — CircleAI.MicroAgents port.

Covers FuncMicroAgent (delegate-backed), NullMicroAgent, the InMemoryMicroAgentHost
registry/router, and the MicroAgentSearch / MicroAgentInvocationLog helpers.
C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.micro_agents import (
    FuncMicroAgent,
    IMicroAgent,
    IMicroAgentHost,
    InMemoryMicroAgentHost,
    MicroAgentDescriptor,
    MicroAgentInvocation,
    MicroAgentInvocationLog,
    MicroAgentResponse,
    MicroAgentSearch,
    NullMicroAgent,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(mins: int) -> datetime:
    return _T0 + timedelta(minutes=mins)


async def test_func_micro_agent_wraps_delegate():
    async def impl(text: str, ct) -> MicroAgentResponse:
        return MicroAgentResponse("echo", text.upper(), {"len": str(len(text))})

    agent = FuncMicroAgent("echo", "echoer", ["text"], impl)
    assert isinstance(agent, IMicroAgent)
    assert agent.agent_id == "echo" and agent.backend_id == "func"
    assert agent.descriptor == MicroAgentDescriptor("echo", "echoer", ["text"])
    resp = await agent.invoke_async("hi")
    assert resp.output == "HI" and resp.metadata == {"len": "2"}


def test_func_micro_agent_guards():
    with pytest.raises(ValueError):
        FuncMicroAgent("  ", "d", [], (lambda t, c: None))  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        FuncMicroAgent("a", "d", [], None)  # type: ignore[arg-type]


async def test_null_micro_agent():
    agent = NullMicroAgent()
    assert agent.agent_id == "null" and agent.backend_id == "null"
    assert agent.descriptor.description == "No-op micro agent"
    resp = await agent.invoke_async("anything")
    assert resp.agent_id == "null" and resp.output == ""


async def test_host_register_list_invoke():
    host = InMemoryMicroAgentHost()
    assert isinstance(host, IMicroAgentHost) and host.backend_id == "in-memory"

    async def impl(text: str, ct) -> MicroAgentResponse:
        return MicroAgentResponse("a", f"ran:{text}")

    host.register(FuncMicroAgent("a", "d", ["cap"], impl))
    listed = host.list()
    assert [d.agent_id for d in listed] == ["a"]
    resp = await host.invoke_async("a", "input")
    assert resp is not None and resp.output == "ran:input"
    assert await host.invoke_async("missing", "x") is None


def test_search_by_capability_and_freetext():
    ds = [
        MicroAgentDescriptor("summariser", "summarises text", ["nlp", "text"]),
        MicroAgentDescriptor("router", "routes requests", ["routing"]),
        MicroAgentDescriptor("archiver", "archives NLP outputs", ["storage"]),
    ]
    by_cap = MicroAgentSearch.by_capability(ds, "NLP")  # case-insensitive
    assert [d.agent_id for d in by_cap] == ["summariser"]

    found = MicroAgentSearch.search(ds, "nlp")  # matches capability + description
    assert {d.agent_id for d in found} == {"summariser", "archiver"}
    assert MicroAgentSearch.search(ds, "route")[0].agent_id == "router"

    with pytest.raises(ValueError):
        MicroAgentSearch.by_capability(ds, " ")
    with pytest.raises(ValueError):
        MicroAgentSearch.search(ds, "x", top_k=0)


def test_search_topk_limit():
    ds = [MicroAgentDescriptor(f"a{i}", "match", []) for i in range(5)]
    assert len(MicroAgentSearch.search(ds, "match", top_k=3)) == 3


def test_invocation_log():
    log = MicroAgentInvocationLog()
    for i in range(3):
        log.append(MicroAgentInvocation("agent", f"in{i}", f"out{i}", _at(i)))
    log.append(MicroAgentInvocation("other", "x", "y", _at(5)))
    assert log.total_invocations == 4
    recent = log.for_agent("agent", 2)
    assert [i.input for i in recent] == ["in2", "in1"]  # newest-first
    with pytest.raises(ValueError):
        log.for_agent("agent", 0)
