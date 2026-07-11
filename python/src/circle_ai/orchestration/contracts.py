# contracts.py
#
# Port of CircleAI.Orchestration AgentRole.cs / AgentTask.cs / SwarmResult.cs /
# QualityGateResult.cs / AgentSwarmConfig.cs / IAgentDispatcher.cs (C# — the
# EXACT spec).
#
# Enums (AgentRole / AgentPriority / AgentStatus), the AgentTask / SwarmResult /
# QualityGateResult / AgentSwarmConfig records, and the IAgentDispatcher
# contract. C# enums map to IntEnum (declaration order == ordinal); records map
# to frozen slotted dataclasses; Guid maps to uuid.UUID; TimeSpan maps to
# datetime.timedelta.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from enum import IntEnum
from typing import Dict, List, Optional
from uuid import UUID, uuid4


class AgentRole(IntEnum):
    """Mirrors ``CircleAI.Orchestration.AgentRole`` (declaration order)."""

    Engineering = 0
    Operations = 1
    Review = 2
    Security = 3


class AgentPriority(IntEnum):
    """Mirrors ``CircleAI.Orchestration.AgentPriority`` — lower value = higher
    urgency."""

    Critical = 0
    High = 1
    Normal = 2
    Low = 3


class AgentStatus(IntEnum):
    """Mirrors ``CircleAI.Orchestration.AgentStatus`` (declaration order)."""

    Pending = 0
    Running = 1
    Passed = 2
    Failed = 3
    Blocked = 4


@dataclass(frozen=True, slots=True)
class AgentTask:
    """Mirrors ``CircleAI.Orchestration.AgentTask`` — ``record(Guid Id,
    AgentRole Role, string Description, AgentPriority Priority,
    IReadOnlyDictionary<string, string> Inputs, DateTimeOffset CreatedAt)``."""

    id: UUID
    role: AgentRole
    description: str
    priority: AgentPriority
    inputs: Dict[str, str]
    created_at: datetime

    @staticmethod
    def create(
        role: AgentRole,
        description: str,
        priority: AgentPriority,
        inputs: Optional[Dict[str, str]] = None,
    ) -> "AgentTask":
        """Factory — stamps a fresh Guid + UtcNow. Mirrors ``AgentTask.Create``."""
        return AgentTask(
            uuid4(),
            role,
            description,
            priority,
            inputs if inputs is not None else {},
            datetime.now(timezone.utc),
        )


@dataclass(frozen=True, slots=True)
class SwarmResult:
    """Mirrors ``CircleAI.Orchestration.SwarmResult`` — ``record(Guid TaskId,
    AgentRole Role, AgentStatus Status, string Output,
    IReadOnlyList<string> Issues, DateTimeOffset CompletedAt)``."""

    task_id: UUID
    role: AgentRole
    status: AgentStatus
    output: str
    issues: List[str]
    completed_at: datetime


@dataclass(frozen=True, slots=True)
class QualityGateResult:
    """Mirrors ``CircleAI.Orchestration.QualityGateResult`` — ``record(bool
    Passed, IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings)``."""

    passed: bool
    blockers: List[str]
    warnings: List[str]


@dataclass(frozen=True, slots=True)
class AgentSwarmConfig:
    """Mirrors ``CircleAI.Orchestration.AgentSwarmConfig`` — ``record(int
    MaxConcurrency, TimeSpan TaskTimeout, bool RequireReviewPassBeforeDeploy,
    bool RequireSecurityPassBeforeDeploy)``."""

    max_concurrency: int
    task_timeout: timedelta
    require_review_pass_before_deploy: bool
    require_security_pass_before_deploy: bool

    @staticmethod
    def default() -> "AgentSwarmConfig":
        """Production-safe defaults: 4 concurrent, 5-minute timeout, both gates
        enforced. Mirrors ``AgentSwarmConfig.Default``."""
        return AgentSwarmConfig(4, timedelta(minutes=5), True, True)

    @staticmethod
    def for_device(probe: "object") -> "AgentSwarmConfig":
        """Device-aware defaults — MaxConcurrency sized via
        DeviceTierDefaults.max_concurrency against the supplied DeviceProbe;
        everything else matches :meth:`default`. Mirrors
        ``AgentSwarmConfig.ForDevice``."""
        # Imported lazily to avoid a hard import cycle at module load.
        from ..device import DeviceTierDefaults

        return AgentSwarmConfig(
            max_concurrency=DeviceTierDefaults.max_concurrency(probe.classify(), probe.cpu_cores),
            task_timeout=timedelta(minutes=5),
            require_review_pass_before_deploy=True,
            require_security_pass_before_deploy=True,
        )


class IAgentDispatcher(ABC):
    """(orchestration) Routes agent tasks to handlers + evaluates quality
    gates."""

    @abstractmethod
    async def dispatch_async(self, task: AgentTask, ct: Optional[object] = None) -> SwarmResult:
        ...

    @abstractmethod
    async def run_quality_gate_async(self, result: SwarmResult, ct: Optional[object] = None) -> QualityGateResult:
        ...
