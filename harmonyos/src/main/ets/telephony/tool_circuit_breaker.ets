/**
 * telephony/tool_circuit_breaker
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// telephony/tool_circuit_breaker.ts
//
// Per-tool circuit breaker + timeout wrapper around any IToolCallRegistry —
// faithful port of ToolCircuitBreaker.cs. Three states: Closed (normal), Open
// (failing — reject immediately), HalfOpen (one trial allowed). Each tool has
// its own breaker state — a broken billing API doesn't cut off the order-lookup
// API.
//
// The C# links a per-call timeout CTS with the caller's token and, on timeout,
// distinguishes it from a caller-cancellation. We reproduce that with
// {@link LinkedTimeout}: `timedOut` is true only when our timeout fired and the
// parent signal did not.

import type {
  IToolCallRegistry,
  LocalToolHandler,
  ToolDefinition,
  ToolInvocation,
  ToolResult,
} from "./tool_calling";
import { toolResult } from "./tool_calling";
import { LinkedTimeout, isCancellation, utcNow } from "./internal";

/** Per-tool timeout + breaker thresholds. Mirrors `ToolCallPolicy`. */
export interface ToolCallPolicy {
  /** Wall-clock ceiling for the call, in ms. Default 5000 ms. */
  readonly timeoutMs?: number;
  /** Consecutive failures that trip the breaker. Default 3. */
  readonly failureThreshold?: number;
  /** How long the breaker stays open before half-opening, in ms. Default 30000 ms. */
  readonly openDurationMs?: number;
}

function timeoutOrDefault(p: ToolCallPolicy): number {
  return p.timeoutMs ?? 5000;
}
function failureThresholdOrDefault(p: ToolCallPolicy): number {
  return p.failureThreshold ?? 3;
}
function openDurationOrDefault(p: ToolCallPolicy): number {
  return p.openDurationMs ?? 30000;
}

/** Breaker state. Mirrors `ToolBreakerState`. */
export const ToolBreakerState = {
  Closed: "Closed",
  Open: "Open",
  HalfOpen: "HalfOpen",
} as const;
export type ToolBreakerState = (typeof ToolBreakerState)[keyof typeof ToolBreakerState];

class BreakerEntry {
  private consecutiveFailures = 0;
  private openedAt: Date = new Date(0);
  private isOpen = false;

  currentState(now: Date, openDurationMs: number): ToolBreakerState {
    if (!this.isOpen) return ToolBreakerState.Closed;
    if (now.getTime() - this.openedAt.getTime() >= openDurationMs) return ToolBreakerState.HalfOpen;
    return ToolBreakerState.Open;
  }

  recordSuccess(): void {
    this.consecutiveFailures = 0;
    this.isOpen = false;
  }

  recordFailure(threshold: number, now: Date): void {
    this.consecutiveFailures += 1;
    if (this.consecutiveFailures >= threshold) {
      this.isOpen = true;
      this.openedAt = now;
    }
  }
}

/**
 * Decorates an {@link IToolCallRegistry} with per-tool timeouts and a circuit
 * breaker. Pass a `clock` for deterministic tests. Mirrors
 * `CircuitBreakerToolRegistry`.
 */
export class CircuitBreakerToolRegistry implements IToolCallRegistry {
  private readonly inner: IToolCallRegistry;
  private readonly defaultPolicy: ToolCallPolicy;
  private readonly policies = new Map<string, ToolCallPolicy>(); // key: lowercased tool name
  private readonly breakers = new Map<string, BreakerEntry>(); // key: lowercased tool name
  private readonly clock: () => Date;

  constructor(inner: IToolCallRegistry, defaultPolicy?: ToolCallPolicy, clock?: () => Date) {
    if (inner === null || inner === undefined) throw new Error("inner is required");
    this.inner = inner;
    this.defaultPolicy = defaultPolicy ?? {};
    this.clock = clock ?? utcNow;
  }

  /** Override the policy for a specific tool. */
  setPolicy(toolName: string, policy: ToolCallPolicy): void {
    if (policy === null || policy === undefined) throw new Error("policy is required");
    this.policies.set(toolName.toLowerCase(), policy);
  }

  /** Inspect the current breaker state for a tool. */
  getState(toolName: string): ToolBreakerState {
    const entry = this.breakers.get(toolName.toLowerCase());
    return entry
      ? entry.currentState(this.clock(), openDurationOrDefault(this.getPolicy(toolName)))
      : ToolBreakerState.Closed;
  }

  get definitions(): readonly ToolDefinition[] {
    return this.inner.definitions;
  }

  registerLocal(definition: ToolDefinition, handler: LocalToolHandler): void {
    this.inner.registerLocal(definition, handler);
  }

  registerWebhook(definition: ToolDefinition, webhook: string): void {
    this.inner.registerWebhook(definition, webhook);
  }

  async invokeAsync(invocation: ToolInvocation, signal?: AbortSignal): Promise<ToolResult> {
    if (invocation === null || invocation === undefined) throw new Error("invocation is required");
    const policy = this.getPolicy(invocation.toolName);
    const key = invocation.toolName.toLowerCase();
    let entry = this.breakers.get(key);
    if (entry === undefined) {
      entry = new BreakerEntry();
      this.breakers.set(key, entry);
    }

    const state = entry.currentState(this.clock(), openDurationOrDefault(policy));
    if (state === ToolBreakerState.Open) {
      return toolResult(
        invocation.callId,
        false,
        "{}",
        `Tool '${invocation.toolName}' is circuit-broken; retry after the breaker resets.`,
      );
    }

    const linked = new LinkedTimeout(signal, timeoutOrDefault(policy));
    try {
      const result = await this.inner.invokeAsync(invocation, linked.signal);
      if (result.succeeded) {
        entry.recordSuccess();
      } else {
        entry.recordFailure(failureThresholdOrDefault(policy), this.clock());
      }
      return result;
    } catch (ex) {
      entry.recordFailure(failureThresholdOrDefault(policy), this.clock());
      if (isCancellation(ex) && linked.timedOut) {
        return toolResult(
          invocation.callId,
          false,
          "{}",
          `Tool '${invocation.toolName}' timed out after ${timeoutOrDefault(policy)} ms.`,
        );
      }
      return toolResult(invocation.callId, false, "{}", ex instanceof Error ? ex.message : String(ex));
    } finally {
      linked.dispose();
    }
  }

  private getPolicy(toolName: string): ToolCallPolicy {
    return this.policies.get(toolName.toLowerCase()) ?? this.defaultPolicy;
  }
}
