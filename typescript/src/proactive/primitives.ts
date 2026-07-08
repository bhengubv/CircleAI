// proactive/primitives.ts
//
// Primitives.cs — shared shapes for the proactive scheduling surface. A
// ProactiveTask is opaque to the substrate; its `payload` is whatever the
// consumer's IProactiveTaskRunner knows how to execute.

/**
 * How a task fires. Exactly one of `cron`, `onEvent`, or `manual` is set.
 * Mirrors CircleAI.Companion.Proactive.ProactiveTrigger.
 */
export interface ProactiveTrigger {
  /** 5-field cron expression — see CronExpression. */
  readonly cron?: string | null;
  /** Event name (e.g. "note-saved", "task-created"). */
  readonly onEvent?: string | null;
  /** True if the task only fires when explicitly invoked. */
  readonly manual?: boolean;
}

/**
 * One scheduled task. Opaque from the substrate's perspective — the host's
 * IProactiveTaskRunner reads `payload` and executes it.
 * Mirrors CircleAI.Companion.Proactive.ProactiveTask.
 */
export interface ProactiveTask {
  /** Unique task id within its source. Used for last-run tracking. */
  readonly id: string;
  /** Cron / event / manual trigger. */
  readonly trigger: ProactiveTrigger;
  /** Consumer-owned object. The substrate never inspects it. */
  readonly payload: unknown;
  /** Optional context tag (vault path, tenant id, …) for per-context last-run state. */
  readonly sourceContext?: string | null;
}

/** One run outcome — success or failure with a message. */
export interface ProactiveTaskRunResult {
  readonly taskId: string;
  readonly success: boolean;
  readonly failureMessage?: string | null;
}

/** One parse failure surfaced through the source. */
export interface ProactiveTaskLoadError {
  readonly taskId: string;
  readonly message: string;
  readonly sourceContext?: string | null;
}

// Constructor helpers so callers can build these positionally, mirroring the
// C# record constructors (with the same defaults).

export function proactiveTrigger(
  cron: string | null = null,
  onEvent: string | null = null,
  manual = false,
): ProactiveTrigger {
  return { cron, onEvent, manual };
}

export function proactiveTask(
  id: string,
  trigger: ProactiveTrigger,
  payload: unknown,
  sourceContext: string | null = null,
): ProactiveTask {
  return { id, trigger, payload, sourceContext };
}

export function proactiveTaskRunResult(
  taskId: string,
  success: boolean,
  failureMessage: string | null = null,
): ProactiveTaskRunResult {
  return { taskId, success, failureMessage };
}

export function proactiveTaskLoadError(
  taskId: string,
  message: string,
  sourceContext: string | null = null,
): ProactiveTaskLoadError {
  return { taskId, message, sourceContext };
}
