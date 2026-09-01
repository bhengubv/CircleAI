/**
 * hosting/chat_runtime
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/chat_runtime.ts
//
// Host-neutral chat runtime seam — the TS port of
// CircleAI.Hosting.Chat.IChatRuntime (pulled down verbatim from
// circle-concierge). Zero CircleAI-internal deps: Object harness can drive a
// Neuron node through this surface without touching engine internals.

/** One turn in a host-neutral conversation. Mirrors ChatTurn(role, content). */
export interface ChatTurn {
  readonly role: string;
  readonly content: string;
}

/** Construct a ChatTurn (parity with the C# record ctor). */
export function chatTurn(role: string, content: string): ChatTurn {
  return { role, content };
}

/**
 * A host-neutral chat engine. Mirrors CircleAI.Hosting.Chat.IChatRuntime:
 * identity + readiness + a single streaming entrypoint.
 */
export interface IChatRuntime {
  /** Stable identifier for this runtime. */
  readonly id: string;
  /** Human-readable engine label (e.g. the resolved model id). */
  readonly engineLabel: string;
  /** True once the underlying engine is loaded and can serve. */
  readonly isReady: boolean;
  /** Short status string for UIs ("loading model…", "ready", …). */
  readonly statusMessage: string;
  /** Stream an assistant reply for the given turns, piece by piece. */
  streamAsync(turns: readonly ChatTurn[]): AsyncGenerator<string>;
}

/**
 * A chat runtime whose session (KV cache) can be snapshotted to disk and
 * restored — RT-02 durability. Mirrors IPersistableChatRuntime.
 */
export interface IPersistableChatRuntime extends IChatRuntime {
  /** Where this runtime persists its session snapshot, or null if none. */
  readonly sessionSnapshotPath: string | null;
  /** Persist the current session. Returns true on success. */
  saveSessionAsync(path: string): Promise<boolean>;
  /** Restore a previously-saved session. Returns true on success. */
  loadSessionAsync(path: string): Promise<boolean>;
}

/** Type guard: does this runtime implement the persistable surface? */
export function isPersistable(
  rt: IChatRuntime,
): rt is IPersistableChatRuntime {
  const p = rt as Partial<IPersistableChatRuntime>;
  return (
    typeof p.saveSessionAsync === "function" &&
    typeof p.loadSessionAsync === "function"
  );
}

/**
 * A runtime that is never ready and streams a single "no engine" notice.
 * Mirrors NullChatRuntime — the safe default when no engine is wired.
 */
export class NullChatRuntime implements IChatRuntime {
  get id(): string {
    return "null-chat-runtime";
  }
  get engineLabel(): string {
    return "none";
  }
  get isReady(): boolean {
    return false;
  }
  get statusMessage(): string {
    return "No chat engine configured.";
  }
  // eslint-disable-next-line require-yield
  async *streamAsync(_turns: readonly ChatTurn[]): AsyncGenerator<string> {
    yield "No chat engine is configured.";
  }
}
