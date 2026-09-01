/**
 * microagents/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// microagents/index.ts
// Full-parity port of CircleAI.MicroAgents (C#). C# is the exact spec.
//
// Micro-agent contracts: MicroAgentDescriptor / MicroAgentResponse records, the
// IMicroAgent + IMicroAgentHost contracts, a FuncMicroAgent lambda wrapper, a
// real InMemoryMicroAgentHost registry+router, a NullMicroAgent, and the
// capability-search + invocation-log helpers.
//
// Type mappings (C# → TS):
//   IReadOnlyList<string> Capabilities        → readonly string[]
//   IReadOnlyDictionary<string,string>? Meta  → ReadonlyMap<string,string> | null
//   ValueTask<MicroAgentResponse>             → Promise<MicroAgentResponse>
//   ConcurrentDictionary<string, IMicroAgent> → Map<string, IMicroAgent>

/** Describes a micro-agent's identity + capabilities. Mirrors C# `MicroAgentDescriptor`. */
export interface MicroAgentDescriptor {
  readonly agentId: string;
  readonly description: string;
  readonly capabilities: readonly string[];
}

/** Constructs a {@link MicroAgentDescriptor}. */
export function microAgentDescriptor(
  agentId: string,
  description: string,
  capabilities: readonly string[],
): MicroAgentDescriptor {
  return { agentId, description, capabilities };
}

/** The output of a micro-agent invocation. Mirrors C# `MicroAgentResponse`. */
export interface MicroAgentResponse {
  readonly agentId: string;
  readonly output: string;
  readonly metadata: ReadonlyMap<string, string> | null;
}

/** Constructs a {@link MicroAgentResponse} (metadata defaults to null). */
export function microAgentResponse(
  agentId: string,
  output: string,
  metadata: ReadonlyMap<string, string> | null = null,
): MicroAgentResponse {
  return { agentId, output, metadata };
}

/** A single-purpose agent. Mirrors C# `IMicroAgent`. */
export interface IMicroAgent {
  readonly agentId: string;
  readonly backendId: string;
  readonly descriptor: MicroAgentDescriptor;
  invokeAsync(input: string, signal?: AbortSignal): Promise<MicroAgentResponse>;
}

/** Registry + router for micro-agents. Mirrors C# `IMicroAgentHost`. */
export interface IMicroAgentHost {
  readonly backendId: string;
  register(agent: IMicroAgent): void;
  list(): readonly MicroAgentDescriptor[];
  invokeAsync(agentId: string, input: string, signal?: AbortSignal): Promise<MicroAgentResponse | null>;
}

/**
 * Wraps a delegate in an {@link IMicroAgent} so callers can register lambdas.
 * Mirrors C# `FuncMicroAgent`.
 */
export class FuncMicroAgent implements IMicroAgent {
  readonly agentId: string;
  readonly descriptor: MicroAgentDescriptor;
  private readonly impl: (input: string, signal?: AbortSignal) => Promise<MicroAgentResponse>;

  constructor(
    agentId: string,
    description: string,
    capabilities: readonly string[] | null,
    impl: (input: string, signal?: AbortSignal) => Promise<MicroAgentResponse>,
  ) {
    if (isBlank(agentId)) throw new Error("agentId required");
    if (impl == null) throw new Error("impl required");
    this.agentId = agentId;
    this.descriptor = microAgentDescriptor(agentId, description ?? "", capabilities ?? []);
    this.impl = impl;
  }

  get backendId(): string {
    return "func";
  }

  invokeAsync(input: string, signal?: AbortSignal): Promise<MicroAgentResponse> {
    return this.impl(input, signal);
  }
}

