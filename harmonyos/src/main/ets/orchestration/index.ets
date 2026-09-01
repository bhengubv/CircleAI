/**
 * orchestration/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// orchestration/index.ts
// Full-parity port of CircleAI.Orchestration (C#). C# is the exact spec.
//
// Loki-mode agent-swarm orchestration: AgentTask / AgentRole / AgentPriority /
// AgentStatus, the IAgentDispatcher contract, a LocalAgentDispatcher (per-role
// handler registry), the LokiOrchestrator (bounded-concurrency swarm runner
// with a quality gate), IncidentTrigger (episodic-memory + anomaly → tasks),
// and the SecurityOrchestrationBridge (ISecurityWatchdog decorator).
//
// Type mappings (C# → TS):
//   Guid Id                         → string (UUID v4)
//   IReadOnlyDictionary<string,str> → ReadonlyMap<string,string> (Map)
//   DateTimeOffset                  → Date
//   TimeSpan TaskTimeout            → number (milliseconds)
//   Task<T> / IAsyncEnumerable<T>   → Promise<T> / AsyncGenerator<T>
//   record `with { Status = … }`    → object spread with overrides

import { DeviceTierDefaults, classify, type DeviceProbe } from "../device/index";
import type { EpisodicMemoryEntry } from "../memory/index";
import {
  ThreatVector,
  type AnomalySignal,
  type ISecurityWatchdog,
  type SecurityResponse,
  type SecurityCheckpoint,
} from "../security/index";

// ─────────────────────────────────────────────────────────────────────────────
// Enums
// ─────────────────────────────────────────────────────────────────────────────

/** Domain responsibility of an agent in a swarm. Mirrors C# `AgentRole`. */
export enum AgentRole {
  Engineering = 0,
  Operations = 1,
  Review = 2,
  Security = 3,
}

/**
 * Execution priority of an agent task. Lower numeric value = higher urgency.
 * Mirrors C# `AgentPriority` (explicit values 0..3).
 */
export enum AgentPriority {
  Critical = 0,
  High = 1,
  Normal = 2,
  Low = 3,
}

/** Lifecycle status of an agent task or swarm result. Mirrors C# `AgentStatus`. */
export enum AgentStatus {
  Pending = 0,
  Running = 1,
  Passed = 2,
  Failed = 3,
  Blocked = 4,
}

// ─────────────────────────────────────────────────────────────────────────────
// Records
// ─────────────────────────────────────────────────────────────────────────────

/** A single unit of work dispatched to an agent swarm. Mirrors C# `AgentTask`. */
export interface AgentTask {
  readonly id: string;
  readonly role: AgentRole;
  readonly description: string;
  readonly priority: AgentPriority;
  readonly inputs: ReadonlyMap<string, string>;
  readonly createdAt: Date;
}

/**
 * Stamps a new {@link AgentTask} with a fresh UUID and the current UTC time.
 * Mirrors C# `AgentTask.Create`. Pass `null`/omit `inputs` for an empty set.
 */
export function createAgentTask(
  role: AgentRole,
  description: string,
  priority: AgentPriority,
  inputs?: ReadonlyMap<string, string> | null,
): AgentTask {
  return {
    id: crypto.randomUUID(),
    role,
    description,
    priority,
    inputs: inputs ?? new Map<string, string>(),
    createdAt: new Date(),
  };
}

/** The outcome produced by an agent handler for a single task. Mirrors C# `SwarmResult`. */
export interface SwarmResult {
  readonly taskId: string;
  readonly role: AgentRole;
  readonly status: AgentStatus;
  readonly output: string;
  readonly issues: readonly string[];
  readonly completedAt: Date;
}

/** Constructs a {@link SwarmResult}. */
export function swarmResult(
  taskId: string,
  role: AgentRole,
  status: AgentStatus,
  output: string,
  issues: readonly string[],
  completedAt: Date,
): SwarmResult {
  return { taskId, role, status, output, issues, completedAt };
}

