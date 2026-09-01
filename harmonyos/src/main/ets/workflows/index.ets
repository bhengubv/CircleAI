/**
 * workflows/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// workflows/index.ts
// Full-parity port of the scoped CircleAI.Workflows surface (C#). C# is the
// exact spec.
//
// Two areas in scope:
//   • Durable-workflow contracts — WorkflowPhase enum, WorkflowDefinition /
//     WorkflowExecution / CheckpointPayload records, the IWorkflowDefinitionStore
//     / IWorkflowRunner / IWorkflowState contracts, fail-closed Null* defaults,
//     and deterministic in-memory implementations (mirroring the sibling board
//     modules' InMemory pattern).
//   • Conversation state machine — ConversationState enum, AgentConversation /
//     ConversationStep / ConversationPermissions records, the IConversationExecutor
//     host hook, and the PacaConversationRuntime registry + lifecycle machine.
//
// Type mappings (C# → TS):
//   ReadOnlyMemory<byte> StateBlob      → Uint8Array
//   IReadOnlyDictionary<string,object?> → ReadonlyMap<string, unknown>
//   DateTimeOffset                      → Date
//   ValueTask / Task                    → Promise
//   CancellationTokenSource / linked    → AbortController + anyAbort(...)
//   record `with { ... }`               → object spread with overrides

// ═════════════════════════════════════════════════════════════════════════════
// Durable-workflow contracts
// ═════════════════════════════════════════════════════════════════════════════

/** Workflow lifecycle phase. Mirrors C# `WorkflowPhase`. */
export enum WorkflowPhase {
  Pending = 0,
  Running = 1,
  Suspended = 2,
  Completed = 3,
  Failed = 4,
}

/** A workflow definition. Mirrors C# `WorkflowDefinition` record. */
export interface WorkflowDefinition {
  readonly definitionId: string;
  readonly name: string;
  readonly version: string;
  readonly description: string;
}

/** Constructs a {@link WorkflowDefinition}. */
export function workflowDefinition(
  definitionId: string,
  name: string,
  version: string,
  description: string,
): WorkflowDefinition {
  return { definitionId, name, version, description };
}

/** A workflow run. Mirrors C# `WorkflowExecution` record. */
export interface WorkflowExecution {
  readonly runId: string;
  readonly definitionId: string;
  readonly phase: WorkflowPhase;
  readonly startUtc: Date;
  readonly failureReason: string | null;
}

/** Constructs a {@link WorkflowExecution}. */
export function workflowExecution(
  runId: string,
  definitionId: string,
  phase: WorkflowPhase,
  startUtc: Date,
  failureReason: string | null,
): WorkflowExecution {
  return { runId, definitionId, phase, startUtc, failureReason };
}

/** A durable checkpoint blob for a step. Mirrors C# `CheckpointPayload` record. */
export interface CheckpointPayload {
  readonly runId: string;
  readonly stepId: string;
  readonly stateBlob: Uint8Array;
}

/** Constructs a {@link CheckpointPayload}. */
export function checkpointPayload(
  runId: string,
  stepId: string,
  stateBlob: Uint8Array,
): CheckpointPayload {
  return { runId, stepId, stateBlob };
}

/** Stores workflow definitions. Mirrors C# `IWorkflowDefinitionStore`. */
export interface IWorkflowDefinitionStore {
  readonly backendId: string;
  upsertAsync(d: WorkflowDefinition, signal?: AbortSignal): Promise<void>;
  getAsync(id: string, signal?: AbortSignal): Promise<WorkflowDefinition | null>;
}

/** Starts + tracks workflow runs. Mirrors C# `IWorkflowRunner`. */
export interface IWorkflowRunner {
  readonly backendId: string;
  startAsync(
    definitionId: string,
    inputs?: ReadonlyMap<string, unknown> | null,
    signal?: AbortSignal,
  ): Promise<WorkflowExecution>;
  getAsync(runId: string, signal?: AbortSignal): Promise<WorkflowExecution | null>;
  cancelAsync(runId: string, signal?: AbortSignal): Promise<void>;
}

