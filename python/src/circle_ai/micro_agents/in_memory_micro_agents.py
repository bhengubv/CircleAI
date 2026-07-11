# in_memory_micro_agents.py
#
# Port of CircleAI.MicroAgents InMemoryMicroAgents.cs + NullImplementations.cs
# (the InMemoryMicroAgentHost is a REAL impl living in NullImplementations.cs) —
# C# is the EXACT spec.
#
# (3.3.0) FuncMicroAgent wraps a coroutine function so callers can register a
# lambda without authoring a new type; NullMicroAgent is the no-op agent;
# InMemoryMicroAgentHost keeps a registry and routes Invoke calls.

from __future__ import annotations

from typing import Awaitable, Callable, Dict, List, Optional

from .contracts import IMicroAgent, IMicroAgentHost, MicroAgentDescriptor, MicroAgentResponse

# C# Func<string, CancellationToken, ValueTask<MicroAgentResponse>>.
MicroAgentImpl = Callable[[str, Optional[object]], Awaitable[MicroAgentResponse]]


class FuncMicroAgent(IMicroAgent):
    """(3.3.0) Wrap a coroutine function in an IMicroAgent."""

    def __init__(
        self,
        agent_id: str,
        description: Optional[str],
        capabilities: Optional[List[str]],
        impl: MicroAgentImpl,
    ) -> None:
        if agent_id is None or agent_id.strip() == "":
            raise ValueError("agentId required")
        if impl is None:
            raise ValueError("impl must not be None")
        self._agent_id = agent_id
        self._descriptor = MicroAgentDescriptor(agent_id, description or "", capabilities or [])
        self._impl = impl

    @property
    def agent_id(self) -> str:
        return self._agent_id

    @property
    def backend_id(self) -> str:
        return "func"

    @property
    def descriptor(self) -> MicroAgentDescriptor:
        return self._descriptor

    async def invoke_async(self, input: str, ct: Optional[object] = None) -> MicroAgentResponse:
        return await self._impl(input, ct)


class NullMicroAgent(IMicroAgent):
    """(2.9.0) No-op micro agent."""

    def __init__(self) -> None:
        self._descriptor = MicroAgentDescriptor("null", "No-op micro agent", [])

    @property
    def agent_id(self) -> str:
        return "null"

    @property
    def backend_id(self) -> str:
        return "null"

    @property
    def descriptor(self) -> MicroAgentDescriptor:
        return self._descriptor

    async def invoke_async(self, input: str, ct: Optional[object] = None) -> MicroAgentResponse:
        return MicroAgentResponse(self.agent_id, "")


class InMemoryMicroAgentHost(IMicroAgentHost):
    """(3.3.0) Real host — keeps a registry of agents and routes Invoke calls."""

    def __init__(self) -> None:
        self._agents: Dict[str, IMicroAgent] = {}

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def register(self, agent: IMicroAgent) -> None:
        self._agents[agent.agent_id] = agent

    def list(self) -> List[MicroAgentDescriptor]:
        return [a.descriptor for a in self._agents.values()]

    async def invoke_async(self, agent_id: str, input: str, ct: Optional[object] = None) -> Optional[MicroAgentResponse]:
        a = self._agents.get(agent_id)
        if a is not None:
            return await a.invoke_async(input, ct)
        return None
