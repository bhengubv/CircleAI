/**
 * networking/grpc/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// networking/grpc/index.ts
// Full-parity port of CircleAI.Networking.Grpc (GrpcTransportCommons.cs).
// C# is the exact spec.
//
// Shared metadata + helpers for the gRPC network transport: channel descriptor,
// retry-policy record, connection-lifecycle enum, reconnection policy with
// exponential backoff, call-deadline math, and an in-memory call counter.
//
// NOTE: This module had no TypeScript counterpart before the StubGuard parity
// pass; it is ported here in full (base types + metrics, including the StubGuard
// additions GrpcConnectionState / GrpcReconnectPolicy (BackoffFor / ShouldRetry /
// Default) / GrpcDeadline) so the port stays at parity with the C# reference.
//
// Type mappings (C# → TS):
//   enum GrpcChannelState/…          → const enum-like
//   record                           → readonly interface (+ positional factory)
//   TimeSpan                         → number (milliseconds), to mirror Date/ms math
//   DateTime nowUtc/deadlineUtc      → Date
//   IReadOnlyList<string>            → readonly string[]
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// NUMERIC PARITY:
//   BackoffFor(attempt) = InitialBackoffMs × Multiplier^(attempt-1), capped at
//   MaxBackoffMs; attempt 1 → InitialBackoffMs; non-finite or over-cap → MaxBackoffMs.
//   TimeSpans are modelled as whole-millisecond numbers (the port-wide convention),
//   so the Default policy (200ms ×2 → 30 000ms ceiling) yields exact integers.

/** Connectivity state of a gRPC channel. Mirrors C# `GrpcChannelState` (Idle = 0). */
export type GrpcChannelState = 0 | 1 | 2 | 3 | 4;
/** Frozen value object for {@link GrpcChannelState} members. */
export const GrpcChannelState = Object.freeze({
  Idle: 0,
  Connecting: 1,
  Ready: 2,
  TransientFailure: 3,
  Shutdown: 4,
} as const) satisfies Record<string, GrpcChannelState>;

/**
 * Lifecycle state of a managed gRPC connection, mirroring the connectivity
 * states a channel steps through as reconnection is driven. Mirrors C#
 * `GrpcConnectionState` (Idle = 0).
 */
export type GrpcConnectionState = 0 | 1 | 2 | 3 | 4;
/** Frozen value object for {@link GrpcConnectionState} members. */
export const GrpcConnectionState = Object.freeze({
  Idle: 0,
  Connecting: 1,
  Ready: 2,
  TransientFailure: 3,
  Shutdown: 4,
} as const) satisfies Record<string, GrpcConnectionState>;

/** A gRPC channel descriptor. Mirrors C# `GrpcChannelDescriptor` record. */
export interface GrpcChannelDescriptor {
  readonly target: string;
  readonly useTls: boolean;
  readonly maxReceiveBytes: number;
  readonly maxSendBytes: number;
  /** Keep-alive interval in milliseconds (C# `TimeSpan KeepAliveInterval`). */
  readonly keepAliveIntervalMs: number;
}

/** Constructs a {@link GrpcChannelDescriptor}. */
export function grpcChannelDescriptor(
  target: string,
  useTls: boolean,
  maxReceiveBytes: number,
  maxSendBytes: number,
  keepAliveIntervalMs: number,
): GrpcChannelDescriptor {
  return { target, useTls, maxReceiveBytes, maxSendBytes, keepAliveIntervalMs };
}

/** A gRPC retry policy. Mirrors C# `GrpcRetryPolicy` record. */
export interface GrpcRetryPolicy {
  readonly maxAttempts: number;
  /** Initial backoff in milliseconds (C# `TimeSpan InitialBackoff`). */
  readonly initialBackoffMs: number;
  /** Maximum backoff in milliseconds (C# `TimeSpan MaxBackoff`). */
  readonly maxBackoffMs: number;
  readonly multiplier: number;
  readonly retryableStatusCodes: readonly string[];
}

/** Constructs a {@link GrpcRetryPolicy}. */
export function grpcRetryPolicy(
  maxAttempts: number,
  initialBackoffMs: number,
  maxBackoffMs: number,
  multiplier: number,
  retryableStatusCodes: readonly string[],
): GrpcRetryPolicy {
  return { maxAttempts, initialBackoffMs, maxBackoffMs, multiplier, retryableStatusCodes };
}

