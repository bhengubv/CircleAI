/**
 * workflows/conversations
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// workflows/conversations.ts
//
// (3.3.0) Conversation state machine + OpenHands runtime contract + repo / PR
// helpers (PacaConversations.cs). The actual Docker isolation + OpenHands SDK
// integration is host-supplied via IConversationExecutor; this module owns the
// state machine, history, and lifecycle events.
//
// Type mappings (C# → TS):
//   DateTimeOffset / DateTimeOffset?     → Date / Date | null
//   record `with { ... }`                → object spread with overrides
//   CancellationTokenSource / linked     → AbortController + anyAbort(...)
//   ConcurrentDictionary                 → Map (single-threaded runtime)

/** (3.3.0) Conversation lifecycle state. Mirrors C# `ConversationState`. */
export enum ConversationState {
  Queued = 0,
  Running = 1,
  Finished = 2,
  Failed = 3,
  Stopped = 4,
}

/** (3.3.0) One conversation between a human + an agent (or agents). Mirrors C# `AgentConversation`. */
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

/** (3.3.0) One executed step in a conversation. Mirrors C# `ConversationStep`. */
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

/** (3.3.0) Permission flag set required to run risky actions. Mirrors C# `ConversationPermissions`. */
export interface ConversationPermissions {
  readonly allowCloneRepos: boolean;
  readonly allowCreatePr: boolean;
}

/** Constructs a {@link ConversationPermissions}. */
export function conversationPermissions(allowCloneRepos: boolean, allowCreatePr: boolean): ConversationPermissions {
  return { allowCloneRepos, allowCreatePr };
}

/**
 * (3.3.0) Host-supplied executor — invokes OpenHands SDK / Docker container per
 * conversation, emitting {@link ConversationStep} events as work progresses.
 * Mirrors C# `IConversationExecutor`.
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
 * (3.3.0) Conversation registry + state machine. Owns the state transitions,
 * history, and lifecycle; the actual isolation/SDK integration is host-supplied
 * via {@link IConversationExecutor}. Mirrors C# `PacaConversationRuntime`.
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
  async startAsync(id: string, permissions: ConversationPermissions, outerSignal?: AbortSignal): Promise<void> {
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
export function anyAbort(a: AbortSignal | undefined, b: AbortSignal): AbortSignal {
  if (a === undefined) return b;
  const controller = new AbortController();
  const onAbort = (): void => controller.abort();
  if (a.aborted || b.aborted) controller.abort();
  a.addEventListener("abort", onAbort, { once: true });
  b.addEventListener("abort", onAbort, { once: true });
  return controller.signal;
}