/**
 * The verdict produced by {@link IAgentDispatcher.runQualityGateAsync}. Mirrors
 * C# `QualityGateResult`.
 */
export interface QualityGateResult {
  readonly passed: boolean;
  readonly blockers: readonly string[];
  readonly warnings: readonly string[];
}

/** Constructs a {@link QualityGateResult}. */
export function qualityGateResult(
  passed: boolean,
  blockers: readonly string[],
  warnings: readonly string[],
): QualityGateResult {
  return { passed, blockers, warnings };
}

// ─────────────────────────────────────────────────────────────────────────────
// AgentSwarmConfig
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Tuning parameters that govern how {@link LokiOrchestrator} schedules and
 * enforces quality gates. Mirrors C# `AgentSwarmConfig`.
 *
 * `taskTimeoutMs` is C# `TimeSpan TaskTimeout` expressed in milliseconds.
 */
export interface AgentSwarmConfig {
  readonly maxConcurrency: number;
  /** Max wall-clock time for a single task in milliseconds (C# `TaskTimeout`). */
  readonly taskTimeoutMs: number;
  readonly requireReviewPassBeforeDeploy: boolean;
  readonly requireSecurityPassBeforeDeploy: boolean;
}

const FIVE_MINUTES_MS = 5 * 60 * 1000;

/** Factory helpers for {@link AgentSwarmConfig}. Mirrors the C# static members. */
export const AgentSwarmConfig = {
  /** Production-safe defaults: 4 concurrent, 5-minute timeout, both gates on. */
  default(): AgentSwarmConfig {
    return {
      maxConcurrency: 4,
      taskTimeoutMs: FIVE_MINUTES_MS,
      requireReviewPassBeforeDeploy: true,
      requireSecurityPassBeforeDeploy: true,
    };
  },

  /**
   * Device-aware defaults: `maxConcurrency` sized via
   * {@link DeviceTierDefaults.maxConcurrency} against the supplied probe;
   * everything else matches {@link AgentSwarmConfig.default}.
   */
  forDevice(probe: DeviceProbe): AgentSwarmConfig {
    return {
      maxConcurrency: DeviceTierDefaults.maxConcurrency(classify(probe), probe.cpuCores),
      taskTimeoutMs: FIVE_MINUTES_MS,
      requireReviewPassBeforeDeploy: true,
      requireSecurityPassBeforeDeploy: true,
    };
  },
} as const;

// ─────────────────────────────────────────────────────────────────────────────
// IAgentDispatcher
// ─────────────────────────────────────────────────────────────────────────────

/** Routes agent tasks to handlers and evaluates quality gates. Mirrors C# `IAgentDispatcher`. */
export interface IAgentDispatcher {
  dispatchAsync(task: AgentTask, signal?: AbortSignal): Promise<SwarmResult>;
  runQualityGateAsync(result: SwarmResult, signal?: AbortSignal): Promise<QualityGateResult>;
}

/** Async agent handler delegate. Mirrors C# `Func<AgentTask, CancellationToken, Task<SwarmResult>>`. */
export type AgentHandler = (task: AgentTask, signal?: AbortSignal) => Promise<SwarmResult>;

/**
 * In-process agent dispatcher. Routes tasks to handler delegates registered per
 * {@link AgentRole}. Mirrors C# `LocalAgentDispatcher`. No external calls.
 */
export class LocalAgentDispatcher implements IAgentDispatcher {
  private readonly handlers = new Map<AgentRole, AgentHandler>();
  private disposed = false;

  /** Registers (replacing) an async handler for the given role. */
  registerHandler(role: AgentRole, handler: AgentHandler): void {
    if (handler == null) throw new Error("handler required");
    this.handlers.set(role, handler);
  }

