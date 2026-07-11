# null_implementations.py
#
# Port of CircleAI.Workflows NullImplementations.cs (C# — the EXACT spec).
#
# (2.8.0) Fail-closed durable-workflow defaults. NullWorkflowRunner.StartAsync
# returns a Failed execution stamped with Guid.Empty (dashed) +
# DateTimeOffset.MinValue.

from __future__ import annotations

from datetime import datetime, timezone
from typing import Any, Dict, Optional

from .contracts import (
    CheckpointPayload,
    IWorkflowDefinitionStore,
    IWorkflowRunner,
    IWorkflowState,
    WorkflowDefinition,
    WorkflowExecution,
    WorkflowPhase,
)

_GUID_EMPTY = "00000000-0000-0000-0000-000000000000"
_MIN_UTC = datetime(1, 1, 1, tzinfo=timezone.utc)


class NullWorkflowDefinitionStore(IWorkflowDefinitionStore):
    Instance: "NullWorkflowDefinitionStore"

    @property
    def backend_id(self) -> str:
        return "null"

    async def upsert_async(self, d: WorkflowDefinition, ct: Optional[object] = None) -> None:
        return None

    async def get_async(self, id: str, ct: Optional[object] = None) -> Optional[WorkflowDefinition]:
        return None


class NullWorkflowRunner(IWorkflowRunner):
    Instance: "NullWorkflowRunner"

    @property
    def backend_id(self) -> str:
        return "null"

    async def start_async(
        self, id: str, inputs: Optional[Dict[str, Any]] = None, ct: Optional[object] = None
    ) -> WorkflowExecution:
        return WorkflowExecution(_GUID_EMPTY, id, WorkflowPhase.Failed, _MIN_UTC, "NullWorkflowRunner")

    async def get_async(self, run_id: str, ct: Optional[object] = None) -> Optional[WorkflowExecution]:
        return None

    async def cancel_async(self, run_id: str, ct: Optional[object] = None) -> None:
        return None


class NullWorkflowState(IWorkflowState):
    Instance: "NullWorkflowState"

    @property
    def backend_id(self) -> str:
        return "null"

    async def checkpoint_async(self, p: CheckpointPayload, ct: Optional[object] = None) -> None:
        return None

    async def load_async(self, run_id: str, step_id: str, ct: Optional[object] = None) -> Optional[CheckpointPayload]:
        return None


NullWorkflowDefinitionStore.Instance = NullWorkflowDefinitionStore()
NullWorkflowRunner.Instance = NullWorkflowRunner()
NullWorkflowState.Instance = NullWorkflowState()
