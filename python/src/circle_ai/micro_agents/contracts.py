# contracts.py
#
# Port of CircleAI.MicroAgents Contracts.cs (C# — the EXACT spec).
#
# (2.9.0) Micro-agent contracts: descriptor, response, agent, host. C# records
# map to frozen slotted dataclasses; IReadOnlyDictionary<string,string>? maps to
# Optional[Dict[str, str]].

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Dict, List, Optional


@dataclass(frozen=True, slots=True)
class MicroAgentDescriptor:
    """Mirrors ``CircleAI.MicroAgents.MicroAgentDescriptor`` —
    ``record(string AgentId, string Description, IReadOnlyList<string>
    Capabilities)``."""

    agent_id: str
    description: str
    capabilities: List[str]


@dataclass(frozen=True, slots=True)
class MicroAgentResponse:
    """Mirrors ``CircleAI.MicroAgents.MicroAgentResponse`` — ``record(string
    AgentId, string Output, IReadOnlyDictionary<string, string>? Metadata =
    null)``."""

    agent_id: str
    output: str
    metadata: Optional[Dict[str, str]] = None


class IMicroAgent(ABC):
    """(2.9.0) A single micro-agent."""

    @property
    @abstractmethod
    def agent_id(self) -> str:
        ...

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @property
    @abstractmethod
    def descriptor(self) -> MicroAgentDescriptor:
        ...

    @abstractmethod
    async def invoke_async(self, input: str, ct: Optional[object] = None) -> MicroAgentResponse:
        ...


class IMicroAgentHost(ABC):
    """(2.9.0) Registry + router for micro-agents."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    def register(self, agent: IMicroAgent) -> None:
        ...

    @abstractmethod
    def list(self) -> List[MicroAgentDescriptor]:
        ...

    @abstractmethod
    async def invoke_async(self, agent_id: str, input: str, ct: Optional[object] = None) -> Optional[MicroAgentResponse]:
        ...
