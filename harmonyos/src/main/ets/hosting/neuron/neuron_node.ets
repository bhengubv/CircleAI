/**
 * hosting/neuron/neuron_node
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/neuron/neuron_node.ts
//
// The Neuron facade — TS port of CircleAI.Hosting.Neuron.NeuronNode. Composes
// the concierge (a router-gated two-slot AIService) behind the host-neutral
// IChatRuntime / IPersistableChatRuntime seam, and exposes the underlying brain
// (IAIService) so CompanionSession can still sit on top. Persists the
// generalist floor's KV session for OOM/restart survival (the specialist is
// rebuildable from the registry).

import { tmpdir } from "../../platform";
import { join } from "../../platform";
import type { ChatMessage } from "../../models/index";
import type { IAIService } from "../service";
import type {
  IPersistableChatRuntime,
  ChatTurn,
} from "../chat_runtime";

/** What NeuronNode needs from its brain beyond the base IAIService surface. */
export interface INeuronBrain extends IAIService {
  /** Resolved generalist model id (engine label), if known. */
  readonly resolvedModelIdValue?: string | null;
  saveSessionAsync?(path: string): Promise<boolean>;
  loadSessionAsync?(path: string): Promise<boolean>;
}

/**
 * A complete, host-neutral Neuron node over an {@link IAIService} brain.
 * Mirrors NeuronNode : IChatRuntime, IPersistableChatRuntime.
 */
export class NeuronNode implements IPersistableChatRuntime {
  private readonly brain: INeuronBrain;
  private readonly snapshotPath: string | null;

  constructor(
    brain: IAIService,
    opts: { sessionSnapshotPath?: string | null } = {},
  ) {
    if (!brain) throw new Error("brain (IAIService) is required");
    this.brain = brain as INeuronBrain;
    // Default to a stable per-node snapshot path so the floor survives restarts.
    this.snapshotPath =
      opts.sessionSnapshotPath ??
      join(tmpdir(), "circleai-neuron-session.bin");
  }

  /** The underlying brain, so CompanionSession can compose over the Neuron. */
  get brainService(): IAIService {
    return this.brain;
  }

  get id(): string {
    return "circleai-neuron";
  }

  get engineLabel(): string {
    const id = this.brain.resolvedModelIdValue;
    return id != null && id.length > 0
      ? `circleai-neuron:${id}`
      : "circleai-neuron";
  }

  get isReady(): boolean {
    return this.brain.isReady;
  }

  get statusMessage(): string {
    return this.brain.isReady ? "ready" : "loading model…";
  }

  get sessionSnapshotPath(): string | null {
    return this.snapshotPath;
  }

  async *streamAsync(turns: readonly ChatTurn[]): AsyncGenerator<string> {
    const messages: ChatMessage[] = turns.map((t) => ({
      role: t.role,
      content: t.content,
    }));
    yield* this.brain.streamAsync(messages);
  }

  async saveSessionAsync(path: string): Promise<boolean> {
    return this.brain.saveSessionAsync != null
      ? await this.brain.saveSessionAsync(path)
      : false;
  }

  async loadSessionAsync(path: string): Promise<boolean> {
    return this.brain.loadSessionAsync != null
      ? await this.brain.loadSessionAsync(path)
      : false;
  }
}
