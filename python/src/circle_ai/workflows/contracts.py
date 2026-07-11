# contracts.py
#
# Port of CircleAI.Workflows Contracts.cs (C# — the EXACT spec).
#
# (2.8.0) Durable workflow contracts: the WorkflowPhase enum, the
# WorkflowDefinition / WorkflowExecution / CheckpointPayload records, and the
# definition-store / runner / state contracts. C# ReadOnlyMemory<byte> maps to
# bytes; IReadOnlyDictionary<string, object?> maps to Dict[str, Any].

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import Any, Dict, Optional


class WorkflowPhase(IntEnum):
    """Mirrors ``CircleAI.Workflows.WorkflowPhase`` (declaration order)."""

    Pending = 0
    Running = 1
    Suspended = 2
    Completed = 3
    Failed = 4


@dataclass(frozen=True, slots=True)
class WorkflowDefinition:
    """Mirrors ``CircleAI.Workflows.WorkflowDefinition`` — ``record(string
    DefinitionId, string Name, string Version, string Description)``."""

    definition_id: str
    name: str
    version: str
    description: str


@dataclass(frozen=True, slots=True)
class WorkflowExecution:
    """Mirrors ``CircleAI.Workflows.WorkflowExecution`` — ``record(string RunId,
    string DefinitionId, WorkflowPhase Phase, DateTimeOffset StartUtc,
    string? FailureReason)``."""

    run_id: str
    definition_id: str
    phase: WorkflowPhase
    start_utc: datetime
    failure_reason: Optional[str]


@dataclass(frozen=True, slots=True)
class CheckpointPayload:
    """Mirrors ``CircleAI.Workflows.CheckpointPayload`` — ``record(string RunId,
    string StepId, ReadOnlyMemory<byte> StateBlob)``."""

    run_id: str
    step_id: str
    state_blob: bytes


class IWorkflowDefinitionStore(ABC):
    """(2.8.0) Workflow-definition store."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def upsert_async(self, d: WorkflowDefinition, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def get_async(self, id: str, ct: Optional[object] = None) -> Optional[WorkflowDefinition]:
        ...


class IWorkflowRunner(ABC):
    """(2.8.0) Workflow runner."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def start_async(
        self, definition_id: str, inputs: Optional[Dict[str, Any]] = None, ct: Optional[object] = None
    ) -> WorkflowExecution:
        ...

    @abstractmethod
    async def get_async(self, run_id: str, ct: Optional[object] = None) -> Optional[WorkflowExecution]:
        ...

    @abstractmethod
    async def cancel_async(self, run_id: str, ct: Optional[object] = None) -> None:
        ...


class IWorkflowState(ABC):
    """(2.8.0) Workflow checkpoint state."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def checkpoint_async(self, payload: CheckpointPayload, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def load_async(self, run_id: str, step_id: str, ct: Optional[object] = None) -> Optional[CheckpointPayload]:
        ...