/** Checkpoints + restores per-step workflow state. Mirrors C# `IWorkflowState`. */
export interface IWorkflowState {
  readonly backendId: string;
  checkpointAsync(payload: CheckpointPayload, signal?: AbortSignal): Promise<void>;
  loadAsync(runId: string, stepId: string, signal?: AbortSignal): Promise<CheckpointPayload | null>;
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory implementations (mirrors the sibling boards' InMemory pattern)
// ─────────────────────────────────────────────────────────────────────────────

/** In-memory {@link IWorkflowDefinitionStore}. */
export class InMemoryWorkflowDefinitionStore implements IWorkflowDefinitionStore {
  private readonly items = new Map<string, WorkflowDefinition>();
  readonly backendId = "in-memory";

  upsertAsync(d: WorkflowDefinition, _signal?: AbortSignal): Promise<void> {
    if (d == null) throw new Error("definition required");
    if (isBlank(d.definitionId)) throw new Error("DefinitionId required");
    this.items.set(d.definitionId, d);
    return Promise.resolve();
  }

  getAsync(id: string, _signal?: AbortSignal): Promise<WorkflowDefinition | null> {
    if (isBlank(id)) throw new Error("id required");
    return Promise.resolve(this.items.get(id) ?? null);
  }
}

/**
 * In-memory {@link IWorkflowRunner} — starts a run in the Running phase, tracks
 * it, and flips it to Failed on cancel (mirroring the deterministic, no-engine
 * behaviour of the sibling in-memory runners).
 */
export class InMemoryWorkflowRunner implements IWorkflowRunner {
  private readonly runs = new Map<string, WorkflowExecution>();
  private seq = 0;
  readonly backendId = "in-memory";

  startAsync(
    definitionId: string,
    _inputs?: ReadonlyMap<string, unknown> | null,
    _signal?: AbortSignal,
  ): Promise<WorkflowExecution> {
    if (isBlank(definitionId)) throw new Error("definitionId required");
    const runId = `wf-${++this.seq}`;
    const run = workflowExecution(runId, definitionId, WorkflowPhase.Running, new Date(), null);
    this.runs.set(runId, run);
    return Promise.resolve(run);
  }

  getAsync(runId: string, _signal?: AbortSignal): Promise<WorkflowExecution | null> {
    if (isBlank(runId)) throw new Error("runId required");
    return Promise.resolve(this.runs.get(runId) ?? null);
  }

  cancelAsync(runId: string, _signal?: AbortSignal): Promise<void> {
    if (isBlank(runId)) throw new Error("runId required");
    const run = this.runs.get(runId);
    if (run !== undefined) {
      this.runs.set(runId, { ...run, phase: WorkflowPhase.Failed, failureReason: "cancelled" });
    }
    return Promise.resolve();
  }
}

/** In-memory {@link IWorkflowState} keyed by run+step. */
export class InMemoryWorkflowState implements IWorkflowState {
  private readonly checkpoints = new Map<string, CheckpointPayload>();
  readonly backendId = "in-memory";

  private static key(runId: string, stepId: string): string {
    return `${runId}/${stepId}`;
  }

  checkpointAsync(payload: CheckpointPayload, _signal?: AbortSignal): Promise<void> {
    if (payload == null) throw new Error("payload required");
    if (isBlank(payload.runId)) throw new Error("RunId required");
    if (isBlank(payload.stepId)) throw new Error("StepId required");
    this.checkpoints.set(InMemoryWorkflowState.key(payload.runId, payload.stepId), payload);
    return Promise.resolve();
  }

  loadAsync(runId: string, stepId: string, _signal?: AbortSignal): Promise<CheckpointPayload | null> {
    if (isBlank(runId)) throw new Error("runId required");
    if (isBlank(stepId)) throw new Error("stepId required");
    return Promise.resolve(this.checkpoints.get(InMemoryWorkflowState.key(runId, stepId)) ?? null);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null implementations
// ─────────────────────────────────────────────────────────────────────────────

const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";
const MIN_DATE = new Date("0001-01-01T00:00:00Z");

/** Fail-closed {@link IWorkflowDefinitionStore}. Mirrors C# `NullWorkflowDefinitionStore`. */
export class NullWorkflowDefinitionStore implements IWorkflowDefinitionStore {
  static readonly instance = new NullWorkflowDefinitionStore();
  readonly backendId = "null";
  upsertAsync(_d: WorkflowDefinition, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  getAsync(_id: string, _signal?: AbortSignal): Promise<WorkflowDefinition | null> {
    return Promise.resolve(null);
  }
}

/** Fail-closed {@link IWorkflowRunner}. Mirrors C# `NullWorkflowRunner`. */
export class NullWorkflowRunner implements IWorkflowRunner {
  static readonly instance = new NullWorkflowRunner();
  readonly backendId = "null";
  startAsync(
    definitionId: string,
    _inputs?: ReadonlyMap<string, unknown> | null,
    _signal?: AbortSignal,
  ): Promise<WorkflowExecution> {
    return Promise.resolve(
      workflowExecution(EMPTY_GUID, definitionId, WorkflowPhase.Failed, MIN_DATE, "NullWorkflowRunner"),
    );
  }
  getAsync(_runId: string, _signal?: AbortSignal): Promise<WorkflowExecution | null> {
    return Promise.resolve(null);
  }
  cancelAsync(_runId: string, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
}

/** Fail-closed {@link IWorkflowState}. Mirrors C# `NullWorkflowState`. */
export class NullWorkflowState implements IWorkflowState {
  static readonly instance = new NullWorkflowState();
  readonly backendId = "null";
  checkpointAsync(_p: CheckpointPayload, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  loadAsync(_runId: string, _stepId: string, _signal?: AbortSignal): Promise<CheckpointPayload | null> {
    return Promise.resolve(null);
  }
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}

// ═════════════════════════════════════════════════════════════════════════════
// PACA modules (paca port) — per-file modules, re-exported here as the barrel.
// ═════════════════════════════════════════════════════════════════════════════

// Conversation state machine + OpenHands runtime contract (PacaConversations.cs).
export {
  ConversationState,
  conversationStep,
  conversationPermissions,
  PacaConversationRuntime,
  anyAbort,
} from "./conversations";
export type {
  AgentConversation,
  ConversationStep,
  ConversationPermissions,
  IConversationExecutor,
} from "./conversations";

// Project + task primitives (PacaProjects.cs).
export { InMemoryPacaStore, taskReference } from "./paca_projects";
export type { PacaProject, PacaTask } from "./paca_projects";

// Sprintboard surface (PacaBoards.cs).
export { SprintState, statusColumn, PacaBoard } from "./paca_boards";
export type { StatusColumn, PacaSprint, TaskBoardMetadata, BoardView } from "./paca_boards";

// Auth: HMAC-SHA256 JWT + API keys (PacaAuth.cs).
export { HmacJwtAuthenticator, PacaApiKeyAuthenticator } from "./paca_auth";
export type { JwtPair, JwtPayload, PacaApiKeyRecord, IssuedApiKey } from "./paca_auth";

// AI agents as project members + 5 preset templates (PacaAgents.cs).
export { MemberKind, AgentTemplates, InMemoryPacaMemberStore } from "./paca_agents";
export type {
  ProjectMember,
  AgentLlmConfig,
  AgentSystemPrompts,
  AgentCapabilities,
  AgentLimits,
  AgentGitIdentity,
  AgentTriggers,
  AgentProfile,
} from "./paca_agents";

// Living documents with versions, activity, links, @mentions (PacaDocs.cs).
export { PacaDocService } from "./paca_docs";
export type { DocNode, DocVersion, DocActivity, DocLink, DocDiff } from "./paca_docs";

// Plugin runtime + manifest + lifecycle (PacaPlugins.cs).
export { PluginExtensionPoint, pluginResourceLimits, PacaPluginRegistry } from "./paca_plugins";
export type { PluginResourceLimits, PluginManifest, InstalledPlugin, IPluginRuntimeHost } from "./paca_plugins";

// MCP server: tool registry + per-agent config (PacaMcp.cs).
export { McpTransportKind, pacaMcpTool, PacaMcpServer, PacaCoreMcpTools } from "./paca_mcp";
export type { AgentMcpConfig, PacaMcpTool, PacaMcpHandler } from "./paca_mcp";

// Realtime fan-out: permission-aware rooms + query invalidation (PacaRealtime.cs).
export {
  RealtimePacaEventKind,
  taskUpdatedEvent,
  queryInvalidationEvent,
  docCursorMoveEvent,
  agentActivityEvent,
  conversationStepEvent,
  PacaRealtimeHub,
  QueryInvalidation,
} from "./paca_realtime";
export type {
  RealtimePacaEvent,
  TaskUpdatedEvent,
  QueryInvalidationEvent,
  DocCursorMoveEvent,
  AgentActivityEvent,
  ConversationStepEvent,
  IRealtimeBroadcaster,
  PermissionCheck,
} from "./paca_realtime";

// Eleven built-in Claude Code skills + installer (PacaSkills.cs).
export {
  pacaSkill,
  skillToMarkdown,
  skillToBodyOnly,
  SkillTemplates,
  PacaSkillLibrary,
  NullSkillFileStore,
  PacaSkillInstaller,
} from "./paca_skills";
export type { PacaSkill, ISkillFileStore } from "./paca_skills";

// Single-command install: docker compose + .env generation (PacaDeploy.cs).
export { PacaDeployMode, pacaDeployOverrides, PacaDeployer } from "./paca_deploy";
export type { PacaDeployOverrides, PacaDeployArtifact } from "./paca_deploy";
