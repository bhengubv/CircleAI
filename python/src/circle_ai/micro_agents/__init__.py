"""circle_ai.micro_agents — port of the CircleAI.MicroAgents assembly.

(2.9.0 contracts / 3.3.0 impl) Micro-agent domain: descriptor + response
records, the agent + host contracts, a delegate-backed FuncMicroAgent, the
no-op NullMicroAgent, a real InMemoryMicroAgentHost registry/router, and
capability-search + invocation-log helpers. C# is the exact spec.

Public surface:

  * MicroAgentDescriptor / MicroAgentResponse             — domain records.
  * IMicroAgent / IMicroAgentHost                         — contracts.
  * FuncMicroAgent / NullMicroAgent / InMemoryMicroAgentHost.
  * MicroAgentSearch / MicroAgentInvocationLog / MicroAgentInvocation — helpers.
"""
from __future__ import annotations

from .contracts import (
    IMicroAgent,
    IMicroAgentHost,
    MicroAgentDescriptor,
    MicroAgentResponse,
)
from .helpers import (
    MicroAgentInvocation,
    MicroAgentInvocationLog,
    MicroAgentSearch,
)
from .in_memory_micro_agents import (
    FuncMicroAgent,
    InMemoryMicroAgentHost,
    NullMicroAgent,
)

__all__ = [
    "MicroAgentDescriptor",
    "MicroAgentResponse",
    "IMicroAgent",
    "IMicroAgentHost",
    "FuncMicroAgent",
    "NullMicroAgent",
    "InMemoryMicroAgentHost",
    "MicroAgentSearch",
    "MicroAgentInvocationLog",
    "MicroAgentInvocation",
]