  async dispatchAsync(task: AgentTask, signal?: AbortSignal): Promise<SwarmResult> {
    if (this.disposed) throw new Error("LocalAgentDispatcher is disposed");

    const handler = this.handlers.get(task.role);
    if (handler !== undefined) return handler(task, signal);

    // No handler registered — surface a blocked result with an actionable message.
    return swarmResult(
      task.id,
      task.role,
      AgentStatus.Blocked,
      `No handler registered for role ${AgentRole[task.role]}.`,
      [`Register a handler for AgentRole.${AgentRole[task.role]} before dispatching.`],
      new Date(),
    );
  }

  /**
   * Deterministic gate: Object issue prefixed with `[CRITICAL]` or `[HIGH]`
   * (case-insensitive) is a blocker; every other issue is a warning.
   */
  runQualityGateAsync(result: SwarmResult, _signal?: AbortSignal): Promise<QualityGateResult> {
    const blockers = result.issues.filter(
      (i) =>
        i.toUpperCase().startsWith("[CRITICAL]") || i.toUpperCase().startsWith("[HIGH]"),
    );
    const warnings = result.issues.filter((i) => !blockers.includes(i));
    return Promise.resolve(qualityGateResult(blockers.length === 0, blockers, warnings));
  }

  /** Marks the dispatcher disposed; subsequent {@link dispatchAsync} calls throw. */
  dispose(): void {
    this.disposed = true;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// LokiOrchestrator
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Host-side orchestrator. Dispatches {@link AgentTask} items through an
 * {@link IAgentDispatcher}, enforces the quality gate, and streams results.
 * Mirrors C# `LokiOrchestrator`.
 *
 * Task execution is bounded by {@link AgentSwarmConfig.maxConcurrency}. A gate
 * failure re-emits the result as {@link AgentStatus.Blocked} with the gate's
 * blockers appended to `issues`.
 */
export class LokiOrchestrator {
  private readonly dispatcher: IAgentDispatcher;
  private readonly config: AgentSwarmConfig;

  constructor(dispatcher: IAgentDispatcher, config?: AgentSwarmConfig | null) {
    if (dispatcher == null) throw new Error("dispatcher required");
    this.dispatcher = dispatcher;
    this.config = config ?? AgentSwarmConfig.default();
  }

  /**
   * Runs a swarm concurrently up to `maxConcurrency`. Yields one result per
   * task in completion order (which, matching the C# awaited-in-submission-order
   * enumeration, is submission order).
   */
  async *runSwarmAsync(
    tasks: Iterable<AgentTask>,
    signal?: AbortSignal,
  ): AsyncGenerator<SwarmResult> {
    const pending = [...tasks];
    const running: Promise<SwarmResult>[] = [];

    // Simple counting semaphore: gate submission on available permits.
    let permits = Math.max(1, this.config.maxConcurrency);
    const waiters: Array<() => void> = [];
    const acquire = (): Promise<void> => {
      if (permits > 0) {
        permits--;
        return Promise.resolve();
      }
      return new Promise<void>((resolve) => waiters.push(resolve));
    };
    const release = (): void => {
      const next = waiters.shift();
      if (next !== undefined) next();
      else permits++;
    };

    for (const task of pending) {
      await acquire();
      running.push(this.runOneAsync(task, release, signal));
    }

    for (const runningTask of running) {
      const result = await runningTask;
      const gate = await this.dispatcher.runQualityGateAsync(result, signal);

      if (
        !gate.passed &&
        (this.config.requireReviewPassBeforeDeploy ||
          this.config.requireSecurityPassBeforeDeploy)
      ) {
        yield {
          ...result,
          status: AgentStatus.Blocked,
          issues: [...result.issues, ...gate.blockers],
        };
      } else {
        yield result;
      }
    }
  }

  private async runOneAsync(
    task: AgentTask,
    release: () => void,
    signal?: AbortSignal,
  ): Promise<SwarmResult> {
    // Link the outer signal with a per-task timeout.
    const timeoutController = new AbortController();
    const timer = setTimeout(() => timeoutController.abort(), this.config.taskTimeoutMs);
    const linked = anyAbort(signal, timeoutController.signal);
    try {
      return await this.dispatcher.dispatchAsync(task, linked);
    } catch (ex) {
      if (timeoutController.signal.aborted) {
        return swarmResult(
          task.id,
          task.role,
          AgentStatus.Failed,
          "Task timed out.",
          ["[HIGH] Task exceeded configured timeout."],
          new Date(),
        );
      }
      const e = ex as Error;
      const name = e?.constructor?.name ?? "Error";
      const message = e?.message ?? String(ex);
      return swarmResult(
        task.id,
        task.role,
        AgentStatus.Failed,
        `Dispatcher threw: ${name}: ${message}`,
        [`[HIGH] ${name}: ${message}`],
        new Date(),
      );
    } finally {
      clearTimeout(timer);
      release();
    }
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

// ─────────────────────────────────────────────────────────────────────────────
// IncidentTrigger
// ─────────────────────────────────────────────────────────────────────────────

const CRASH_TAGS = new Set(["crash", "exception", "unhandled_error", "oom", "null_reference"]);
const SECURITY_TAGS = new Set([
  "auth_failure",
  "permission_denied",
  "token_expired",
  "injection",
  "overflow",
]);

const HIGH_SEVERITY_VECTORS = new Set<ThreatVector>([
  ThreatVector.ControlFlowDrift,
  ThreatVector.PrivilegeEscalation,
  ThreatVector.NetworkPivot,
  ThreatVector.StateCorruption,
]);

/**
 * Maps a recorded {@link EpisodicMemoryEntry} or {@link AnomalySignal} to the
 * agent tasks that should be triggered. Mirrors C# `IncidentTrigger`.
 */
export const IncidentTrigger = {
  /**
   * Inspects an episodic memory entry and returns the tasks to trigger. Empty
   * when the entry is not an incident. Always includes an Operations task on a
   * crash tag; additionally a Security task when a security tag is present.
   */
  fromMemoryEntry(entry: EpisodicMemoryEntry): readonly AgentTask[] {
    if (entry == null) throw new Error("entry required");

    const tags = entry.tags ?? {};
    const tagKeys = Object.keys(tags);
    const isCrash = tagKeys.some((k) => CRASH_TAGS.has(k.toLowerCase()));
    if (!isCrash) return [];

    const tasks: AgentTask[] = [];

    tasks.push(
      createAgentTask(
        AgentRole.Operations,
        `ops-incident: diagnose crash recorded at ${toIso(entry.recordedAtUtc)}`,
        AgentPriority.High,
        new Map<string, string>([
          ["episode_id", entry.id],
          ["user_text", entry.userText],
          ["assistant_text", entry.assistantText],
          ["app_context", entry.appContext ?? ""],
        ]),
      ),
    );

    const isSecurity = tagKeys.some((k) => SECURITY_TAGS.has(k.toLowerCase()));
    if (isSecurity) {
      tasks.push(
        createAgentTask(
          AgentRole.Security,
          `ops-security: investigate security incident from episode ${entry.id}`,
          AgentPriority.Critical,
          new Map<string, string>([
            ["episode_id", entry.id],
            ["app_context", entry.appContext ?? ""],
            ["tags", tagKeys.join(",")],
          ]),
        ),
      );
    }

    return tasks;
  },

  /**
   * Maps a confirmed {@link AnomalySignal} to an ops-security task, or `null`
   * when confidence is below `dispatchThreshold` (default 0.30). Confidence
   * drives priority; high-severity vectors bump one rank (lower numeric value).
   */
  fromAnomalySignal(signal: AnomalySignal, dispatchThreshold = 0.3): AgentTask | null {
    if (signal == null) throw new Error("signal required");
    if (signal.confidence < dispatchThreshold) return null;

    let priority: AgentPriority;
    if (signal.confidence >= 0.85) priority = AgentPriority.Critical;
    else if (signal.confidence >= 0.6) priority = AgentPriority.High;
    else priority = AgentPriority.Normal;

    const isHighSeverityVector = HIGH_SEVERITY_VECTORS.has(signal.vector);
    // priority ordering: Critical=0 < High=1 < Normal=2 < Low=3; bump = decrease.
    if (isHighSeverityVector && priority > AgentPriority.Critical) {
      priority = Math.max(AgentPriority.Critical, priority - 1);
    }

    const inputs = new Map<string, string>(Object.entries(signal.evidence));
    inputs.set("signal_id", signal.id);
    inputs.set("vector", ThreatVector[signal.vector]);
    inputs.set("confidence", signal.confidence.toFixed(3));
    inputs.set("affected_module", signal.affectedModule);
    inputs.set("description", signal.description);
    inputs.set("detected_at", toIso(signal.detectedAt));

    return createAgentTask(
      AgentRole.Security,
      `ops-security: anomaly ${ThreatVector[signal.vector]} in ${signal.affectedModule} ` +
        `(confidence ${Math.round(signal.confidence * 100)}%)`,
      priority,
      inputs,
    );
  },
} as const;

function toIso(d: Date): string {
  return d.toISOString();
}

// ─────────────────────────────────────────────────────────────────────────────
// SecurityOrchestrationBridge
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Wraps an {@link ISecurityWatchdog} so every anomaly signal also dispatches an
 * ops-security {@link AgentTask} to a {@link LokiOrchestrator}. Runtime response
 * and agent dispatch proceed in parallel; neither blocks the other. Mirrors C#
 * `SecurityOrchestrationBridge`.
 */
export class SecurityOrchestrationBridge implements ISecurityWatchdog {
  readonly componentName = "SecurityOrchestrationBridge";
  private readonly inner: ISecurityWatchdog;
  private readonly orchestrator: LokiOrchestrator;
  private readonly dispatchThreshold: number;

  constructor(
    inner: ISecurityWatchdog,
    orchestrator: LokiOrchestrator,
    dispatchThreshold = 0.3,
  ) {
    if (inner == null) throw new Error("inner required");
    if (orchestrator == null) throw new Error("orchestrator required");
    this.inner = inner;
    this.orchestrator = orchestrator;
    this.dispatchThreshold = dispatchThreshold;
  }

  async onAnomalyDetectedAsync(
    signal: AnomalySignal,
    checkpoint?: SecurityCheckpoint | null,
    abortSignal?: AbortSignal,
  ): Promise<SecurityResponse> {
    if (signal == null) throw new Error("signal required");

    // Run the immediate immune-system response and the agent dispatch in
    // parallel. The runtime response MUST NOT wait on the agent swarm.
    const watchdogTask = this.inner.onAnomalyDetectedAsync(signal, checkpoint, abortSignal);
    const agentTask = this.dispatchAgentAsync(signal, abortSignal);

    // Await the watchdog so the caller gets the runtime response immediately.
    const response = await watchdogTask;

    // Fire-and-forget on the agent path — observe completion only to swallow
    // exceptions so agent failures never crash the runtime.
    void agentTask.catch(() => {
      /* intentional: agent failures must not crash the runtime */
    });

    return response;
  }

  streamSignalsAsync(abortSignal?: AbortSignal): AsyncGenerator<AnomalySignal> {
    return this.inner.streamSignalsAsync(abortSignal);
  }

  private async dispatchAgentAsync(signal: AnomalySignal, abortSignal?: AbortSignal): Promise<void> {
    const task = IncidentTrigger.fromAnomalySignal(signal, this.dispatchThreshold);
    if (task === null) return;

    // Drain the swarm enumerator — typically a single task → single result.
    for await (const _ of this.orchestrator.runSwarmAsync([task], abortSignal)) {
      // Results are observable through orchestrator subscriptions on the host side.
    }
  }
}