/** No-op {@link IMicroAgent}. Mirrors C# `NullMicroAgent`. */
export class NullMicroAgent implements IMicroAgent {
  get agentId(): string {
    return "null";
  }
  get backendId(): string {
    return "null";
  }
  readonly descriptor: MicroAgentDescriptor = microAgentDescriptor(
    "null",
    "No-op micro agent",
    [],
  );
  invokeAsync(_input: string, _signal?: AbortSignal): Promise<MicroAgentResponse> {
    return Promise.resolve(microAgentResponse(this.agentId, ""));
  }
}

/** Real in-memory {@link IMicroAgentHost} registry+router. Mirrors C# `InMemoryMicroAgentHost`. */
export class InMemoryMicroAgentHost implements IMicroAgentHost {
  get backendId(): string {
    return "in-memory";
  }
  private readonly agents = new Map<string, IMicroAgent>();

  register(agent: IMicroAgent): void {
    this.agents.set(agent.agentId, agent);
  }

  list(): readonly MicroAgentDescriptor[] {
    return [...this.agents.values()].map((a) => a.descriptor);
  }

  async invokeAsync(
    agentId: string,
    input: string,
    signal?: AbortSignal,
  ): Promise<MicroAgentResponse | null> {
    const a = this.agents.get(agentId);
    if (a !== undefined) return a.invokeAsync(input, signal);
    return null;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers — capability search + invocation log
// ─────────────────────────────────────────────────────────────────────────────

/** One recorded micro-agent invocation. Mirrors C# `MicroAgentInvocation`. */
export interface MicroAgentInvocation {
  readonly agentId: string;
  readonly input: string;
  readonly responseText: string;
  readonly atUtc: Date;
}

/** Constructs a {@link MicroAgentInvocation}. */
export function microAgentInvocation(
  agentId: string,
  input: string,
  responseText: string,
  atUtc: Date,
): MicroAgentInvocation {
  return { agentId, input, responseText, atUtc };
}

/** Capability filter over descriptors. Mirrors C# `MicroAgentSearch`. */
export const MicroAgentSearch = {
  /** Find agents advertising a capability tag (case-insensitive), sorted by agentId. */
  byCapability(
    all: Iterable<MicroAgentDescriptor>,
    capability: string,
  ): readonly MicroAgentDescriptor[] {
    if (all == null) throw new Error("all required");
    if (isBlank(capability)) throw new Error("capability required");
    const cap = capability.toLowerCase();
    return [...all]
      .filter((d) => d.capabilities.some((c) => c.toLowerCase() === cap))
      .sort((a, b) => (a.agentId < b.agentId ? -1 : a.agentId > b.agentId ? 1 : 0));
  },

  /** Substring search over id / description / capabilities; first `topK` matches. */
  search(
    all: Iterable<MicroAgentDescriptor>,
    query: string,
    topK = 10,
  ): readonly MicroAgentDescriptor[] {
    if (all == null) throw new Error("all required");
    if (query == null) throw new Error("query required");
    if (topK <= 0) throw new Error("topK out of range");
    const q = query.toLowerCase();
    const out: MicroAgentDescriptor[] = [];
    for (const d of all) {
      if (
        d.agentId.toLowerCase().includes(q) ||
        d.description.toLowerCase().includes(q) ||
        d.capabilities.some((c) => c.toLowerCase().includes(q))
      ) {
        out.push(d);
        if (out.length >= topK) break;
      }
    }
    return out;
  },
} as const;

/** Keeps an in-memory invocation log. Mirrors C# `MicroAgentInvocationLog`. */
export class MicroAgentInvocationLog {
  private readonly items: MicroAgentInvocation[] = [];

  append(i: MicroAgentInvocation): void {
    if (i == null) throw new Error("invocation required");
    this.items.push(i);
  }

  /** Most-recent-first invocations for an agent, up to `limit`. */
  forAgent(agentId: string, limit = 50): readonly MicroAgentInvocation[] {
    if (limit <= 0) throw new Error("limit out of range");
    return this.items
      .filter((i) => i.agentId === agentId)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime())
      .slice(0, limit);
  }

  get totalInvocations(): number {
    return this.items.length;
  }
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
