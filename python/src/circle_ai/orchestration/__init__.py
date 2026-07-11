"""circle_ai.orchestration — port of the CircleAI.Orchestration assembly.

Agent-swarm orchestration primitives: the AgentRole / AgentPriority /
AgentStatus enums, the AgentTask / SwarmResult / QualityGateResult /
AgentSwarmConfig records (with the Create / Default / ForDevice factories), the
IAgentDispatcher contract, and an in-process LocalAgentDispatcher that routes
tasks to per-role handlers and runs a deterministic quality gate. C# is the
exact spec. (The LokiOrchestrator / SecurityOrchestrationBridge host layers are
out of this unit's scope.)

Public surface:

  * AgentRole / AgentPriority / AgentStatus               — enums.
  * AgentTask / SwarmResult / QualityGateResult / AgentSwarmConfig — records.
  * IAgentDispatcher                                      — contract.
  * LocalAgentDispatcher                                  — in-process dispatcher.
"""
from __future__ import annotations

from .contracts import (
    AgentPriority,
    AgentRole,
    AgentStatus,
    AgentSwarmConfig,
    AgentTask,
    IAgentDispatcher,
    QualityGateResult,
    SwarmResult,
)
from .local_agent_dispatcher import AgentHandler, LocalAgentDispatcher

__all__ = [
    "AgentRole",
    "AgentPriority",
    "AgentStatus",
    "AgentTask",
    "SwarmResult",
    "QualityGateResult",
    "AgentSwarmConfig",
    "IAgentDispatcher",
    "AgentHandler",
    "LocalAgentDispatcher",
]
