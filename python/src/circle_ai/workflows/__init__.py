"""circle_ai.workflows — port of the CircleAI.Workflows assembly.

Durable-workflow domain: the WorkflowPhase enum, the WorkflowDefinition /
WorkflowExecution / CheckpointPayload records, the definition-store / runner /
state contracts with fail-closed null defaults, plus the Paca conversation state
machine (ConversationState + AgentConversation / ConversationStep /
ConversationPermissions records, the host-supplied IConversationExecutor
contract, and the PacaConversationRuntime registry/state-machine). C# is the
exact spec. (The other Paca* host boards — auth/boards/deploy/docs/mcp/plugins/
projects/realtime/skills — are outside this unit's scope.)

Public surface:

  * WorkflowPhase                                         — enum.
  * WorkflowDefinition / WorkflowExecution / CheckpointPayload — records.
  * IWorkflowDefinitionStore / IWorkflowRunner / IWorkflowState — contracts.
  * NullWorkflowDefinitionStore / NullWorkflowRunner / NullWorkflowState.
  * ConversationState                                     — enum.
  * AgentConversation / ConversationStep / ConversationPermissions — records.
  * IConversationExecutor / PacaConversationRuntime / ConversationCancelToken.
"""
from __future__ import annotations

from .contracts import (
    CheckpointPayload,
    IWorkflowDefinitionStore,
    IWorkflowRunner,
    IWorkflowState,
    WorkflowDefinition,
    WorkflowExecution,
    WorkflowPhase,
)
from .conversations import (
    AgentConversation,
    ConversationCancelToken,
    ConversationPermissions,
    ConversationState,
    ConversationStep,
    IConversationExecutor,
    PacaConversationRuntime,
)
from .null_implementations import (
    NullWorkflowDefinitionStore,
    NullWorkflowRunner,
    NullWorkflowState,
)

__all__ = [
    "WorkflowPhase",
    "WorkflowDefinition",
    "WorkflowExecution",
    "CheckpointPayload",
    "IWorkflowDefinitionStore",
    "IWorkflowRunner",
    "IWorkflowState",
    "NullWorkflowDefinitionStore",
    "NullWorkflowRunner",
    "NullWorkflowState",
    "ConversationState",
    "AgentConversation",
    "ConversationStep",
    "ConversationPermissions",
    "IConversationExecutor",
    "PacaConversationRuntime",
    "ConversationCancelToken",
]
