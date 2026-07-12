"""circle_ai.orchestration — port of the CircleAI.Orchestration assembly.

Agent-swarm orchestration primitives: the AgentRole / AgentPriority /
AgentStatus enums, the AgentTask / SwarmResult / QualityGateResult /
AgentSwarmConfig records (with the Create / Default / ForDevice factories), the
IAgentDispatcher contract, an in-process LocalAgentDispatcher that routes tasks
to per-role handlers and runs a deterministic quality gate, the incident-trigger
mapper (episodic-memory + anomaly-signal -> agent tasks), the semaphore-bounded
LokiOrchestrator (swarm + quality-gate + timeout), and the
SecurityOrchestrationBridge (immune-system watchdog -> ops-security agents). C#
is the exact spec.

Public surface:

  * AgentRole / AgentPriority / AgentStatus               — enums.
  * AgentTask / SwarmResult / QualityGateResult / AgentSwarmConfig — records.
  * IAgentDispatcher                                      — contract.
  * LocalAgentDispatcher                                  — in-process dispatcher.
  * IncidentTrigger                                       — incident -> tasks.
  * LokiOrchestrator                                      — host-side swarm runner.
  * SecurityOrchestrationBridge                           — security -> orchestration.
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
from .incident_trigger import IncidentTrigger
from .local_agent_dispatcher import AgentHandler, LocalAgentDispatcher
from .loki_orchestrator import LokiOrchestrator
from .security_orchestration_bridge import SecurityOrchestrationBridge

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
    "IncidentTrigger",
    "LokiOrchestrator",
    "SecurityOrchestrationBridge",
]
