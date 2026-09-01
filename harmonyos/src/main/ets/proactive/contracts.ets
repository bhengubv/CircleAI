/**
 * proactive/contracts
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// proactive/contracts.ts
//
// Contracts.cs — the proactive scheduling contract surface. Three interfaces
// split cleanly so consumers can replace one without touching the others:
//
//   IProactiveTaskSource   — where do tasks come from?
//   IProactiveTaskRunner   — how do we execute one?
//   IProactiveScheduler    — when do they fire? (the substrate ships this)
//
// C# uses ValueTask/Task + CancellationToken; TypeScript models both as Promise
// with an optional AbortSignal. IDictionary<string,string> becomes a
// ReadonlyMap<string,string>.

import type { ProactiveTask, ProactiveTaskRunResult, ProactiveTaskLoadError } from "./primitives";

/**
 * Where the active set of tasks comes from. Refreshed via `getTasksAsync` on
 * every scheduler refresh / tick. Mirrors IProactiveTaskSource.
 */
export interface IProactiveTaskSource {
  /** Backend self-identification — "vault-fs", "in-memory", "null". */
  readonly backendId: string;
  /** Snapshot the current set of tasks. */
  getTasksAsync(signal?: AbortSignal): Promise<readonly ProactiveTask[]>;
  /** Any parse / load failures surfaced from the last refresh. */
  getErrorsAsync(signal?: AbortSignal): Promise<readonly ProactiveTaskLoadError[]>;
}

/**
 * Executes one task. The substrate hands the task back; the consumer reads
 * `task.payload` and runs it. Mirrors IProactiveTaskRunner.
 */
export interface IProactiveTaskRunner {
  /** Backend self-identification — "workflow-engine", "delegate", "null". */
  readonly backendId: string;
  /**
   * Execute one task. `variables` carry trigger-time context (event payload,
   * manual-invoke args, …) the runner can substitute into prompts or pass through.
   */
  runAsync(
    task: ProactiveTask,
    variables?: ReadonlyMap<string, string> | null,
    signal?: AbortSignal,
  ): Promise<ProactiveTaskRunResult>;
}

/**
 * The scheduling loop. Owns cron parsing + last-run tracking + event dispatch.
 * Ticked once a minute by the background service. Mirrors IProactiveScheduler.
 */
export interface IProactiveScheduler {
  /** Backend self-identification. */
  readonly backendId: string;
  /** Current snapshot — populated by `refreshAsync`. */
  readonly tasks: readonly ProactiveTask[];
  /** Any load errors from the source. */
  readonly loadErrors: readonly ProactiveTaskLoadError[];

  /**
   * Next cron firing for a task. Returns null for non-cron triggers or
   * unparseable expressions.
   */
  getNextRun(task: ProactiveTask, after: Date): Date | null;

  /**
   * Re-snapshot tasks from the source. Drops state for tasks the source no
   * longer reports; leaves last-run state for surviving tasks intact.
   */
  refreshAsync(signal?: AbortSignal): Promise<void>;

  /**
   * Tick. Run every task whose cron next-run is at-or-before `now` and that
   * hasn't already fired for the matching minute.
   */
  tickAsync(now: Date, signal?: AbortSignal): Promise<void>;

  /** Fire every event-triggered task matching the event name. */
  dispatchEventAsync(
    eventName: string,
    variables?: ReadonlyMap<string, string> | null,
    signal?: AbortSignal,
  ): Promise<void>;

  /** One-shot manual run by task id. */
  runByIdAsync(
    id: string,
    variables?: ReadonlyMap<string, string> | null,
    signal?: AbortSignal,
  ): Promise<ProactiveTaskRunResult>;
}
