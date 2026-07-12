"""circle_ai.workflows — port of the CircleAI.Workflows assembly.

Durable-workflow domain: the WorkflowPhase enum, the WorkflowDefinition /
WorkflowExecution / CheckpointPayload records, the definition-store / runner /
state contracts with fail-closed null defaults, plus the Paca conversation state
machine (ConversationState + AgentConversation / ConversationStep /
ConversationPermissions records, the host-supplied IConversationExecutor
contract, and the PacaConversationRuntime registry/state-machine), plus the full
PACA project-management surface ported from paca: projects/tasks, kanban boards
+ sprints, HMAC-JWT + API-key auth, agent members + templates, living docs with
versioning + @mentions, the WASM plugin lifecycle, the MCP tool server, the
realtime fan-out hub, the built-in skill library + installer, and the docker
compose deploy generator. C# is the exact spec.

Public surface:

  * WorkflowPhase                                         — enum.
  * WorkflowDefinition / WorkflowExecution / CheckpointPayload — records.
  * IWorkflowDefinitionStore / IWorkflowRunner / IWorkflowState — contracts.
  * NullWorkflowDefinitionStore / NullWorkflowRunner / NullWorkflowState.
  * ConversationState                                     — enum.
  * AgentConversation / ConversationStep / ConversationPermissions — records.
  * IConversationExecutor / PacaConversationRuntime / ConversationCancelToken.
  * PacaProject / PacaTask / InMemoryPacaStore.
  * SprintState / StatusColumn / PacaSprint / TaskBoardMetadata / BoardView /
    PacaBoard.
  * JwtPair / JwtPayload / HmacJwtAuthenticator / PacaApiKeyRecord /
    PacaApiKeyAuthenticator.
  * MemberKind / ProjectMember / AgentLlmConfig / AgentSystemPrompts /
    AgentCapabilities / AgentLimits / AgentGitIdentity / AgentTriggers /
    AgentProfile / AgentTemplates / InMemoryPacaMemberStore.
  * DocNode / DocVersion / DocActivity / DocLink / PacaDocService.
  * PluginExtensionPoint / PluginResourceLimits / PluginManifest /
    InstalledPlugin / IPluginRuntimeHost / PacaPluginRegistry.
  * McpTransportKind / AgentMcpConfig / PacaMcpTool / PacaMcpHandler /
    PacaMcpServer / PacaCoreMcpTools.
  * RealtimePacaEvent (+ TaskUpdatedEvent / QueryInvalidationEvent /
    DocCursorMoveEvent / AgentActivityEvent / ConversationStepEvent) /
    IRealtimeBroadcaster / PermissionCheck / PacaRealtimeHub / QueryInvalidation.
  * PacaSkill / PacaSkillLibrary / SkillTemplates / PacaSkillInstaller.
  * PacaDeployMode / PacaDeployOverrides / PacaDeployArtifact / PacaDeployer.
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
from .paca_agents import (
    AgentCapabilities,
    AgentGitIdentity,
    AgentLimits,
    AgentLlmConfig,
    AgentProfile,
    AgentSystemPrompts,
    AgentTemplates,
    AgentTriggers,
    InMemoryPacaMemberStore,
    MemberKind,
    ProjectMember,
)
from .paca_auth import (
    HmacJwtAuthenticator,
    JwtPair,
    JwtPayload,
    PacaApiKeyAuthenticator,
    PacaApiKeyRecord,
)
from .paca_boards import (
    BoardView,
    PacaBoard,
    PacaSprint,
    SprintState,
    StatusColumn,
    TaskBoardMetadata,
)
from .paca_deploy import (
    PacaDeployArtifact,
    PacaDeployMode,
    PacaDeployOverrides,
    PacaDeployer,
)
from .paca_docs import (
    DocActivity,
    DocLink,
    DocNode,
    DocVersion,
    PacaDocService,
)
from .paca_mcp import (
    AgentMcpConfig,
    McpTransportKind,
    PacaCoreMcpTools,
    PacaMcpHandler,
    PacaMcpServer,
    PacaMcpTool,
)
from .paca_plugins import (
    IPluginRuntimeHost,
    InstalledPlugin,
    PacaPluginRegistry,
    PluginExtensionPoint,
    PluginManifest,
    PluginResourceLimits,
)
from .paca_projects import (
    InMemoryPacaStore,
    PacaProject,
    PacaTask,
)
from .paca_realtime import (
    AgentActivityEvent,
    ConversationStepEvent,
    DocCursorMoveEvent,
    IRealtimeBroadcaster,
    PacaRealtimeHub,
    PermissionCheck,
    QueryInvalidation,
    QueryInvalidationEvent,
    RealtimePacaEvent,
    TaskUpdatedEvent,
)
from .paca_skills import (
    PacaSkill,
    PacaSkillInstaller,
    PacaSkillLibrary,
    SkillTemplates,
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
    # Projects / tasks
    "PacaProject",
    "PacaTask",
    "InMemoryPacaStore",
    # Boards / sprints
    "SprintState",
    "StatusColumn",
    "PacaSprint",
    "TaskBoardMetadata",
    "BoardView",
    "PacaBoard",
    # Auth
    "JwtPair",
    "JwtPayload",
    "HmacJwtAuthenticator",
    "PacaApiKeyRecord",
    "PacaApiKeyAuthenticator",
    # Agents / members
    "MemberKind",
    "ProjectMember",
    "AgentLlmConfig",
    "AgentSystemPrompts",
    "AgentCapabilities",
    "AgentLimits",
    "AgentGitIdentity",
    "AgentTriggers",
    "AgentProfile",
    "AgentTemplates",
    "InMemoryPacaMemberStore",
    # Docs
    "DocNode",
    "DocVersion",
    "DocActivity",
    "DocLink",
    "PacaDocService",
    # Plugins (WASM lifecycle)
    "PluginExtensionPoint",
    "PluginResourceLimits",
    "PluginManifest",
    "InstalledPlugin",
    "IPluginRuntimeHost",
    "PacaPluginRegistry",
    # MCP
    "McpTransportKind",
    "AgentMcpConfig",
    "PacaMcpTool",
    "PacaMcpHandler",
    "PacaMcpServer",
    "PacaCoreMcpTools",
    # Realtime
    "RealtimePacaEvent",
    "TaskUpdatedEvent",
    "QueryInvalidationEvent",
    "DocCursorMoveEvent",
    "AgentActivityEvent",
    "ConversationStepEvent",
    "IRealtimeBroadcaster",
    "PermissionCheck",
    "PacaRealtimeHub",
    "QueryInvalidation",
    # Skills library
    "PacaSkill",
    "PacaSkillLibrary",
    "SkillTemplates",
    "PacaSkillInstaller",
    # Deploy
    "PacaDeployMode",
    "PacaDeployOverrides",
    "PacaDeployArtifact",
    "PacaDeployer",
]
