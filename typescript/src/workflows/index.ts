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

// ═════════════════════════════════════════════════════════════════════════════
// Conversation state machine
// ═════════════════════════════════════════════════════════════════════════════

/** Conversation lifecycle state. Mirrors C# `ConversationState`. */
export enum ConversationState {
  Queued = 0,
  Running = 1,
  Finished = 2,
  Failed = 3,
  Stopped = 4,
}

/** One conversation between a human + an agent (or agents). Mirrors C# `AgentConversation`. */
export interface AgentConversation {
  readonly id: string;
  readonly projectId: string;
  readonly agentMemberId: string;
  readonly humanMemberId: string | null;
  readonly openingPrompt: string;
  readonly state: ConversationState;
  readonly queuedAtUtc: Date;
  readonly startedAtUtc: Date | null;
  readonly finishedAtUtc: Date | null;
  readonly resultJson: string | null;
  readonly failureReason: string | null;
}

/** One executed step in a conversation. Mirrors C# `ConversationStep`. */
export interface ConversationStep {
  readonly conversationId: string;
  readonly order: number;
  /** "user" / "agent" / "tool". */
  readonly speaker: string;
  readonly contentJson: string;
  readonly at: Date;
}

/** Constructs a {@link ConversationStep}. */
export function conversationStep(
  conversationId: string,
  order: number,
  speaker: string,
  contentJson: string,
  at: Date,
): ConversationStep {
  return { conversationId, order, speaker, contentJson, at };
}

/** Permission flags required to run risky actions. Mirrors C# `ConversationPermissions`. */
export interface ConversationPermissions {
  readonly allowCloneRepos: boolean;
  readonly allowCreatePr: boolean;
}

/** Constructs a {@link ConversationPermissions}. */
export function conversationPermissions(
  allowCloneRepos: boolean,
  allowCreatePr: boolean,
): ConversationPermissions {
  return { allowCloneRepos, allowCreatePr };
}

/**
 * Host-supplied executor — invokes the real agent runtime per conversation,
 * emitting {@link ConversationStep} events as work progresses. Mirrors C#
 * `IConversationExecutor`.
 */
export interface IConversationExecutor {
  runAsync(
    conversation: AgentConversation,
    permissions: ConversationPermissions,
    onStep: (step: ConversationStep) => void,
    signal?: AbortSignal,
  ): Promise<void>;
}

/**
 * Conversation registry + state machine. Owns the state transitions, history,
 * and lifecycle; the actual isolation/SDK integration is host-supplied via
 * {@link IConversationExecutor}. Mirrors C# `PacaConversationRuntime`.
 */
export class PacaConversationRuntime {
  private readonly conversations = new Map<string, AgentConversation>();
  private readonly steps = new Map<string, ConversationStep[]>();
  private readonly running = new Map<string, AbortController>();
  private readonly executor: IConversationExecutor;
  private readonly clock: () => Date;

  constructor(executor: IConversationExecutor, clock?: (() => Date) | null) {
    if (executor == null) throw new Error("executor required");
    this.executor = executor;
    this.clock = clock ?? ((): Date => new Date());
  }

  /** Enqueue a new conversation. Throws if the id already exists. Mirrors C# `Queue`. */
  queue(
    id: string,
    projectId: string,
    agentMemberId: string,
    openingPrompt: string,
    humanMemberId: string | null = null,
  ): AgentConversation {
    if (this.conversations.has(id)) throw new Error(`Conversation '${id}' already exists.`);
    const c: AgentConversation = {
      id,
      projectId,
      agentMemberId,
      humanMemberId,
      openingPrompt: openingPrompt ?? "",
      state: ConversationState.Queued,
      queuedAtUtc: this.clock(),
      startedAtUtc: null,
      finishedAtUtc: null,
      resultJson: null,
      failureReason: null,
    };
    this.conversations.set(id, c);
    this.steps.set(id, []);
    return c;
  }

  /** Get a conversation by id, or null. Mirrors C# `Get`. */
  get(id: string): AgentConversation | null {
    return this.conversations.get(id) ?? null;
  }

  /** Snapshot of a conversation's executed steps. Mirrors C# `Steps`. */
  getSteps(id: string): readonly ConversationStep[] {
    const list = this.steps.get(id);
    return list === undefined ? [] : [...list];
  }

  /** Begin executing a queued conversation. Mirrors C# `StartAsync`. */
  async startAsync(
    id: string,
    permissions: ConversationPermissions,
    outerSignal?: AbortSignal,
  ): Promise<void> {
    const current = this.conversations.get(id);
    if (current === undefined || current.state !== ConversationState.Queued) {
      throw new Error(`Conversation '${id}' is not in Queued state.`);
    }
    const started: AgentConversation = {
      ...current,
      state: ConversationState.Running,
      startedAtUtc: this.clock(),
    };
    this.conversations.set(id, started);

    const controller = new AbortController();
    this.running.set(id, controller);
    const linked = anyAbort(outerSignal, controller.signal);

    try {
      await this.executor.runAsync(
        started,
        permissions,
        (step) => {
          const list = this.steps.get(id);
          if (list !== undefined) list.push(step);
        },
        linked,
      );
      this.conversations.set(id, {
        ...started,
        state: ConversationState.Finished,
        finishedAtUtc: this.clock(),
        resultJson: "{}",
      });
    } catch (ex) {
      if (controller.signal.aborted) {
        this.conversations.set(id, {
          ...started,
          state: ConversationState.Stopped,
          finishedAtUtc: this.clock(),
        });
      } else {
        this.conversations.set(id, {
          ...started,
          state: ConversationState.Failed,
          finishedAtUtc: this.clock(),
          failureReason: (ex as Error)?.message ?? String(ex),
        });
      }
    } finally {
      this.running.delete(id);
    }
  }

  /** Stop a running conversation from the UI. Mirrors C# `Stop`. */
  stop(id: string): void {
    const controller = this.running.get(id);
    if (controller !== undefined) controller.abort();
  }
}

/** Returns an AbortSignal that fires when any of the given signals fire. */
function anyAbort(a: AbortSignal | undefined, b: AbortSignal): AbortSignal {
  if (a === undefined) return b;
  const controller = new AbortController();
  const onAbort = (): void => controller.abort();
  if (a.aborted || b.aborted) controller.abort();
  a.addEventListener("abort", onAbort, { once: true });
  b.addEventListener("abort", onAbort, { once: true });
  return controller.signal;
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