/** A summary of a completed gRPC call. Mirrors C# `GrpcCallSummary` record. */
export interface GrpcCallSummary {
  readonly method: string;
  readonly attempts: number;
  /** Latency in milliseconds (C# `TimeSpan Latency`). */
  readonly latencyMs: number;
  readonly statusCode: string;
  /** UTC instant the call completed (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link GrpcCallSummary}. */
export function grpcCallSummary(
  method: string,
  attempts: number,
  latencyMs: number,
  statusCode: string,
  atUtc: Date,
): GrpcCallSummary {
  return { method, attempts, latencyMs, statusCode, atUtc };
}

/** Well-known retry policies. Mirrors C# `GrpcRetryPolicies`. */
export const GrpcRetryPolicies = {
  Default: grpcRetryPolicy(3, 100, 2000, 2.0, ["UNAVAILABLE", "DEADLINE_EXCEEDED"]),
  Aggressive: grpcRetryPolicy(6, 50, 5000, 2.0, ["UNAVAILABLE", "DEADLINE_EXCEEDED", "RESOURCE_EXHAUSTED"]),
  NoRetry: grpcRetryPolicy(1, 0, 0, 1.0, []),
} as const;

/**
 * Reconnection strategy for a managed gRPC channel: how many attempts to make
 * and how to grow the backoff between them. Mirrors C# `GrpcReconnectPolicy`.
 * Backoff durations are milliseconds.
 */
export class GrpcReconnectPolicy {
  constructor(
    readonly maxAttempts: number,
    /** Initial backoff, in milliseconds (C# `TimeSpan InitialBackoff`). */
    readonly initialBackoffMs: number,
    readonly backoffMultiplier: number,
    /** Backoff ceiling, in milliseconds (C# `TimeSpan MaxBackoff`). */
    readonly maxBackoffMs: number,
  ) {}

  /** A sane default: 5 attempts, 200ms growing ×2 up to a 30 000ms ceiling. Mirrors C# `Default`. */
  static readonly Default = new GrpcReconnectPolicy(5, 200, 2.0, 30_000);

  /**
   * Backoff (ms) before a given 1-based attempt:
   * `initialBackoffMs × multiplier^(attempt-1)`, capped at `maxBackoffMs`.
   * Attempt 1 returns `initialBackoffMs`. Non-finite or over-cap values return
   * `maxBackoffMs`. Mirrors C# `BackoffFor`.
   */
  backoffFor(attempt: number): number {
    if (attempt < 1) throw new Error("attempt is 1-based");
    const scaled = this.initialBackoffMs * this.backoffMultiplier ** (attempt - 1);
    const capMs = this.maxBackoffMs;
    if (!Number.isFinite(scaled) || scaled > capMs) return this.maxBackoffMs;
    return scaled;
  }

  /** True when the 1-based attempt number is still within the retry budget. Mirrors C# `ShouldRetry`. */
  shouldRetry(attempt: number): boolean {
    return attempt < this.maxAttempts;
  }
}

/**
 * Deadline math for gRPC calls: turns a relative timeout into the absolute UTC
 * instant a call must complete by, and reports remaining time against a clock.
 * Timeouts and remaining durations are milliseconds. Mirrors C# `GrpcDeadline`.
 */
export const GrpcDeadline = {
  /** Absolute deadline for a call started at `nowUtc` with the given timeout (ms). Mirrors C# `FromTimeout`. */
  fromTimeout(timeoutMs: number, nowUtc: Date): Date {
    if (timeoutMs < 0) throw new Error("timeout must be non-negative");
    return new Date(nowUtc.getTime() + timeoutMs);
  },

  /** Time left (ms) before `deadlineUtc`, clamped to zero once passed. Mirrors C# `Remaining`. */
  remaining(deadlineUtc: Date, nowUtc: Date): number {
    const left = deadlineUtc.getTime() - nowUtc.getTime();
    return left > 0 ? left : 0;
  },

  /** True once `nowUtc` has reached or passed the deadline. Mirrors C# `IsExpired`. */
  isExpired(deadlineUtc: Date, nowUtc: Date): boolean {
    return nowUtc.getTime() >= deadlineUtc.getTime();
  },
} as const;

/** In-memory gRPC call metrics + channel registry. Mirrors C# `InMemoryGrpcCallMetrics`. */
export class InMemoryGrpcCallMetrics {
  private readonly channels = new Map<string, GrpcChannelDescriptor>();
  private readonly states = new Map<string, GrpcChannelState>();
  private readonly calls: GrpcCallSummary[] = [];
  private seq = 0;

  registerChannel(id: string, d: GrpcChannelDescriptor): void {
    if (d == null) throw new Error("d required");
    this.channels.set(id, d);
  }

  getChannel(id: string): GrpcChannelDescriptor | undefined {
    return this.channels.get(id);
  }

  setState(id: string, s: GrpcChannelState): void {
    this.states.set(id, s);
  }

  state(id: string): GrpcChannelState {
    return this.states.get(id) ?? GrpcChannelState.Idle;
  }

  /** Record a completed call and return a monotonic call id. Mirrors C# `LogCall`. */
  logCall(c: GrpcCallSummary): string {
    if (c == null) throw new Error("c required");
    this.calls.push(c);
    return `grpc-${++this.seq}`;
  }

  /** The most recent calls, newest-first, capped at `limit` (default 50). Mirrors C# `RecentCalls`. */
  recentCalls(limit = 50): readonly GrpcCallSummary[] {
    return [...this.calls].sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime()).slice(0, limit);
  }
}
